using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KspContinuum;

static class Program
{
    const string Schema = "ksp-continuum-layout-bench/v1";
    const double StepSeconds = 0.02;
    const double Coupling = 0.125;
    const int BlockWidth = 8;
    const int WarmupSamples = 10;
    const int MaxSamples = 1000;
    static readonly int[] AllowedBodyCounts = { 32, 1024, 4096 };
    static readonly string[] Strategies = { "object-aos", "soa", "aosoa-8" };
    static readonly string[] Workloads = { "free-body", "neighbor-chain" };

    static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var environment = new EnvironmentReport {
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription,
                LogicalProcessors = Environment.ProcessorCount
            };
            var configuration = new ConfigurationReport {
                BodyCounts = options.BodyCounts,
                Samples = options.Samples,
                WarmupSamples = WarmupSamples,
                Seed = options.Seed,
                StepSeconds = StepSeconds,
                AosoaBlockWidth = BlockWidth,
                Strategies = Strategies,
                Workloads = Workloads
            };
            var report = new Report {
                Configuration = configuration,
                Environment = environment,
                ConfigurationSha256 = HashText(configuration.Canonical()),
                EnvironmentSha256 = HashText(environment.Canonical()),
            };
            foreach (int bodyCount in options.BodyCounts)
                report.Cases.Add(MeasureCase(bodyCount, options.Samples, options.Seed));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("layout-bench: " + error.Message);
            return 1;
        }
    }

    static CaseReport MeasureCase(int bodyCount, int samples, int seed)
    {
        BodySeed[] source = Fixture(bodyCount);
        string fixtureSha256 = HashFixture(source);
        var expected = Workloads.ToDictionary(workload => workload,
            workload => Expected(source, workload), StringComparer.Ordinal);
        var results = new Dictionary<string, ResultReport>(StringComparer.Ordinal);
        var orders = new List<OrderReport>();
        foreach (string workload in Workloads)
        {
            foreach (string strategy in Strategies)
                results.Add(workload + "\n" + strategy, new ResultReport(workload, strategy));
            var random = new StableRandom(unchecked((uint)seed ^ (uint)bodyCount ^ HashSeed(workload)));
            for (int sample = -WarmupSamples; sample < samples; sample++)
            {
                string[] order = (string[])Strategies.Clone();
                random.Shuffle(order);
                if (sample >= 0)
                {
                    OrderReport group = orders.FirstOrDefault(item => item.Workload == workload);
                    if (group == null) { group = new OrderReport { Workload = workload }; orders.Add(group); }
                    group.Orders.Add(order);
                }
                foreach (string strategy in order)
                {
                    Measurement measured = Execute(source, expected[workload], workload, strategy);
                    if (sample >= 0) results[workload + "\n" + strategy].Add(measured);
                }
            }
        }
        var report = new CaseReport {
            Bodies = bodyCount,
            FixtureSha256 = fixtureSha256,
            MeasuredStrategyOrders = orders,
            Results = results.Values.OrderBy(result => result.Workload, StringComparer.Ordinal)
                .ThenBy(result => result.Strategy, StringComparer.Ordinal).ToList()
        };
        foreach (ResultReport result in report.Results)
            result.Finish(bodyCount, fixtureSha256);
        return report;
    }

    static Measurement Execute(BodySeed[] source, StateValue[] expected, string workload, string strategy)
    {
        long totalAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();

        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        BodySeed[] captured = (BodySeed[])source.Clone();
        double captureMilliseconds = Milliseconds(start, Stopwatch.GetTimestamp());
        long captureAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        ICapture capture = Pack(captured, strategy);
        double packingMilliseconds = Milliseconds(start, Stopwatch.GetTimestamp());
        long packingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        IResult result = capture.Compute(workload);
        double kernelMilliseconds = Milliseconds(start, Stopwatch.GetTimestamp());
        long kernelAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        IResult synchronized = result;
        GC.KeepAlive(synchronized);
        double synchronizeMilliseconds = Milliseconds(start, Stopwatch.GetTimestamp());
        long synchronizeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        Evidence evidence = Consume(synchronized, expected);
        double consumeMilliseconds = Milliseconds(start, Stopwatch.GetTimestamp());
        long consumeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        double endToEndMilliseconds = Milliseconds(totalStart, Stopwatch.GetTimestamp());
        long endToEndAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - totalAllocationStart;
        return new Measurement {
            CaptureMilliseconds = captureMilliseconds,
            PackingMilliseconds = packingMilliseconds,
            KernelMilliseconds = kernelMilliseconds,
            SynchronizeMilliseconds = synchronizeMilliseconds,
            ConsumeMilliseconds = consumeMilliseconds,
            EndToEndMilliseconds = endToEndMilliseconds,
            CaptureAllocatedBytes = captureAllocatedBytes,
            PackingAllocatedBytes = packingAllocatedBytes,
            KernelAllocatedBytes = kernelAllocatedBytes,
            SynchronizeAllocatedBytes = synchronizeAllocatedBytes,
            ConsumeAllocatedBytes = consumeAllocatedBytes,
            EndToEndAllocatedBytes = endToEndAllocatedBytes,
            Evidence = evidence
        };
    }

    static ICapture Pack(BodySeed[] source, string strategy)
    {
        if (strategy == "object-aos") return new ObjectCapture(source);
        if (strategy == "soa") return new SoaCapture(source);
        if (strategy == "aosoa-8") return new AosoaCapture(source);
        throw new InvalidOperationException("Unknown strategy.");
    }

    static Evidence Consume(IResult result, StateValue[] expected)
    {
        if (result.Count != expected.Length) throw new InvalidOperationException("Result count changed.");
        double positionError = 0, velocityError = 0;
        using var bytes = new MemoryStream(result.Count * 64);
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            for (int i = 0; i < result.Count; i++)
            {
                StateValue actual = result.Read(i);
                StateValue wanted = expected[i];
                if (actual.Id != wanted.Id) throw new InvalidOperationException("Result body order or identity changed.");
                positionError = Math.Max(positionError, MaxError(actual.Px, wanted.Px, actual.Py, wanted.Py, actual.Pz, wanted.Pz));
                velocityError = Math.Max(velocityError, MaxError(actual.Vx, wanted.Vx, actual.Vy, wanted.Vy, actual.Vz, wanted.Vz));
                writer.Write(actual.Id);
                writer.Write(actual.Px); writer.Write(actual.Py); writer.Write(actual.Pz);
                writer.Write(actual.Vx); writer.Write(actual.Vy); writer.Write(actual.Vz);
            }
        }
        return new Evidence {
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))).ToLowerInvariant(),
            PositionError = positionError,
            VelocityError = velocityError
        };
    }

    static double MaxError(double ax, double bx, double ay, double by, double az, double bz)
    {
        return Math.Max(Math.Abs(ax - bx), Math.Max(Math.Abs(ay - by), Math.Abs(az - bz)));
    }

    static StateValue[] Expected(BodySeed[] source, string workload)
    {
        var result = new StateValue[source.Length];
        bool neighbors = workload == "neighbor-chain";
        for (int i = 0; i < source.Length; i++)
        {
            BodySeed body = source[i];
            BodySeed left = source[i == 0 ? i : i - 1];
            BodySeed right = source[i == source.Length - 1 ? i : i + 1];
            double ax = Acceleration(body.Fx, body.Mass, body.Px, left.Px, right.Px, neighbors);
            double ay = Acceleration(body.Fy, body.Mass, body.Py, left.Py, right.Py, neighbors);
            double az = Acceleration(body.Fz, body.Mass, body.Pz, left.Pz, right.Pz, neighbors);
            result[i] = Integrate(body.Id, body.Px, body.Py, body.Pz, body.Vx, body.Vy, body.Vz, ax, ay, az);
        }
        return result;
    }

    static StateValue Integrate(int id, double px, double py, double pz, double vx, double vy, double vz,
        double ax, double ay, double az)
    {
        double halfDtSquared = 0.5 * StepSeconds * StepSeconds;
        return new StateValue(id,
            px + vx * StepSeconds + ax * halfDtSquared,
            py + vy * StepSeconds + ay * halfDtSquared,
            pz + vz * StepSeconds + az * halfDtSquared,
            vx + ax * StepSeconds, vy + ay * StepSeconds, vz + az * StepSeconds);
    }

    static double Acceleration(double force, double mass, double current, double left, double right, bool neighbors)
    {
        double value = force / mass;
        return neighbors ? value + (left + right - 2 * current) * Coupling : value;
    }

    static BodySeed[] Fixture(int count)
    {
        var result = new BodySeed[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new BodySeed(i, 1 + (i % 17) * 0.125,
                i * 0.25, (i % 13) * 0.1, (i % 7) * -0.2,
                1 + (i % 5) * 0.01, -2 + (i % 3) * 0.02, 0.5 - (i % 11) * 0.01,
                (i % 19) * 0.1 - 0.9, (i % 23) * -0.05 + 0.55, (i % 29) * 0.025 - 0.35);
        }
        return result;
    }

    static void ValidateSource(BodySeed[] source)
    {
        var ids = new HashSet<int>();
        foreach (BodySeed body in source)
        {
            if (body.Id < 0 || !ids.Add(body.Id) || !(body.Mass > 0) ||
                !Finite(body.Mass) || !Finite(body.Px) || !Finite(body.Py) || !Finite(body.Pz) ||
                !Finite(body.Vx) || !Finite(body.Vy) || !Finite(body.Vz) ||
                !Finite(body.Fx) || !Finite(body.Fy) || !Finite(body.Fz))
                throw new ArgumentException("Logical capture contains an invalid or duplicate body.");
        }
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    static string HashFixture(BodySeed[] source)
    {
        using var bytes = new MemoryStream(source.Length * 88);
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            foreach (BodySeed body in source)
            {
                writer.Write(body.Id); writer.Write(body.Mass);
                writer.Write(body.Px); writer.Write(body.Py); writer.Write(body.Pz);
                writer.Write(body.Vx); writer.Write(body.Vy); writer.Write(body.Vz);
                writer.Write(body.Fx); writer.Write(body.Fy); writer.Write(body.Fz);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))).ToLowerInvariant();
    }

    static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    static uint HashSeed(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value) hash = (hash ^ character) * 16777619;
        return hash;
    }

    static double Milliseconds(long start, long end)
    {
        return (end - start) * 1000.0 / Stopwatch.Frequency;
    }

    readonly struct BodySeed
    {
        public BodySeed(int id, double mass, double px, double py, double pz, double vx, double vy, double vz,
            double fx, double fy, double fz)
        {
            Id = id; Mass = mass; Px = px; Py = py; Pz = pz; Vx = vx; Vy = vy; Vz = vz; Fx = fx; Fy = fy; Fz = fz;
        }
        public readonly int Id;
        public readonly double Mass, Px, Py, Pz, Vx, Vy, Vz, Fx, Fy, Fz;
    }

    readonly struct StateValue
    {
        public StateValue(int id, double px, double py, double pz, double vx, double vy, double vz)
        { Id = id; Px = px; Py = py; Pz = pz; Vx = vx; Vy = vy; Vz = vz; }
        public readonly int Id;
        public readonly double Px, Py, Pz, Vx, Vy, Vz;
    }

    interface ICapture { IResult Compute(string workload); }
    interface IResult { int Count { get; } StateValue Read(int index); }

    sealed class ObjectCapture : ICapture
    {
        readonly SimulationBatch batch;
        public ObjectCapture(BodySeed[] source)
        {
            var bodies = new SimulationBody[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                BodySeed b = source[i];
                bodies[i] = new SimulationBody(b.Id, b.Mass, new Vec(b.Px, b.Py, b.Pz),
                    new Vec(b.Vx, b.Vy, b.Vz), new Vec(b.Fx, b.Fy, b.Fz));
            }
            batch = new SimulationBatch(new WorkStamp(0, 0, 0), StepSeconds, bodies);
        }
        public IResult Compute(string workload)
        {
            bool neighbors = workload == "neighbor-chain";
            var bodies = new SimulationBody[batch.Bodies.Count];
            for (int i = 0; i < bodies.Length; i++)
            {
                SimulationBody body = batch.Bodies[i];
                SimulationBody left = batch.Bodies[i == 0 ? i : i - 1];
                SimulationBody right = batch.Bodies[i == bodies.Length - 1 ? i : i + 1];
                double ax = Acceleration(body.Force.X, body.Mass, body.Position.X, left.Position.X, right.Position.X, neighbors);
                double ay = Acceleration(body.Force.Y, body.Mass, body.Position.Y, left.Position.Y, right.Position.Y, neighbors);
                double az = Acceleration(body.Force.Z, body.Mass, body.Position.Z, left.Position.Z, right.Position.Z, neighbors);
                StateValue state = Integrate(body.Id, body.Position.X, body.Position.Y, body.Position.Z,
                    body.Velocity.X, body.Velocity.Y, body.Velocity.Z, ax, ay, az);
                bodies[i] = new SimulationBody(state.Id, body.Mass, new Vec(state.Px, state.Py, state.Pz),
                    new Vec(state.Vx, state.Vy, state.Vz), body.Force);
            }
            return new ObjectResult(new SimulationBatch(batch.Stamp, StepSeconds, bodies));
        }
    }

    sealed class ObjectResult : IResult
    {
        readonly SimulationBatch batch;
        public ObjectResult(SimulationBatch batch) { this.batch = batch; }
        public int Count => batch.Bodies.Count;
        public StateValue Read(int index)
        {
            SimulationBody body = batch.Bodies[index];
            return new StateValue(body.Id, body.Position.X, body.Position.Y, body.Position.Z,
                body.Velocity.X, body.Velocity.Y, body.Velocity.Z);
        }
    }

    sealed class SoaCapture : ICapture
    {
        readonly int[] ids;
        readonly double[] mass, px, py, pz, vx, vy, vz, fx, fy, fz;
        public SoaCapture(BodySeed[] source)
        {
            ValidateSource(source);
            ids = new int[source.Length]; mass = new double[source.Length];
            px = new double[source.Length]; py = new double[source.Length]; pz = new double[source.Length];
            vx = new double[source.Length]; vy = new double[source.Length]; vz = new double[source.Length];
            fx = new double[source.Length]; fy = new double[source.Length]; fz = new double[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                BodySeed b = source[i]; ids[i] = b.Id; mass[i] = b.Mass;
                px[i] = b.Px; py[i] = b.Py; pz[i] = b.Pz; vx[i] = b.Vx; vy[i] = b.Vy; vz[i] = b.Vz;
                fx[i] = b.Fx; fy[i] = b.Fy; fz[i] = b.Fz;
            }
        }
        public IResult Compute(string workload)
        {
            bool neighbors = workload == "neighbor-chain";
            var output = new SoaResult(ids);
            for (int i = 0; i < ids.Length; i++)
            {
                int left = i == 0 ? i : i - 1, right = i == ids.Length - 1 ? i : i + 1;
                double ax = Acceleration(fx[i], mass[i], px[i], px[left], px[right], neighbors);
                double ay = Acceleration(fy[i], mass[i], py[i], py[left], py[right], neighbors);
                double az = Acceleration(fz[i], mass[i], pz[i], pz[left], pz[right], neighbors);
                output.Set(i, Integrate(ids[i], px[i], py[i], pz[i], vx[i], vy[i], vz[i], ax, ay, az));
            }
            return output;
        }
    }

    sealed class SoaResult : IResult
    {
        readonly int[] ids;
        readonly double[] px, py, pz, vx, vy, vz;
        public SoaResult(int[] sourceIds)
        {
            ids = (int[])sourceIds.Clone(); px = new double[ids.Length]; py = new double[ids.Length]; pz = new double[ids.Length];
            vx = new double[ids.Length]; vy = new double[ids.Length]; vz = new double[ids.Length];
        }
        public int Count => ids.Length;
        public void Set(int index, StateValue value)
        { px[index] = value.Px; py[index] = value.Py; pz[index] = value.Pz; vx[index] = value.Vx; vy[index] = value.Vy; vz[index] = value.Vz; }
        public StateValue Read(int index) => new StateValue(ids[index], px[index], py[index], pz[index], vx[index], vy[index], vz[index]);
    }

    sealed class AosoaCapture : ICapture
    {
        const int FieldCount = 10;
        readonly int count;
        readonly int[] ids;
        readonly double[] blocks;
        public AosoaCapture(BodySeed[] source)
        {
            ValidateSource(source);
            count = source.Length; ids = new int[count]; blocks = new double[((count + BlockWidth - 1) / BlockWidth) * BlockWidth * FieldCount];
            for (int i = 0; i < count; i++)
            {
                BodySeed b = source[i]; ids[i] = b.Id;
                Set(i, 0, b.Mass); Set(i, 1, b.Px); Set(i, 2, b.Py); Set(i, 3, b.Pz);
                Set(i, 4, b.Vx); Set(i, 5, b.Vy); Set(i, 6, b.Vz);
                Set(i, 7, b.Fx); Set(i, 8, b.Fy); Set(i, 9, b.Fz);
            }
        }
        int Offset(int index, int field) => (index / BlockWidth) * FieldCount * BlockWidth + field * BlockWidth + index % BlockWidth;
        double Get(int index, int field) => blocks[Offset(index, field)];
        void Set(int index, int field, double value) => blocks[Offset(index, field)] = value;
        public IResult Compute(string workload)
        {
            bool neighbors = workload == "neighbor-chain";
            var output = new AosoaResult(ids);
            for (int i = 0; i < count; i++)
            {
                int left = i == 0 ? i : i - 1, right = i == count - 1 ? i : i + 1;
                double ax = Acceleration(Get(i, 7), Get(i, 0), Get(i, 1), Get(left, 1), Get(right, 1), neighbors);
                double ay = Acceleration(Get(i, 8), Get(i, 0), Get(i, 2), Get(left, 2), Get(right, 2), neighbors);
                double az = Acceleration(Get(i, 9), Get(i, 0), Get(i, 3), Get(left, 3), Get(right, 3), neighbors);
                output.Set(i, Integrate(ids[i], Get(i, 1), Get(i, 2), Get(i, 3), Get(i, 4), Get(i, 5), Get(i, 6), ax, ay, az));
            }
            return output;
        }
    }

    sealed class AosoaResult : IResult
    {
        const int FieldCount = 6;
        readonly int[] ids;
        readonly double[] blocks;
        public AosoaResult(int[] sourceIds)
        {
            ids = (int[])sourceIds.Clone(); blocks = new double[((ids.Length + BlockWidth - 1) / BlockWidth) * BlockWidth * FieldCount];
        }
        int Offset(int index, int field) => (index / BlockWidth) * FieldCount * BlockWidth + field * BlockWidth + index % BlockWidth;
        public int Count => ids.Length;
        public void Set(int index, StateValue value)
        {
            blocks[Offset(index, 0)] = value.Px; blocks[Offset(index, 1)] = value.Py; blocks[Offset(index, 2)] = value.Pz;
            blocks[Offset(index, 3)] = value.Vx; blocks[Offset(index, 4)] = value.Vy; blocks[Offset(index, 5)] = value.Vz;
        }
        public StateValue Read(int index) => new StateValue(ids[index], blocks[Offset(index, 0)], blocks[Offset(index, 1)],
            blocks[Offset(index, 2)], blocks[Offset(index, 3)], blocks[Offset(index, 4)], blocks[Offset(index, 5)]);
    }

    sealed class StableRandom
    {
        uint state;
        public StableRandom(uint seed) { state = seed == 0 ? 0x9e3779b9u : seed; }
        uint Next()
        {
            uint value = state;
            value ^= value << 13; value ^= value >> 17; value ^= value << 5;
            state = value; return value;
        }
        public void Shuffle(string[] values)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int other = (int)(Next() % (uint)(i + 1));
                (values[i], values[other]) = (values[other], values[i]);
            }
        }
    }

    sealed class Options
    {
        public int[] BodyCounts { get; private set; } = (int[])AllowedBodyCounts.Clone();
        public int Samples { get; private set; } = 100;
        public int Seed { get; private set; } = 20260921;
        public static Options Parse(string[] args)
        {
            var result = new Options();
            if (args.Length % 2 != 0) throw new ArgumentException("Options require values: --bodies, --samples, --seed.");
            for (int i = 0; i < args.Length; i += 2)
            {
                if (args[i] == "--bodies")
                {
                    int[] values;
                    try { values = args[i + 1].Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray(); }
                    catch (FormatException) { throw new ArgumentException("Bodies must be a comma-separated subset of 32,1024,4096."); }
                    catch (OverflowException) { throw new ArgumentException("Bodies must be a comma-separated subset of 32,1024,4096."); }
                    if (values.Length == 0 || values.Distinct().Count() != values.Length || values.Any(value => !AllowedBodyCounts.Contains(value)))
                        throw new ArgumentException("Bodies must be a unique comma-separated subset of 32,1024,4096.");
                    Array.Sort(values); result.BodyCounts = values;
                }
                else if (args[i] == "--samples")
                {
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > MaxSamples)
                        throw new ArgumentException("Samples must be between 1 and " + MaxSamples + ".");
                    result.Samples = value;
                }
                else if (args[i] == "--seed")
                {
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 0)
                        throw new ArgumentException("Seed must be a nonnegative 32-bit integer.");
                    result.Seed = value;
                }
                else throw new ArgumentException("Unknown option " + args[i] + ".");
            }
            return result;
        }
    }

    sealed class Measurement
    {
        public double CaptureMilliseconds, PackingMilliseconds, KernelMilliseconds, SynchronizeMilliseconds, ConsumeMilliseconds, EndToEndMilliseconds;
        public long CaptureAllocatedBytes, PackingAllocatedBytes, KernelAllocatedBytes, SynchronizeAllocatedBytes, ConsumeAllocatedBytes, EndToEndAllocatedBytes;
        public Evidence Evidence = null!;
    }
    sealed class Evidence { public string Sha256 = ""; public double PositionError, VelocityError; }
    sealed class Report
    {
        public string Schema { get; } = Program.Schema;
        public string Status { get; } = "synthetic-layout-experiment-only";
        public string Measurement { get; } = "Current-thread allocation and directly measured capture-through-publication latency; synthetic kernels, randomized strategy order.";
        public bool ImmutableLogicalCaptures { get; } = true;
        public bool PoolingUsed { get; } = false;
        public bool StockPhysicsSpeedupMeasured { get; } = false;
        public ConfigurationReport Configuration { get; set; } = null!;
        public EnvironmentReport Environment { get; set; } = null!;
        public string ConfigurationSha256 { get; set; } = "";
        public string EnvironmentSha256 { get; set; } = "";
        public List<CaseReport> Cases { get; } = new List<CaseReport>();
    }
    sealed class ConfigurationReport
    {
        public int[] BodyCounts { get; set; } = Array.Empty<int>();
        public int Samples { get; set; }
        public int WarmupSamples { get; set; }
        public int Seed { get; set; }
        public double StepSeconds { get; set; }
        public int AosoaBlockWidth { get; set; }
        public string[] Strategies { get; set; } = Array.Empty<string>();
        public string[] Workloads { get; set; } = Array.Empty<string>();
        public string Canonical() => string.Join("|", BodyCounts) + "|" + Samples + "|" + WarmupSamples + "|" + Seed + "|" +
            StepSeconds.ToString("R", CultureInfo.InvariantCulture) + "|" + AosoaBlockWidth + "|" + string.Join(",", Strategies) + "|" + string.Join(",", Workloads);
    }
    sealed class EnvironmentReport
    {
        public string Runtime { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public int LogicalProcessors { get; set; }
        public string Canonical() => Runtime + "|" + Architecture + "|" + OperatingSystem + "|" + LogicalProcessors;
    }
    sealed class CaseReport
    {
        public int Bodies { get; set; }
        public string FixtureSha256 { get; set; } = "";
        public List<OrderReport> MeasuredStrategyOrders { get; set; } = new List<OrderReport>();
        public List<ResultReport> Results { get; set; } = new List<ResultReport>();
    }
    sealed class OrderReport
    {
        public string Workload { get; set; } = "";
        public List<string[]> Orders { get; } = new List<string[]>();
    }
    sealed class ResultReport
    {
        public ResultReport(string workload, string strategy) { Workload = workload; Strategy = strategy; }
        public string Workload { get; }
        public string Strategy { get; }
        public PerformanceObservation Observation { get; private set; } = null!;
        public List<double> CaptureMilliseconds { get; } = new List<double>();
        public List<double> PackingMilliseconds { get; } = new List<double>();
        public List<double> KernelMilliseconds { get; } = new List<double>();
        public List<double> SynchronizeMilliseconds { get; } = new List<double>();
        public List<double> ConsumeMilliseconds { get; } = new List<double>();
        public List<double> EndToEndMilliseconds { get; } = new List<double>();
        public List<long> CaptureAllocatedBytes { get; } = new List<long>();
        public List<long> PackingAllocatedBytes { get; } = new List<long>();
        public List<long> KernelAllocatedBytes { get; } = new List<long>();
        public List<long> SynchronizeAllocatedBytes { get; } = new List<long>();
        public List<long> ConsumeAllocatedBytes { get; } = new List<long>();
        public List<long> EndToEndAllocatedBytes { get; } = new List<long>();
        public string OutputSha256 { get; private set; } = "";
        public double MaxPositionError { get; private set; }
        public double MaxVelocityError { get; private set; }
        public void Add(Measurement value)
        {
            if (OutputSha256.Length != 0 && OutputSha256 != value.Evidence.Sha256)
                throw new InvalidOperationException("One strategy produced inconsistent output hashes across samples.");
            OutputSha256 = value.Evidence.Sha256;
            MaxPositionError = Math.Max(MaxPositionError, value.Evidence.PositionError);
            MaxVelocityError = Math.Max(MaxVelocityError, value.Evidence.VelocityError);
            CaptureMilliseconds.Add(value.CaptureMilliseconds); PackingMilliseconds.Add(value.PackingMilliseconds); KernelMilliseconds.Add(value.KernelMilliseconds);
            SynchronizeMilliseconds.Add(value.SynchronizeMilliseconds);
            ConsumeMilliseconds.Add(value.ConsumeMilliseconds); EndToEndMilliseconds.Add(value.EndToEndMilliseconds);
            CaptureAllocatedBytes.Add(value.CaptureAllocatedBytes); PackingAllocatedBytes.Add(value.PackingAllocatedBytes); KernelAllocatedBytes.Add(value.KernelAllocatedBytes);
            SynchronizeAllocatedBytes.Add(value.SynchronizeAllocatedBytes);
            ConsumeAllocatedBytes.Add(value.ConsumeAllocatedBytes); EndToEndAllocatedBytes.Add(value.EndToEndAllocatedBytes);
        }
        public void Finish(int bodies, string fixtureSha256)
        {
            Observation = new PerformanceObservation {
                workload = new PerformanceWorkloadIdentity { system = "layout-integration", workload = Workload,
                    fixtureSha256 = fixtureSha256, items = bodies, steps = 1 },
                strategy = Strategy,
                capture = Phase(CaptureMilliseconds, CaptureAllocatedBytes),
                pack = Phase(PackingMilliseconds, PackingAllocatedBytes),
                compute = Phase(KernelMilliseconds, KernelAllocatedBytes),
                synchronize = Phase(SynchronizeMilliseconds, SynchronizeAllocatedBytes),
                publish = Phase(ConsumeMilliseconds, ConsumeAllocatedBytes),
                total = Phase(EndToEndMilliseconds, EndToEndAllocatedBytes)
            };
            PerformanceObservations.Validate(Observation);
        }
        static PerformancePhaseSamples Phase(List<double> milliseconds, List<long> bytes)
        {
            return new PerformancePhaseSamples { milliseconds = milliseconds.ToArray(), allocatedBytes = bytes.ToArray() };
        }
    }
}
