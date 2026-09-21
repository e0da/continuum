using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using KspContinuum;

static class HandoffComparison
{
    const double Dt = .02;
    const int Warmup = 10;
    sealed class MeasuredBackend : ISimulationBackend
    {
        readonly ConstantForceBackend backend = new ConstantForceBackend();
        public long AllocatedBytes;
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            var result = backend.Compute(batch, cancellation);
            Interlocked.Exchange(ref AllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - start);
            return result;
        }
    }
    sealed class Sample
    {
        public double EndToEndMilliseconds { get; set; }
        public long CallerAllocatedBytes { get; set; }
        public long BackendAllocatedBytes { get; set; }
        public double MaxPositionError { get; set; }
        public double MaxVelocityError { get; set; }
    }
    sealed class Result
    {
        public string Layout { get; set; }
        public List<Sample> Samples { get; } = new List<Sample>();
        public string OutputSha256 { get; set; }
        public double MedianMilliseconds { get { return Percentile(.5); } }
        public double P95Milliseconds { get { return Percentile(.95); } }
        double Percentile(double fraction)
        {
            var sorted = Samples.Select(s => s.EndToEndMilliseconds).OrderBy(x => x).ToArray();
            return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
        }
    }
    sealed class Fixture
    {
        public readonly int[] Ids;
        public readonly double[] Masses;
        public readonly Vec[] Positions, Velocities, Forces;
        public Fixture(int count)
        {
            Ids = new int[count]; Masses = new double[count];
            Positions = new Vec[count]; Velocities = new Vec[count]; Forces = new Vec[count];
            for (int i = 0; i < count; i++)
            {
                Ids[i] = i; Masses[i] = 2;
                Positions[i] = new Vec(i, 0, 0); Velocities[i] = new Vec(1, 2, 3); Forces[i] = new Vec(0, -4, 2);
            }
        }
        public SimulationBatch Capture(bool columns, long tick)
        {
            var stamp = new WorkStamp(tick, 2, 3);
            if (columns) return SimulationBatch.FromColumns(stamp, Dt, Ids, Masses, Positions, Velocities, Forces);
            var bodies = new SimulationBody[Ids.Length];
            for (int i = 0; i < bodies.Length; i++) bodies[i] = new SimulationBody(Ids[i], Masses[i], Positions[i], Velocities[i], Forces[i]);
            return new SimulationBatch(stamp, Dt, bodies);
        }
    }
    public static object Measure(int count, int samples)
    {
        var fixture = new Fixture(count);
        var backends = new[] { new MeasuredBackend(), new MeasuredBackend() };
        var results = new[] { new Result { Layout = "object" }, new Result { Layout = "columns" } };
        var orders = new List<string[]>();
        using (var objectWorker = new SimulationWorker(backends[0]))
        using (var columnWorker = new SimulationWorker(backends[1]))
        {
            var workers = new[] { objectWorker, columnWorker };
            uint random = 20260921;
            for (int sample = -Warmup; sample < samples; sample++)
            {
                random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                int first = (int)(random & 1);
                if (sample >= 0) orders.Add(new[] { results[first].Layout, results[1 - first].Layout });
                for (int turn = 0; turn < 2; turn++)
                {
                    int layout = turn == 0 ? first : 1 - first;
                    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    var input = fixture.Capture(layout == 1, sample + Warmup);
                    var output = Execute(workers[layout], input);
                    if (output.Count != count || output.UsesColumnStorage != (layout == 1)) throw new InvalidOperationException("Handoff layout/count changed.");
                    double positionError = 0, velocityError = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (output.GetId(i) != i || output.GetMass(i) != 2 || Error(output.GetForce(i), new Vec(0, -4, 2)) != 0)
                            throw new InvalidOperationException("Handoff changed body envelope.");
                        positionError = Math.Max(positionError, Error(output.GetPosition(i), new Vec(i + .02, .0396, .0602)));
                        velocityError = Math.Max(velocityError, Error(output.GetVelocity(i), new Vec(1, 1.96, 3.02)));
                    }
                    if (positionError > 1e-10 || velocityError > 1e-10) throw new InvalidOperationException("Handoff disagrees with analytic fixture.");
                    double milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    string hash = Hash(output);
                    if (results[layout].OutputSha256 != null && hash != results[layout].OutputSha256) throw new InvalidOperationException("Output changed across samples.");
                    results[layout].OutputSha256 = hash;
                    if (sample >= 0) results[layout].Samples.Add(new Sample {
                        EndToEndMilliseconds = milliseconds, CallerAllocatedBytes = allocated,
                        BackendAllocatedBytes = Interlocked.Read(ref backends[layout].AllocatedBytes),
                        MaxPositionError = positionError, MaxVelocityError = velocityError });
                }
            }
        }
        if (results[0].OutputSha256 != results[1].OutputSha256) throw new InvalidOperationException("Layouts returned different output bytes.");
        return new {
            schema = "ksp-continuum-handoff-layout/v1", bodies = count, samples, warmupSamples = Warmup, stepSeconds = Dt,
            seed = 20260921, runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            measurement = "capture through actual worker handoff and indexed consumption/analytic checks; fixture preparation and output hashing excluded; caller polling included",
            allocationScope = "caller thread capture-through-consume plus separately measured backend Compute allocations; excludes worker envelope/queue overhead and other process threads; not retained memory",
            workload = "independent constant-force point masses; same fixed fixture as compute benchmark; no rotation, joints, contacts, Unity or native bridge",
            percentileMethod = "nearest rank", compatibilityBodiesMaterialized = false, poolingUsed = false,
            stockPhysicsSpeedupMeasured = false, orders, results
        };
    }
    static SimulationBatch Execute(SimulationWorker worker, SimulationBatch input)
    {
        if (worker.TrySubmit(input) != SubmitStatus.Accepted) throw new InvalidOperationException("Handoff submission refused.");
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            var status = worker.TryTake(input.Stamp, out SimulationBatch result);
            if (status == ResultStatus.Ready) return result;
            if (status != ResultStatus.Pending) throw new InvalidOperationException("Handoff failed: " + status, worker.Fault);
            if ((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency > 10) throw new TimeoutException("Handoff timed out.");
            Thread.Yield();
        }
    }
    static double Error(Vec a, Vec b) { return Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z))); }
    static string Hash(SimulationBatch batch)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    writer.Write(batch.GetId(i)); writer.Write(batch.GetMass(i));
                    foreach (var vector in new[] { batch.GetPosition(i), batch.GetVelocity(i), batch.GetForce(i) })
                    { writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z); }
                }
            }
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
        }
    }
}
