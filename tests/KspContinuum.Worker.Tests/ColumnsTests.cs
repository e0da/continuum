using System;
using System.Collections.Generic;
using System.Threading;
using KspContinuum;

static partial class Program
{
    static SimulationBatch Columns(long tick = 1)
    {
        return SimulationBatch.FromColumns(Stamp(tick), 2, new[] { 7 }, new[] { 2.0 },
            new[] { new Vec(1, 2, 3) }, new[] { new Vec(4, 0, -2) }, new[] { new Vec(2, 4, 0) });
    }
    static void TestColumns()
    {
        var ids = new[] { 7 }; var masses = new[] { 2.0 };
        var positions = new[] { new Vec(1, 2, 3) }; var velocities = new[] { new Vec(4, 0, -2) };
        var forces = new[] { new Vec(2, 4, 0) };
        var batch = SimulationBatch.FromColumns(Stamp(), 2, ids, masses, positions, velocities, forces);
        ids[0] = 99; masses[0] = 99; positions[0].X = 99; velocities[0].Y = 99; forces[0].Z = 99;
        Check(batch.UsesColumnStorage && batch.Count == 1 && batch.GetId(0) == 7, "Column identity aliased capture arrays");
        Near(2, batch.GetMass(0)); Near(1, batch.GetPosition(0).X); Near(0, batch.GetVelocity(0).Y); Near(0, batch.GetForce(0).Z);
        var copy = batch.GetPosition(0); copy.X = 100;
        Near(1, batch.GetPosition(0).X);
        var solved = new ConstantForceBackend().Compute(batch, CancellationToken.None);
        Check(solved.UsesColumnStorage, "Column backend materialized object output");
        Near(11, solved.GetPosition(0).X); Near(6, solved.GetPosition(0).Y); Near(-1, solved.GetPosition(0).Z);
        Near(6, solved.GetVelocity(0).X); Near(4, solved.GetVelocity(0).Y); Near(-2, solved.GetVelocity(0).Z);
        Check(solved.GetId(0) == 7 && solved.GetMass(0) == 2, "Column result envelope changed");
        Near(1, batch.GetPosition(0).X);
        var views = new IReadOnlyList<SimulationBody>[2];
        var a = new Thread(() => views[0] = solved.Bodies); var b = new Thread(() => views[1] = solved.Bodies);
        a.Start(); b.Start(); a.Join(); b.Join();
        Check(object.ReferenceEquals(views[0], views[1]), "Compatibility view not cached atomically");
        Check(!(views[0] is SimulationBody[]), "Compatibility view exposed array");
        try { ((IList<SimulationBody>)views[0])[0] = Body(99); throw new Exception("Column compatibility view writable"); }
        catch (NotSupportedException) { assertions++; }
        Near(11, solved.Bodies[0].Position.X);

        Reject(() => SimulationBatch.FromColumns(null, 1, ids, masses, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 0, ids, masses, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, null, masses, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, null, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, masses, null, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, masses, positions, null, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, masses, positions, velocities, null));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, new double[0], positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, new int[0], new double[0], new Vec[0], new Vec[0], new Vec[0]));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, new[] { 7, 7 }, new[] { 2.0, 3.0 }, new Vec[2], new Vec[2], new Vec[2]));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, new[] { -1 }, masses, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), 1, new int[4097], new double[4097], new Vec[4097], new Vec[4097], new Vec[4097]));
        foreach (var invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, new[] { invalid }, positions, velocities, forces));
        Reject(() => SimulationBatch.FromColumns(Stamp(), double.NaN, ids, masses, positions, velocities, forces));
        foreach (var target in new[] { positions, velocities, forces })
        {
            var saved = target[0]; target[0] = new Vec(0, double.NaN, 0);
            Reject(() => SimulationBatch.FromColumns(Stamp(), 1, ids, masses, positions, velocities, forces));
            target[0] = saved;
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { new ConstantForceBackend().Compute(batch, cancelled.Token); throw new Exception("Column compute ignored cancellation"); }
            catch (OperationCanceledException) { assertions++; }
        }
        using (var worker = new SimulationWorker(new ConstantForceBackend()))
        {
            SimulationBatch output;
            Check(worker.TrySubmit(batch) == SubmitStatus.Accepted, "Column submission refused");
            Check(Await(worker, Stamp(), out output) == ResultStatus.Ready && output.UsesColumnStorage, "Worker converted column handoff");
            Near(11, output.GetPosition(0).X);
            Check(worker.TrySubmit(Columns(2)) == SubmitStatus.Accepted, "Next column submission refused");
            Check(Await(worker, Stamp(2, 99), out output) == ResultStatus.Stale && output == null, "Stale column result escaped");
        }
        for (int kind = 0; kind < 9; kind++)
        using (var worker = new SimulationWorker(new BadBackend(kind)))
        {
            SimulationBatch output;
            Check(worker.TrySubmit(Columns()) == SubmitStatus.Accepted, "Bad backend column test refused");
            Check(Await(worker, Stamp(), out output) == ResultStatus.Faulted && output == null, "Invalid cross-layout result escaped");
        }
        using (var worker = new SimulationWorker(new ConstantForceBackend()))
        {
            var overflow = SimulationBatch.FromColumns(Stamp(), 2, new[] { 1 }, new[] { 1.0 },
                new[] { new Vec(double.MaxValue, 0, 0) }, new[] { new Vec(double.MaxValue, 0, 0) }, new[] { new Vec() });
            SimulationBatch output;
            Check(worker.TrySubmit(overflow) == SubmitStatus.Accepted, "Column overflow case refused");
            Check(Await(worker, Stamp(), out output) == ResultStatus.Faulted, "Nonfinite column output escaped");
        }
    }
}
