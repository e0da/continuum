using System;
using System.Collections.Generic;
using System.Threading;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
    static SimulationBatch Batch(int count)
    {
        var bodies = new SimulationBody[count];
        for (int i = 0; i < count; i++) bodies[i] = new SimulationBody(100 + i * 3, 1 + i,
            new Vec(i, i * .25, 0), new Vec(i * .01, 0, 0), new Vec(0, i + 1, 0));
        return new SimulationBatch(new WorkStamp(7, 8, 9), .02, bodies);
    }
    sealed class TrackingKernel : IStructuralIslandKernel
    {
        readonly IStructuralIslandKernel inner = new RigidTranslationIslandKernel();
        int active, peak; public int Peak { get { return peak; } }
        public IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> indices, CancellationToken token)
        {
            int now = Interlocked.Increment(ref active), seen;
            do { seen = peak; if (seen >= now) break; } while (Interlocked.CompareExchange(ref peak, now, seen) != seen);
            try { Thread.SpinWait(100000); return inner.Compute(batch, indices, token); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    sealed class FailingKernel : IStructuralIslandKernel
    {
        readonly IStructuralIslandKernel inner = new RigidTranslationIslandKernel();
        public IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> indices, CancellationToken token)
        { if (batch.GetId(indices[0]) == 106) throw new InvalidOperationException("injected"); return inner.Compute(batch, indices, token); }
    }
    static string Signature(StructuralIslandPlan plan, SimulationBatch batch)
    {
        var groups = new List<string>();
        foreach (var island in plan.BodyIndices) { var ids = new List<string>(); foreach (int i in island) ids.Add(batch.GetId(i).ToString()); groups.Add(string.Join(",", ids)); }
        return string.Join("|", groups);
    }
    static int Main()
    {
        SimulationBatch batch = Batch(8);
        var linksA = new[] { new IslandLink(103, 100), new IslandLink(118, 115), new IslandLink(109, 106), new IslandLink(106, 103) };
        var linksB = new[] { linksA[2], linksA[0], linksA[3], linksA[1] };
        var planA = DeterministicIslandDecomposer.Decompose(batch, linksA);
        var planB = DeterministicIslandDecomposer.Decompose(batch, linksB);
        Check(Signature(planA, batch) == "100,103,106,109|112|115,118|121", "unexpected island order");
        Check(Signature(planA, batch) == Signature(planB, batch), "link order changed decomposition");
        var serial = new StructuralIslandBackend(linksA, 1).Compute(batch, CancellationToken.None);
        var parallel = new StructuralIslandBackend(linksB, 4).Compute(batch, CancellationToken.None);
        for (int i = 0; i < batch.Count; i++)
        {
            Check(serial.GetId(i) == parallel.GetId(i), "parallel body order changed");
            Check(serial.GetPosition(i).X == parallel.GetPosition(i).X && serial.GetPosition(i).Y == parallel.GetPosition(i).Y, "parallel position changed");
            Check(serial.GetVelocity(i).X == parallel.GetVelocity(i).X && serial.GetVelocity(i).Y == parallel.GetVelocity(i).Y, "parallel velocity changed");
        }
        var tracker = new TrackingKernel(); new StructuralIslandBackend(new IslandLink[0], 2, tracker).Compute(batch, CancellationToken.None);
        Check(tracker.Peak > 1 && tracker.Peak <= 2, "scheduler did not respect bounded concurrency: " + tracker.Peak);
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => new StructuralIslandBackend(linksA, 4).Compute(batch, cancelled.Token), "cancellation ignored");
        Reject<AggregateException>(() => new StructuralIslandBackend(new IslandLink[0], 4, new FailingKernel()).Compute(batch, CancellationToken.None), "island failure escaped");
        Check(batch.GetPosition(0).X == 0 && batch.GetPosition(7).X == 7, "failed execution mutated input");
        Reject<ArgumentException>(() => DeterministicIslandDecomposer.Decompose(batch, new[] { new IslandLink(100, 999) }), "foreign endpoint accepted");
        Reject<ArgumentException>(() => new IslandLink(1, 1), "self link accepted");
        Reject<ArgumentOutOfRangeException>(() => new StructuralIslandBackend(linksA, 0), "zero concurrency accepted");
        Console.WriteLine("PASS " + checks + " island scheduler assertions"); return 0;
    }
}
