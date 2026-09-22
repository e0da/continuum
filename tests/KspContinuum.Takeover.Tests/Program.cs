using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    sealed class MemoryDriver : IActivePhysicsDriver
    {
        ActivePhysicsStamp stamp; readonly SortedDictionary<int, ActivePhysicsBody> bodies = new SortedDictionary<int, ActivePhysicsBody>();
        public int Captures, Writes, FailCaptureAt, CorruptCaptureAt, FailWriteAt, RejectRollbackAfter;
        public bool MutateOnFailedWrite;
        public MemoryDriver(ActivePhysicsSnapshot initial) { Set(initial); }
        public void Set(ActivePhysicsSnapshot snapshot)
        {
            stamp = snapshot.Stamp; bodies.Clear(); foreach (var body in snapshot.Bodies) bodies.Add(body.Id, body);
        }
        public void SetStamp(ActivePhysicsStamp value) { stamp = value; }
        public ActivePhysicsSnapshot Capture()
        {
            Captures++; if (Captures == FailCaptureAt) throw new InvalidOperationException("capture failure");
            var copy = new List<ActivePhysicsBody>(bodies.Values);
            if (Captures == CorruptCaptureAt)
            {
                var b = copy[0]; copy[0] = Body(b.Id, b.Generation, b.Mass, b.Position.X + 99);
            }
            return new ActivePhysicsSnapshot(stamp, copy);
        }
        public bool TryWrite(ActivePhysicsBody body)
        {
            Writes++;
            bool fail = Writes == FailWriteAt || (RejectRollbackAfter > 0 && Writes >= RejectRollbackAfter);
            if (!fail || MutateOnFailedWrite) bodies[body.Id] = body;
            return !fail;
        }
    }

    static int assertions;
    static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
    static ActivePhysicsStamp Stamp(long tick = 7, long topology = 11, long frame = 13, string key = "kerbin-local")
    { return new ActivePhysicsStamp(tick, topology, frame, key); }
    static ActivePhysicsBody Body(int id, long generation, double mass, double x)
    { return new ActivePhysicsBody(id, generation, mass, new Vec(x, 0, 0), new Vec(x / 10, 0, 0)); }
    static ActivePhysicsSnapshot Snapshot(ActivePhysicsStamp stamp, double offset = 0)
    { return new ActivePhysicsSnapshot(stamp, new[] { Body(2, 3, 2, 20 + offset), Body(1, 4, 1, 10 + offset) }); }
    static TakeoverTransaction Prepare(MemoryDriver driver, ActivePhysicsSnapshot before, ActivePhysicsSnapshot desired)
    {
        var coordinator = new TakeoverCoordinator(driver); TakeoverTransaction transaction;
        Check(coordinator.Prepare(before.Stamp, desired, out transaction) == TakeoverStatus.Prepared, "prepare");
        Check(transaction.Phase == TakeoverPhase.Prepared && transaction.Before.Bodies[0].Position.X == 10, "immutable before-image");
        return transaction;
    }
    static bool Equal(ActivePhysicsSnapshot a, ActivePhysicsSnapshot b)
    {
        if (!a.Stamp.Matches(b.Stamp) || a.Bodies.Count != b.Bodies.Count) return false;
        for (int i = 0; i < a.Bodies.Count; i++)
            if (a.Bodies[i].Id != b.Bodies[i].Id || a.Bodies[i].Position.X != b.Bodies[i].Position.X ||
                a.Bodies[i].Velocity.X != b.Bodies[i].Velocity.X) return false;
        return true;
    }
    static void SuccessAndAuthority()
    {
        var before = Snapshot(Stamp()); var desired = Snapshot(before.Stamp, 5); var driver = new MemoryDriver(before);
        var coordinator = new TakeoverCoordinator(driver); TakeoverTransaction first, second;
        Check(coordinator.Prepare(before.Stamp, desired, out first) == TakeoverStatus.Prepared, "first authority");
        Check(coordinator.Prepare(before.Stamp, desired, out second) == TakeoverStatus.Busy && second == null, "exclusive authority");
        var foreignDriver = new MemoryDriver(before); var foreign = Prepare(foreignDriver, before, desired);
        var rejected = first.Apply(foreign.Authority);
        Check(rejected.Status == TakeoverStatus.Rejected && first.Phase == TakeoverPhase.Prepared && driver.Writes == 0, "foreign token rejected without consuming authority");
        foreign.Abort(foreign.Authority);
        var receipt = first.Apply(first.Authority);
        Check(receipt.Status == TakeoverStatus.Verified && receipt.Phase == TakeoverPhase.Verified, "verified publication");
        Check(Equal(desired, driver.Capture()), "desired state read back");
        Check(receipt.Writes.Count == 2 && receipt.Writes[0].Attempted && receipt.Writes[0].Accepted, "write journal retained");
        Check(coordinator.Prepare(before.Stamp, desired, out second) == TakeoverStatus.Prepared, "authority released after verification");
        second.Abort(second.Authority);
    }
    static void AdmissionRejection()
    {
        var before = Snapshot(Stamp()); var driver = new MemoryDriver(before); var coordinator = new TakeoverCoordinator(driver); TakeoverTransaction tx;
        Check(coordinator.Prepare(Stamp(8), Snapshot(Stamp(8), 1), out tx) == TakeoverStatus.Stale, "stale tick rejected");
        Check(coordinator.Prepare(Stamp(7, 12), Snapshot(Stamp(7, 12), 1), out tx) == TakeoverStatus.TopologyChanged, "topology rejected");
        Check(coordinator.Prepare(Stamp(7, 11, 14), Snapshot(Stamp(7, 11, 14), 1), out tx) == TakeoverStatus.FrameChanged, "frame generation rejected");
        Check(coordinator.Prepare(Stamp(7, 11, 13, "mun-local"), Snapshot(Stamp(7, 11, 13, "mun-local"), 1), out tx) == TakeoverStatus.FrameChanged, "frame identity rejected");
        var bad = new ActivePhysicsSnapshot(before.Stamp, new[] { Body(1, 4, 99, 1), Body(2, 3, 2, 2) });
        Check(coordinator.Prepare(before.Stamp, bad, out tx) == TakeoverStatus.Rejected, "mass mutation rejected");
    }
    static void Revalidation()
    {
        foreach (var mode in new[] { TakeoverStatus.Stale, TakeoverStatus.TopologyChanged, TakeoverStatus.FrameChanged })
        {
            var before = Snapshot(Stamp()); var driver = new MemoryDriver(before); var tx = Prepare(driver, before, Snapshot(before.Stamp, 1));
            if (mode == TakeoverStatus.Stale) driver.SetStamp(Stamp(8));
            if (mode == TakeoverStatus.TopologyChanged) driver.SetStamp(Stamp(7, 12));
            if (mode == TakeoverStatus.FrameChanged) driver.SetStamp(Stamp(7, 11, 14));
            var receipt = tx.Apply(tx.Authority);
            Check(receipt.Status == mode && receipt.Phase == TakeoverPhase.Aborted && driver.Writes == 0, mode + " revalidation before writes");
        }
        var stateBefore = Snapshot(Stamp()); var changed = Snapshot(stateBefore.Stamp, 50); var changedDriver = new MemoryDriver(stateBefore);
        var changedTx = Prepare(changedDriver, stateBefore, Snapshot(stateBefore.Stamp, 1)); changedDriver.Set(changed);
        Check(changedTx.Apply(changedTx.Authority).Status == TakeoverStatus.Stale && changedDriver.Writes == 0, "same-stamp state change rejected");

        var unreadableDriver = new MemoryDriver(stateBefore); var unreadable = Prepare(unreadableDriver, stateBefore, Snapshot(stateBefore.Stamp, 1));
        unreadableDriver.FailCaptureAt = 2;
        Check(unreadable.Apply(unreadable.Authority).Status == TakeoverStatus.Aborted && unreadableDriver.Writes == 0,
            "failed pre-write capture aborts without writes");
    }
    static void Compensation()
    {
        var before = Snapshot(Stamp()); var desired = Snapshot(before.Stamp, 5);
        var driver = new MemoryDriver(before) { FailWriteAt = 2, MutateOnFailedWrite = true };
        var tx = Prepare(driver, before, desired); var receipt = tx.Apply(tx.Authority);
        Check(receipt.Status == TakeoverStatus.Aborted && receipt.Phase == TakeoverPhase.Aborted, "partial write compensated");
        Check(Equal(before, driver.Capture()), "before-image restored after partial write");
        Check(receipt.Writes[0].RollbackAttempted && receipt.Writes[1].RollbackAttempted, "all attempted writes rolled back");

        driver = new MemoryDriver(before) { CorruptCaptureAt = 3 }; tx = Prepare(driver, before, desired);
        receipt = tx.Apply(tx.Authority);
        Check(receipt.Status == TakeoverStatus.Aborted && Equal(before, driver.Capture()), "mismatched readback compensated");

        driver = new MemoryDriver(before) { FailCaptureAt = 3 }; tx = Prepare(driver, before, desired);
        receipt = tx.Apply(tx.Authority);
        Check(receipt.Status == TakeoverStatus.Aborted && Equal(before, driver.Capture()), "unreadable publication compensated and verified");

        driver = new MemoryDriver(before) { FailWriteAt = 2, RejectRollbackAfter = 3 };
        tx = Prepare(driver, before, desired); receipt = tx.Apply(tx.Authority);
        Check(receipt.Status == TakeoverStatus.Indeterminate && receipt.Phase == TakeoverPhase.Indeterminate, "failed compensation is indeterminate");
        Check(!Equal(before, driver.Capture()), "indeterminate state is not disguised as restored");
    }
    static void ExplicitAbort()
    {
        var before = Snapshot(Stamp()); var driver = new MemoryDriver(before); var tx = Prepare(driver, before, Snapshot(before.Stamp, 1));
        var receipt = tx.Abort(tx.Authority);
        Check(receipt.Status == TakeoverStatus.Aborted && driver.Writes == 0, "explicit abort writes nothing");
    }
    static void Main()
    {
        SuccessAndAuthority(); AdmissionRejection(); Revalidation(); Compensation(); ExplicitAbort();
        Console.WriteLine("Takeover assertions: " + assertions);
    }
}
