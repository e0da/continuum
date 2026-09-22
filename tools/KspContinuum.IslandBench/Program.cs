using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using KspContinuum;

sealed class TimingRow
{
    public string workload { get; set; } = "";
    public int bodies { get; set; }
    public int islands { get; set; }
    public int maxIslandBodies { get; set; }
    public double serialMilliseconds { get; set; }
    public double parallelMilliseconds { get; set; }
    public double speedup { get; set; }
    public bool exactOutputMatch { get; set; }
}
sealed class SafetyRow
{
    public bool cancellationObserved { get; set; }
    public bool failureObserved { get; set; }
    public bool cancellationPublishedOutput { get; set; }
    public bool failurePublishedOutput { get; set; }
}
sealed class Report
{
    public string schema { get; set; } = "ksp-continuum-island-bench/v1";
    public int repetitions { get; set; }
    public int parallelism { get; set; }
    public string timingNote { get; set; } = "End-to-end decomposition, scheduling, rigid translation, validation, and publication.";
    public List<TimingRow> workloads { get; set; } = new List<TimingRow>();
    public SafetyRow safety { get; set; } = new SafetyRow();
}
sealed class FailingKernel : IStructuralIslandKernel
{
    public IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> indices, CancellationToken token)
    { throw new InvalidOperationException("injected island failure"); }
}
sealed class CancellingKernel : IStructuralIslandKernel
{
    readonly CancellationTokenSource source;
    public CancellingKernel(CancellationTokenSource source) { this.source = source; }
    public IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> indices, CancellationToken token)
    { source.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception("unreachable"); }
}
static class Program
{
    static SimulationBatch MakeBatch(int count)
    {
        var bodies = new SimulationBody[count];
        for (int i = 0; i < count; i++) bodies[i] = new SimulationBody(i, 1 + i % 13,
            new Vec(i * .125, (i % 17) * .25, (i % 7) * -.5),
            new Vec((i % 5) * .01, (i % 11) * -.02, (i % 3) * .03),
            new Vec((i % 19) * .1, (i % 23) * -.05, (i % 29) * .025));
        return new SimulationBatch(new WorkStamp(1, 1, 1), .02, bodies);
    }
    static List<IslandLink> Chains(params int[] sizes)
    {
        var links = new List<IslandLink>(); int offset = 0;
        foreach (int size in sizes) { for (int i = 1; i < size; i++) links.Add(new IslandLink(offset + i - 1, offset + i)); offset += size; }
        return links;
    }
    static string Hash(SimulationBatch batch)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < batch.Count; i++)
        {
            hash.AppendData(BitConverter.GetBytes(batch.GetId(i)));
            Vec p = batch.GetPosition(i), v = batch.GetVelocity(i);
            foreach (double value in new[] { p.X, p.Y, p.Z, v.X, v.Y, v.Z }) hash.AppendData(BitConverter.GetBytes(value));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static double Measure(StructuralIslandBackend backend, SimulationBatch batch, int repetitions, out SimulationBatch output)
    {
        output = backend.Compute(batch, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < repetitions; i++) output = backend.Compute(batch, CancellationToken.None);
        watch.Stop(); return watch.Elapsed.TotalMilliseconds / repetitions;
    }
    static TimingRow MeasureCase(string name, SimulationBatch batch, List<IslandLink> links, int parallelism, int repetitions)
    {
        StructuralIslandPlan plan = DeterministicIslandDecomposer.Decompose(batch, links); int max = 0;
        foreach (var island in plan.BodyIndices) max = Math.Max(max, island.Count);
        SimulationBatch serialOutput, parallelOutput;
        double serial = Measure(new StructuralIslandBackend(links, 1), batch, repetitions, out serialOutput);
        double parallel = Measure(new StructuralIslandBackend(links, parallelism), batch, repetitions, out parallelOutput);
        return new TimingRow { workload = name, bodies = batch.Count, islands = plan.BodyIndices.Count, maxIslandBodies = max,
            serialMilliseconds = serial, parallelMilliseconds = parallel, speedup = serial / parallel,
            exactOutputMatch = Hash(serialOutput) == Hash(parallelOutput) };
    }
    static int Main(string[] args)
    {
        int repetitions = 20, parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount));
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repetitions" && i + 1 < args.Length) repetitions = int.Parse(args[++i]);
            else if (args[i] == "--parallelism" && i + 1 < args.Length) parallelism = int.Parse(args[++i]);
            else throw new ArgumentException("usage: [--repetitions 1..1000] [--parallelism 1..64]");
        }
        if (repetitions < 1 || repetitions > 1000 || parallelism < 1 || parallelism > 64) throw new ArgumentOutOfRangeException();
        var report = new Report { repetitions = repetitions, parallelism = parallelism };
        report.workloads.Add(MeasureCase("many-tiny-islands", MakeBatch(4096), new List<IslandLink>(), parallelism, repetitions));
        report.workloads.Add(MeasureCase("long-chain", MakeBatch(4096), Chains(4096), parallelism, repetitions));
        int[] mixedSizes = { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1500, 7 };
        report.workloads.Add(MeasureCase("mixed-sizes", MakeBatch(4089), Chains(mixedSizes), parallelism, repetitions));
        var safetyBatch = MakeBatch(16); var noLinks = new List<IslandLink>();
        try { new StructuralIslandBackend(noLinks, parallelism, new FailingKernel()).Compute(safetyBatch, CancellationToken.None); report.safety.failurePublishedOutput = true; }
        catch (AggregateException) { report.safety.failureObserved = true; }
        using (var source = new CancellationTokenSource())
        {
            try { new StructuralIslandBackend(noLinks, parallelism, new CancellingKernel(source)).Compute(safetyBatch, source.Token); report.safety.cancellationPublishedOutput = true; }
            catch (OperationCanceledException) { report.safety.cancellationObserved = true; }
        }
        bool valid = report.safety.failureObserved && report.safety.cancellationObserved;
        foreach (var row in report.workloads) valid &= row.exactOutputMatch;
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return valid ? 0 : 1;
    }
}
