using System;
using System.Collections.Generic;
using System.Threading;
using KspContinuum;

static class Program
{
    sealed class Driver : IActivePhysicsDriver
    {
        ActivePhysicsSnapshot state;
        public int Writes;
        public Driver(ActivePhysicsSnapshot state) { this.state = state; }
        public ActivePhysicsSnapshot Capture() { return state; }
        public bool TryWrite(ActivePhysicsBody body)
        {
            Writes++;
            var bodies = new List<ActivePhysicsBody>(state.Bodies);
            for (int i = 0; i < bodies.Count; i++) if (bodies[i].Id == body.Id) bodies[i] = body;
            state = new ActivePhysicsSnapshot(state.Stamp, bodies); return true;
        }
        public void Set(ActivePhysicsSnapshot value) { state = value; }
    }
    sealed class Gravity : IActivePhysicsForceSource
    {
        public Vec ForceFor(ActivePhysicsBody body) { return new Vec(0, -9.5 * body.Mass, 0); }
    }
    sealed class GateBackend : ISimulationBackend
    {
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        { Entered.Set(); Release.Wait(cancellation); return new ConstantForceBackend().Compute(batch, cancellation); }
    }

    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static ActivePhysicsStamp Stamp(long tick = 4, long topology = 7, long frame = 9, string name = "minmus-local")
    { return new ActivePhysicsStamp(tick, topology, frame, name); }
    static ActivePhysicsSnapshot Snapshot(ActivePhysicsStamp stamp, double y = 100, double velocity = 0)
    { return new ActivePhysicsSnapshot(stamp, new[] { new ActivePhysicsBody(12, 3, 2, new Vec(0, y, 0), new Vec(0, velocity, 0)) }); }
    static ActivePhysicsWorkloadStatus Await(ActivePhysicsWorkloadAdapter adapter, ActivePhysicsStamp stamp, out TakeoverReceipt receipt)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5); ActivePhysicsWorkloadStatus status;
        do { status = adapter.TryPublish(stamp, out receipt); if (status != ActivePhysicsWorkloadStatus.Pending) return status; Thread.Yield(); }
        while (DateTime.UtcNow < limit);
        throw new Exception("adapter result timed out");
    }

    static void PublishesSolvedState()
    {
        var stamp = Stamp(); var driver = new Driver(Snapshot(stamp));
        using (var adapter = new ActivePhysicsWorkloadAdapter(driver, new ConstantForceBackend()))
        {
            Check(adapter.TrySubmit(stamp, 2, new Gravity()) == ActivePhysicsWorkloadStatus.Accepted, "work rejected");
            TakeoverReceipt receipt;
            Check(Await(adapter, stamp, out receipt) == ActivePhysicsWorkloadStatus.Verified, "publication not verified");
            ActivePhysicsBody body = driver.Capture().Bodies[0];
            Check(body.Position.Y == 81 && body.Velocity.Y == -19, "nonempty dynamics result not applied");
            Check(driver.Writes == 1 && receipt.Phase == TakeoverPhase.Verified, "write was not transactionally verified");
        }
    }

    static void RejectsChangedAuthority()
    {
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            ActivePhysicsStamp admitted = Stamp(); var driver = new Driver(Snapshot(admitted)); var gate = new GateBackend();
            using (var adapter = new ActivePhysicsWorkloadAdapter(driver, gate))
            {
                Check(adapter.TrySubmit(admitted, 1, new Gravity()) == ActivePhysicsWorkloadStatus.Accepted, "stale case not submitted");
                Check(gate.Entered.Wait(5000), "backend did not start");
                ActivePhysicsStamp changed = kind == 0 ? Stamp(5) : kind == 1 ? Stamp(4, 8) : kind == 2 ? Stamp(4, 7, 10) : Stamp(4, 7, 9, "kerbin-local");
                driver.Set(Snapshot(changed)); gate.Release.Set();
                TakeoverReceipt receipt; ActivePhysicsWorkloadStatus status = Await(adapter, changed, out receipt);
                ActivePhysicsWorkloadStatus expected = kind == 0 ? ActivePhysicsWorkloadStatus.Stale : kind == 1 ? ActivePhysicsWorkloadStatus.TopologyChanged : ActivePhysicsWorkloadStatus.FrameChanged;
                Check(status == expected && receipt == null && driver.Writes == 0, "changed authority escaped: " + kind);
            }
        }
    }

    static void RejectsSameStampMutation()
    {
        ActivePhysicsStamp stamp = Stamp(); var driver = new Driver(Snapshot(stamp)); var gate = new GateBackend();
        using (var adapter = new ActivePhysicsWorkloadAdapter(driver, gate))
        {
            Check(adapter.TrySubmit(stamp, 1, new Gravity()) == ActivePhysicsWorkloadStatus.Accepted, "mutation case not submitted");
            Check(gate.Entered.Wait(5000), "mutation backend did not start");
            driver.Set(Snapshot(stamp, 99, 1)); gate.Release.Set();
            TakeoverReceipt receipt;
            Check(Await(adapter, stamp, out receipt) == ActivePhysicsWorkloadStatus.Stale, "same-stamp mutation escaped");
            Check(receipt == null && driver.Writes == 0, "same-stamp mutation wrote host state");
        }
    }

    static void Main()
    {
        PublishesSolvedState(); RejectsChangedAuthority(); RejectsSameStampMutation();
        Console.WriteLine("PASS " + checks + " adapter assertions");
    }
}
