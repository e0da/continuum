using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KspContinuum;

static class Program
{
    static int assertions;
    static void Check(bool value, string label) { Interlocked.Increment(ref assertions); if (!value) throw new Exception(label); }
    static ProgramSnapshot Initial(double x = 0)
    {
        return new ProgramSnapshot("kerbin-inertial", 0, 1000, new[] {
            new ProgramBody(2, 0, 2, 1, new Vec(x + 10, 0, 0), new Vec()),
            new ProgramBody(1, 0, 1, 1, new Vec(x, 0, 0), new Vec()) });
    }
    static ProgramSnapshot Burn(ProgramWork work, double dv)
    {
        var bodies = new List<ProgramBody>();
        foreach (var body in work.Snapshot.Bodies)
        {
            bool controlled = work.Authority.BodyIds.Contains(body.Id);
            bodies.Add(new ProgramBody(body.Id, body.Generation, body.Mass, body.Radius,
                body.Position, controlled ? body.Velocity + new Vec(dv, 0, 0) : body.Velocity));
        }
        return new ProgramSnapshot(work.Snapshot.Frame, work.Snapshot.ModelFingerprint,
            work.Snapshot.TopologyGeneration, work.Snapshot.FrameGeneration, work.Snapshot.Tick + 1,
            work.Authority.EndTime, bodies);
    }
    static string RunBranch(string id, double dv)
    {
        var history = new ProgramHistory(); var root = Initial();
        Check(history.CreateRoot("nominal", root) == ProgramStatus.Committed, id + " root");
        Check(history.Fork(id, "nominal", root.Hash) == ProgramStatus.Committed, id + " fork");
        ProgramAuthority lease;
        Check(history.Acquire("controller", id, root.Hash, 1000, 1001, new[] { 1 }, out lease) == ProgramStatus.Acquired, id + " acquire");
        Check(history.Execute(lease, "finite-burn", "dv=" + dv.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            (work, token) => Burn(work, dv), CancellationToken.None) == ProgramStatus.Committed, id + " commit");
        return history.Capture(id).Hash;
    }
    static void Main()
    {
        var a = Initial(); var reversed = new ProgramSnapshot(a.Frame, a.Tick, a.Epoch, a.Bodies.Reverse());
        Check(a.Hash == reversed.Hash, "canonical body ordering");
        Check(a.Hash == Initial().Hash, "repeatable snapshot hash");
        var negativeZero = new ProgramSnapshot(a.Frame, a.Tick, a.Epoch, new[] {
            new ProgramBody(1, 0, 1, 1, new Vec(-0.0, 0, 0), new Vec()), a.Bodies[1] });
        Check(a.Hash == negativeZero.Hash, "negative zero canonicalized");
        var changedModel = new ProgramSnapshot(a.Frame, "other-model", 0, 0, a.Tick, a.Epoch, a.Bodies);
        Check(a.Hash != changedModel.Hash, "model fingerprint participates in state identity");

        var history = new ProgramHistory();
        Check(history.CreateRoot("main", a) == ProgramStatus.Committed, "create root");
        Check(history.Fork("forecast-001", "main", a.Hash) == ProgramStatus.Committed, "fork exact head");
        Check(history.Fork("forecast-stale", "main", "bad") == ProgramStatus.Stale, "reject stale fork");
        ProgramAuthority authority;
        Check(history.Acquire("bad-time", "forecast-001", a.Hash, 0, 1, new[] { 1 }, out authority) == ProgramStatus.Rejected, "interval anchored to snapshot epoch");
        Check(history.Acquire("mechjeb", "forecast-001", a.Hash, 1000, 1002, new[] { 1 }, out authority) == ProgramStatus.Acquired, "acquire bounded authority");
        ProgramAuthority second;
        Check(history.Acquire("other", "forecast-001", a.Hash, 1000, 1002, new[] { 1 }, out second) == ProgramStatus.Busy, "one writer per branch");
        Check(history.Execute(authority, "finite-burn", "dv=2", (work, token) => Burn(work, 2), CancellationToken.None) == ProgramStatus.Committed, "atomic commit");
        Check(history.Capture("main").Hash == a.Hash, "forecast cannot mutate parent");
        Check(history.Events().Count == 3, "root fork commit ledger");
        Check(history.Events().Last().ParentHash == a.Hash && history.Events().Last().ResultHash == history.Capture("forecast-001").Hash, "ledger binds transition");
        Check(history.Verify("main") && history.Verify("forecast-001"), "hash chained ledgers verify");
        Check(history.GetSnapshot(a.Hash) != null, "content addressed snapshot available");

        var beforeFault = history.Capture("forecast-001");
        Check(history.Acquire("fault", "forecast-001", beforeFault.Hash, 1002, 1003, new[] { 1 }, out authority) == ProgramStatus.Acquired, "fault acquire");
        Check(history.Execute(authority, "finite-burn", "fault", (work, token) => throw new InvalidOperationException(), CancellationToken.None) == ProgramStatus.Faulted, "solver fault reported");
        Check(history.Capture("forecast-001").Hash == beforeFault.Hash && history.Events().Count == 3, "fault has no partial publication");

        Check(history.Acquire("abort", "forecast-001", beforeFault.Hash, 1002, 1003, new[] { 1 }, out authority) == ProgramStatus.Acquired, "abort acquire");
        Check(history.Execute(authority, "bad kind", "payload", (work, token) => Burn(work, 1), CancellationToken.None) == ProgramStatus.Rejected, "bad execute rejected");
        Check(history.Acquire("after-abort", "forecast-001", beforeFault.Hash, 1002, 1003, new[] { 1 }, out authority) == ProgramStatus.Acquired, "bad execute releases lease");
        Check(history.Abort(authority) == ProgramStatus.Cancelled, "explicit abort releases lease");

        Check(history.Acquire("invalid", "forecast-001", beforeFault.Hash, 1002, 1003, new[] { 1 }, out authority) == ProgramStatus.Acquired, "invalid acquire");
        Check(history.Execute(authority, "finite-burn", "bad", (work, token) => new ProgramSnapshot(work.Snapshot.Frame,
            work.Snapshot.Tick + 1, 1003, new[] { new ProgramBody(1, 0, 99, 1, new Vec(), new Vec()), work.Snapshot.Bodies[1] }),
            CancellationToken.None) == ProgramStatus.InvalidResult, "identity mutation rejected");
        Check(history.Capture("forecast-001").Hash == beforeFault.Hash, "invalid result has no publication");

        Check(history.Acquire("stale", "forecast-001", beforeFault.Hash, 1002, 1003, new[] { 1 }, out authority) == ProgramStatus.Acquired, "stale acquire");
        Check(history.Observe("forecast-001", Initial(100), "ksp") == ProgramStatus.Committed, "external observation advances authority");
        ProgramAuthority replacement;
        Check(history.Acquire("replacement", "forecast-001", Initial(100).Hash, 1000, 1001, new[] { 1 }, out replacement) == ProgramStatus.Acquired, "new authority after observation");
        Check(history.Execute(authority, "finite-burn", "late", (work, token) => Burn(work, 1), CancellationToken.None) == ProgramStatus.Stale, "stale result rejected");
        Check(history.Execute(replacement, "finite-burn", "current", (work, token) => Burn(work, 1), CancellationToken.None) == ProgramStatus.Committed, "stale completion cannot clear replacement lease");
        Check(history.Capture("forecast-001").Hash != Initial(100).Hash, "replacement remains authoritative");

        string serialA = RunBranch("scenario-0001", 0.1), serialB = RunBranch("scenario-0002", -0.2);
        string parallelA = null, parallelB = null;
        Parallel.Invoke(() => parallelA = RunBranch("scenario-0001", 0.1), () => parallelB = RunBranch("scenario-0002", -0.2));
        Check(serialA == parallelA && serialB == parallelB, "parallel scenarios equal serial scenarios");
        Check(serialA == RunBranch("scenario-0001", 0.1), "deterministic replay");
        Console.WriteLine("Program history assertions: " + assertions);
    }
}
