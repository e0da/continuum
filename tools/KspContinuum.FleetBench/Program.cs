using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KspContinuum;

static class Program
{
    const double Epoch = 1000000, Mu = 3.5316e12;
    sealed class Vessel
    {
        public int Id;
        public CoastingEngine Engine;
        public bool PredictEvent;
    }
    struct Result { public Vec Position, Velocity; public int Candidates; }

    static int Main(string[] args)
    {
        try
        {
            int samples = 7; bool quick = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--quick") { quick = true; continue; }
                if (args[i] == "--samples" && i + 1 < args.Length && int.TryParse(args[++i], out samples) && samples >= 3 && samples <= 100) continue;
                throw new ArgumentException("Use --quick and/or --samples 3..100.");
            }
            int maximumWorkers = Math.Max(1, Math.Min(Environment.ProcessorCount, 16));
            int[] workers = new[] { 1, 2, 4, 8, 16 }.Where(x => x <= maximumWorkers).Append(maximumWorkers).Distinct().OrderBy(x => x).ToArray();
            int[] counts = quick ? new[] { 16, 64 } : new[] { 16, 64, 256, 1024, 4096 };
            int[] work = quick ? new[] { 1, 8 } : new[] { 1, 8, 32 };
            double[] eventDensity = quick ? new[] { 0d, 1d } : new[] { 0d, .25, 1d };
            var rows = new List<object>();
            foreach (int count in counts) foreach (int steps in work) foreach (double density in eventDensity)
            {
                Vessel[] fleet = Fixture(count, density);
                Result[] reference = Execute(fleet, steps, 1);
                string expectedHash = Hash(reference);
                foreach (int workerCount in workers)
                    rows.Add(Measure(fleet, steps, density, workerCount, samples, expectedHash));
            }
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema = "ksp-continuum-fleet-bench/v1", samples, quick, warmups = 3, timerTargetMilliseconds = 20,
                logicalProcessors = Environment.ProcessorCount,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                workload = "independent persistent two-body trajectory samples plus optional two-object encounter plans",
                timing = "complete ordered fleet evaluation; calibration chooses repeated passes; validation and hashing outside timing",
                allocation = "process-wide allocated bytes during timed window; includes runtime worker activity and is not retained memory",
                rows
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static Vessel[] Fixture(int count, double eventDensity)
    {
        var fleet = new Vessel[count];
        for (int i = 0; i < count; i++)
        {
            double radius = 700000 + (i % 97) * 500;
            double speed = Math.Sqrt(Mu / radius);
            fleet[i] = new Vessel {
                Id = i,
                Engine = new CoastingEngine(Epoch, Mu,
                    new[] { new CoastingBody(i, new Vec(radius, 0, 0), new Vec(0, speed, 0)) }, 1),
                PredictEvent = ((i * 2654435761u) % 1000) < eventDensity * 1000
            };
        }
        return fleet;
    }

    static Result[] Execute(Vessel[] fleet, int steps, int workers)
    {
        var output = new Result[fleet.Length];
        Action<int> evaluate = i =>
        {
            Vessel vessel = fleet[i]; CoastingBody body = null;
            for (int step = 1; step <= steps; step++) body = vessel.Engine.SampleAt(Epoch + step * 5).Bodies[0];
            int candidates = 0;
            if (vessel.PredictEvent)
            {
                var plan = EncounterPlanner.Plan(new[] {
                    new EncounterMotion(vessel.Id * 2, 0, 0, "fleet-local", body.Position, body.Velocity, 2, 20, 20, accelerationBound: 0),
                    new EncounterMotion(vessel.Id * 2 + 1, 0, 0, "fleet-local", body.Position + new Vec(100, 0, 0),
                        body.Velocity + new Vec(-5, 0, 0), 2, 20, 20, accelerationBound: 0)
                }, 20, new EncounterBudget());
                if (plan.Status != EncounterPlanStatus.Complete) throw new InvalidOperationException("Encounter plan did not complete.");
                candidates = plan.Candidates.Count;
            }
            output[i] = new Result { Position = body.Position, Velocity = body.Velocity, Candidates = candidates };
        };
        if (workers == 1) for (int i = 0; i < fleet.Length; i++) evaluate(i);
        else Parallel.For(0, fleet.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, evaluate);
        return output;
    }

    static object Measure(Vessel[] fleet, int steps, double density, int workers, int samples, string expectedHash)
    {
        for (int i = 0; i < 3; i++) Verify(Execute(fleet, steps, workers), expectedHash);
        int repetitions = 1;
        while (true)
        {
            var calibration = Stopwatch.StartNew();
            for (int i = 0; i < repetitions; i++) Execute(fleet, steps, workers);
            calibration.Stop();
            if (calibration.Elapsed.TotalMilliseconds >= 20 || repetitions >= 16384) break;
            repetitions *= 2;
        }
        var milliseconds = new double[samples]; var allocations = new long[samples]; string hash = null;
        for (int sample = 0; sample < samples; sample++)
        {
            long before = GC.GetTotalAllocatedBytes(true); Result[] result = null;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < repetitions; i++) result = Execute(fleet, steps, workers);
            clock.Stop(); long after = GC.GetTotalAllocatedBytes(true);
            hash = Hash(result); Verify(result, expectedHash);
            milliseconds[sample] = clock.Elapsed.TotalMilliseconds / repetitions;
            allocations[sample] = (after - before) / repetitions;
        }
        Array.Sort(milliseconds); Array.Sort(allocations);
        double median = milliseconds[(samples - 1) / 2];
        return new { vessels = fleet.Length, samplesPerVessel = steps, eventDensity = density, workers, repetitions,
            medianMilliseconds = median, p95Milliseconds = milliseconds[(int)Math.Ceiling(samples * .95) - 1],
            medianAllocatedBytes = allocations[(samples - 1) / 2], vesselsPerSecond = fleet.Length * 1000 / median,
            outputHash = hash, deterministicOrder = true };
    }

    static void Verify(Result[] result, string expectedHash)
    {
        if (Hash(result) != expectedHash) throw new InvalidOperationException("Worker count changed deterministic ordered output.");
        for (int i = 0; i < result.Length; i++)
            if (!Finite(result[i].Position) || !Finite(result[i].Velocity)) throw new InvalidOperationException("Nonfinite fleet result.");
    }
    static bool Finite(Vec value) => !(double.IsNaN(value.X) || double.IsInfinity(value.X) ||
        double.IsNaN(value.Y) || double.IsInfinity(value.Y) || double.IsNaN(value.Z) || double.IsInfinity(value.Z));
    static string Hash(Result[] values)
    {
        var text = new StringBuilder(values.Length * 80);
        for (int i = 0; i < values.Length; i++) text.Append(i).Append(':').Append(values[i].Position.X.ToString("R"))
            .Append(':').Append(values[i].Position.Y.ToString("R")).Append(':').Append(values[i].Position.Z.ToString("R"))
            .Append(':').Append(values[i].Velocity.X.ToString("R")).Append(':').Append(values[i].Velocity.Y.ToString("R"))
            .Append(':').Append(values[i].Velocity.Z.ToString("R")).Append(':').Append(values[i].Candidates).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
