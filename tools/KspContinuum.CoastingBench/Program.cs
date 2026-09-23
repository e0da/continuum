using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using KspContinuum;

static class Program
{
    const double Epoch = 13110197.451798951;
    const double HorizonSeconds = 600;
    const double Mu = 3.5316e12;
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

    sealed class Measurement
    {
        public string mode { get; set; } = "";
        public int bodies { get; set; }
        public int workBatchSize { get; set; }
        public int evaluationsPerOperation { get; set; }
        public int samples { get; set; }
        public double[] milliseconds { get; set; } = Array.Empty<double>();
        public long[] processAllocatedBytes { get; set; } = Array.Empty<long>();
        public long[] callerAllocatedBytes { get; set; } = Array.Empty<long>();
        public double medianMilliseconds { get; set; }
        public double medianMicrosecondsPerBodyEvaluation { get; set; }
        public double medianProcessBytesPerBodyEvaluation { get; set; }
        public double checksum { get; set; }
    }

    sealed class Report
    {
        public string schema { get; set; } = "ksp-continuum-coasting-bench/v1";
        public string runtime { get; set; } = RuntimeInformation.FrameworkDescription;
        public string architecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
        public int logicalProcessors { get; set; } = Environment.ProcessorCount;
        public string measurement { get; set; } =
            "Each case has one untimed warmup followed by repeated process-local measurements. Engine and input construction occur before timing. " +
            "Elapsed time includes Parallel.For scheduling, propagation, immutable result construction, snapshot array cloning, and publication retention where applicable. " +
            "Process allocation deltas include worker-thread allocations but may include unrelated runtime activity; caller allocation excludes worker threads. " +
            "The epoch-copy case executes the same scheduling and snapshot path with zero propagation interval, so comparison with final-only estimates solve cost without claiming a pure component timer.";
        public string interpretation { get; set; } =
            "This compares the portable engine in isolation. It does not measure KSP synchronization, frame time, rendering, or an FPS improvement.";
        public bool qualified { get; set; }
        public SortedDictionary<string, bool> checks { get; set; } = new SortedDictionary<string, bool>();
        public SortedDictionary<string, double> observedCostMap { get; set; } = new SortedDictionary<string, double>();
        public Measurement[] workloads { get; set; } = Array.Empty<Measurement>();
    }

    static CoastingBody[] Bodies(int count)
    {
        const double semiMajor = 732639.5703;
        var result = new CoastingBody[count];
        double speed = Math.Sqrt(Mu / semiMajor);
        for (int index = 0; index < count; index++)
        {
            double angle = index * (2 * Math.PI / count);
            double radius = semiMajor + (index % 17 - 8) * 100;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            result[index] = new CoastingBody(index,
                new Vec(radius * cosine, radius * sine, (index % 11 - 5) * 10),
                new Vec(-speed * sine, speed * cosine, 0));
        }
        return result;
    }

    static double Execute(string mode, CoastingEngine engine)
    {
        CoastingSnapshot final;
        if (mode == "epoch-copy") final = engine.SampleAt(Epoch);
        else if (mode == "final-only") final = engine.SampleAt(Epoch + HorizonSeconds);
        else if (mode == "sparse-publication") final = engine.AdvanceTo(Epoch + HorizonSeconds, 300).Final;
        else if (mode == "dense-evaluation-discard")
        {
            final = null;
            for (double offset = 10; offset <= HorizonSeconds; offset += 10)
                final = engine.SampleAt(Epoch + offset);
        }
        else if (mode == "dense-publication") final = engine.AdvanceTo(Epoch + HorizonSeconds, 10).Final;
        else throw new ArgumentException("Unknown benchmark mode.");
        CoastingBody first = final.Bodies[0], last = final.Bodies[final.Bodies.Count - 1];
        return first.Position.X + first.Velocity.Y + last.Position.Z + last.Velocity.X;
    }

    static int Evaluations(string mode)
    {
        if (mode == "sparse-publication") return 2;
        if (mode == "dense-evaluation-discard" || mode == "dense-publication") return 60;
        return 1;
    }

    static double Median(double[] values)
    {
        double[] copy = (double[])values.Clone(); Array.Sort(copy);
        return copy.Length % 2 == 0 ? (copy[copy.Length / 2 - 1] + copy[copy.Length / 2]) / 2 : copy[copy.Length / 2];
    }

    static long Median(long[] values)
    {
        long[] copy = (long[])values.Clone(); Array.Sort(copy);
        return copy.Length % 2 == 0 ? (copy[copy.Length / 2 - 1] + copy[copy.Length / 2]) / 2 : copy[copy.Length / 2];
    }

    static Measurement Measure(string mode, CoastingBody[] bodies, int workBatchSize, int samples)
    {
        Execute(mode, new CoastingEngine(Epoch, Mu, bodies, workBatchSize));
        var milliseconds = new double[samples];
        var processBytes = new long[samples];
        var callerBytes = new long[samples];
        double checksum = 0;
        for (int sample = 0; sample < samples; sample++)
        {
            var engine = new CoastingEngine(Epoch, Mu, bodies, workBatchSize);
            long processStart = GC.GetTotalAllocatedBytes(false);
            long callerStart = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            checksum = Execute(mode, engine);
            clock.Stop();
            callerBytes[sample] = GC.GetAllocatedBytesForCurrentThread() - callerStart;
            processBytes[sample] = GC.GetTotalAllocatedBytes(false) - processStart;
            milliseconds[sample] = clock.Elapsed.TotalMilliseconds;
        }
        int evaluations = Evaluations(mode);
        double medianMs = Median(milliseconds);
        double bodyEvaluations = (double)bodies.Length * evaluations;
        return new Measurement {
            mode = mode, bodies = bodies.Length, workBatchSize = workBatchSize,
            evaluationsPerOperation = evaluations, samples = samples,
            milliseconds = milliseconds, processAllocatedBytes = processBytes, callerAllocatedBytes = callerBytes,
            medianMilliseconds = medianMs,
            medianMicrosecondsPerBodyEvaluation = medianMs * 1000 / bodyEvaluations,
            medianProcessBytesPerBodyEvaluation = Median(processBytes) / bodyEvaluations,
            checksum = checksum
        };
    }

    static int Main(string[] arguments)
    {
        try
        {
            int samples = 7; string output = null;
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] == "--samples" && index + 1 < arguments.Length)
                    samples = int.Parse(arguments[++index]);
                else if (arguments[index] == "--output" && index + 1 < arguments.Length)
                    output = arguments[++index];
                else throw new ArgumentException("usage: [--samples 1..32] [--output NEW_PATH]");
            }
            if (samples < 1 || samples > 32) throw new ArgumentOutOfRangeException("samples");
            if (output != null && (File.Exists(output) || Directory.Exists(output)))
                throw new ArgumentException("Output must be a new file path.");

            string[] modes = { "epoch-copy", "final-only", "sparse-publication", "dense-evaluation-discard", "dense-publication" };
            int[] counts = { 1, 16, 256, 4096 };
            var rows = new List<Measurement>();
            foreach (int count in counts)
            {
                CoastingBody[] bodies = Bodies(count);
                int coarseBatch = Math.Min(64, count);
                foreach (int batch in new[] { 1, coarseBatch }.Distinct())
                    foreach (string mode in modes) rows.Add(Measure(mode, bodies, batch, samples));
            }
            bool batchStable = rows.GroupBy(row => new { row.bodies, row.mode })
                .All(group => group.Select(row => BitConverter.DoubleToInt64Bits(row.checksum)).Distinct().Count() == 1);
            bool finiteMeasurements = rows.All(row => row.milliseconds.All(value => value >= 0 && double.IsFinite(value)) &&
                row.processAllocatedBytes.All(value => value >= 0) && row.callerAllocatedBytes.All(value => value >= 0));
            Measurement Find(int bodies, int batch, string mode) => rows.Single(row =>
                row.bodies == bodies && row.workBatchSize == batch && row.mode == mode);
            Measurement copy4096 = Find(4096, 64, "epoch-copy");
            Measurement final4096 = Find(4096, 64, "final-only");
            Measurement sparse4096 = Find(4096, 64, "sparse-publication");
            Measurement discard4096 = Find(4096, 64, "dense-evaluation-discard");
            Measurement dense4096 = Find(4096, 64, "dense-publication");
            Measurement fine256 = Find(256, 1, "final-only"), coarse256 = Find(256, 64, "final-only");
            Measurement fine4096 = Find(4096, 1, "final-only");
            var checks = new SortedDictionary<string, bool> {
                ["batchStrategiesBitwiseStable"] = batchStable,
                ["measurementsFinite"] = finiteMeasurements,
                ["matrixComplete"] = rows.Count == 35
            };
            var costMap = new SortedDictionary<string, double> {
                ["bodies4096EpochCopyMilliseconds"] = copy4096.medianMilliseconds,
                ["bodies4096FinalOnlyMilliseconds"] = final4096.medianMilliseconds,
                ["bodies4096EstimatedPropagationIncrementMilliseconds"] = Math.Max(0, final4096.medianMilliseconds - copy4096.medianMilliseconds),
                ["bodies4096SparsePublicationMilliseconds"] = sparse4096.medianMilliseconds,
                ["bodies4096DenseEvaluationDiscardMilliseconds"] = discard4096.medianMilliseconds,
                ["bodies4096DensePublicationMilliseconds"] = dense4096.medianMilliseconds,
                ["bodies4096DenseRetentionIncrementMilliseconds"] = Math.Max(0, dense4096.medianMilliseconds - discard4096.medianMilliseconds),
                ["bodies4096DenseProcessAllocatedMegabytes"] = Median(dense4096.processAllocatedBytes) / (1024.0 * 1024),
                ["bodies4096DenseToFinalElapsedRatio"] = dense4096.medianMilliseconds / final4096.medianMilliseconds,
                ["bodies256CoarseToFineFinalElapsedRatio"] = coarse256.medianMilliseconds / fine256.medianMilliseconds,
                ["bodies4096CoarseToFineFinalElapsedRatio"] = final4096.medianMilliseconds / fine4096.medianMilliseconds
            };
            var report = new Report { workloads = rows.ToArray(), checks = checks,
                qualified = checks.Values.All(value => value), observedCostMap = costMap };
            string encoded = JsonSerializer.Serialize(report, Json);
            if (output == null) Console.WriteLine(encoded);
            else
            {
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    JsonSerializer.Serialize(stream, report, Json);
                Console.WriteLine(JsonSerializer.Serialize(new { output, workloads = rows.Count }));
            }
            return report.qualified ? 0 : 2;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
