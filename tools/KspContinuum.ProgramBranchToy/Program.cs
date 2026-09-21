using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KspContinuum;

static class Program
{
    sealed class Scenario { public int sample { get; set; } public string hash { get; set; } }
    sealed class Receipt
    {
        public string schema { get; set; } public int seed { get; set; } public int samples { get; set; }
        public string rootHash { get; set; } public bool serialParallelEqual { get; set; }
        public bool staleRejected { get; set; } public Scenario[] scenarios { get; set; }
    }
    static ProgramSnapshot Initial()
    {
        return new ProgramSnapshot("kerbin-inertial", "program-branch-toy/v1", 0, 0, 0, 1000, new[] {
            new ProgramBody(1, 0, 4, 1, new Vec(0, 0, 0), new Vec(0, 1, 0)),
            new ProgramBody(2, 0, 2, 1, new Vec(100, 0, 0), new Vec(0, -1, 0)) });
    }
    static ulong Mix(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL; value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL; return value ^ (value >> 31);
    }
    static double DeltaV(int seed, int sample)
    { return ((Mix(((ulong)(uint)seed << 32) | (uint)sample) >> 11) * (1.0 / (1UL << 53)) - 0.5) * 0.2; }
    static string Evaluate(ProgramSnapshot root, int seed, int sample)
    {
        var history = new ProgramHistory(); history.CreateRoot("root", root);
        string branch = "forecast-" + sample.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        history.Fork(branch, "root", root.Hash); ProgramAuthority authority;
        history.Acquire("script", branch, root.Hash, 1000, 1001, new[] { 1 }, out authority);
        double dv = DeltaV(seed, sample);
        var status = history.Execute(authority, "finite-burn", "seed=" + seed + ";sample=" + sample,
            (work, token) => {
                var bodies = new List<ProgramBody>();
                foreach (var body in work.Snapshot.Bodies)
                    bodies.Add(new ProgramBody(body.Id, body.Generation, body.Mass, body.Radius, body.Position,
                        body.Id == 1 ? body.Velocity + new Vec(dv, 0, 0) : body.Velocity));
                return new ProgramSnapshot(work.Snapshot.Frame, work.Snapshot.ModelFingerprint,
                    work.Snapshot.TopologyGeneration, work.Snapshot.FrameGeneration, 1, 1001, bodies);
            }, CancellationToken.None);
        if (status != ProgramStatus.Committed || !history.Verify(branch)) throw new Exception("scenario failed");
        return history.Capture(branch).Hash;
    }
    static void Main(string[] args)
    {
        int seed = args.Length == 0 ? 20260921 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        const int count = 256; var root = Initial(); var serial = new string[count]; var parallel = new string[count];
        for (int i = 0; i < count; i++) serial[i] = Evaluate(root, seed, i);
        Parallel.For(0, count, i => parallel[i] = Evaluate(root, seed, i));
        bool equal = true; var scenarios = new Scenario[count];
        for (int i = 0; i < count; i++) { equal &= serial[i] == parallel[i]; scenarios[i] = new Scenario { sample = i, hash = serial[i] }; }

        var stale = new ProgramHistory(); stale.CreateRoot("root", root); ProgramAuthority old;
        stale.Acquire("old", "root", root.Hash, 1000, 1001, new[] { 1 }, out old);
        stale.Observe("root", Initial(), "ksp");
        bool staleRejected = stale.Execute(old, "finite-burn", "late", (work, token) => work.Snapshot,
            CancellationToken.None) == ProgramStatus.Stale;
        var receipt = new Receipt { schema = "ksp-continuum-program-branch-receipt/v1", seed = seed,
            samples = count, rootHash = root.Hash, serialParallelEqual = equal, staleRejected = staleRejected,
            scenarios = scenarios };
        Console.WriteLine(JsonSerializer.Serialize(receipt));
        if (!equal || !staleRejected) Environment.ExitCode = 1;
    }
}
