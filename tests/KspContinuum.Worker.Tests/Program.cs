using System;
using System.Collections.Generic;
using System.Threading;
using KspContinuum;

static partial class Program
{
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Near(double expected, double actual) { Check(!double.IsNaN(actual) && Math.Abs(expected - actual) <= 1e-10 * Math.Max(1, Math.Abs(expected)), "Expected " + expected + ", got " + actual); }
    static void Reject(Action action) { assertions++; try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid input accepted"); }
    static WorkStamp Stamp(long tick = 1, long topology = 2, long frame = 3) { return new WorkStamp(tick, topology, frame); }
    static SimulationBody Body(int id = 7) { return new SimulationBody(id, 2, new Vec(1, 2, 3), new Vec(4, 0, -2), new Vec(2, 4, 0)); }
    static SimulationBatch Batch(long tick = 1) { return new SimulationBatch(Stamp(tick), 2, new[] { Body() }); }
    static ResultStatus Await(SimulationWorker worker, WorkStamp expected, out SimulationBatch result)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        ResultStatus status;
        do { status = worker.TryTake(expected, out result); if (status != ResultStatus.Pending) return status; Thread.Yield(); } while (DateTime.UtcNow < end);
        throw new Exception("Worker did not finish");
    }
    sealed class GateBackend : ISimulationBackend
    {
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Exited = new ManualResetEventSlim();
        public int ThreadId;
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            ThreadId = Thread.CurrentThread.ManagedThreadId; Entered.Set();
            try { Release.Wait(cancellation); return new ConstantForceBackend().Compute(batch, cancellation); }
            finally { Exited.Set(); }
        }
    }
    sealed class BadBackend : ISimulationBackend
    {
        readonly int kind;
        public BadBackend(int kind) { this.kind = kind; }
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            if (kind == 0) throw new InvalidOperationException("witness backend failure");
            if (kind == 1) return null;
            if (kind == 2) return new SimulationBatch(Stamp(999), batch.StepSeconds, batch.Bodies);
            if (kind == 3) return new SimulationBatch(batch.Stamp, batch.StepSeconds, new[] { Body(99) });
            if (kind == 4) return new SimulationBatch(batch.Stamp, batch.StepSeconds + 1, batch.Bodies);
            if (kind == 5) return new SimulationBatch(batch.Stamp, batch.StepSeconds, new[] { new SimulationBody(7, 9, new Vec(), new Vec(), new Vec()) });
            if (kind == 6) return new SimulationBatch(Stamp(batch.Stamp.Tick, 99), batch.StepSeconds, batch.Bodies);
            if (kind == 7) return new SimulationBatch(Stamp(batch.Stamp.Tick, 2, 99), batch.StepSeconds, batch.Bodies);
            return new SimulationBatch(batch.Stamp, batch.StepSeconds, new[] { new SimulationBody(7, 2, new Vec(), new Vec(), new Vec(99, 4, 0)) });
        }
    }
    sealed class UncooperativeBackend : ISimulationBackend
    {
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Exited = new ManualResetEventSlim();
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            Entered.Set(); Release.Wait(); Exited.Set(); return batch;
        }
    }
    static void Main()
    {
        TestColumns();
        var source = new[] { Body() };
        var batch = new SimulationBatch(Stamp(), 2, source);
        source[0] = Body(99);
        var position = batch.Bodies[0].Position; position.X = 100;
        Check(batch.Bodies[0].Id == 7 && batch.Bodies[0].Position.X == 1, "Input was not copied");
        Check(!(batch.Bodies is SimulationBody[]), "Mutable array exposed");
        try { ((IList<SimulationBody>)batch.Bodies)[0] = Body(99); throw new Exception("Read-only collection was writable"); }
        catch (NotSupportedException) { assertions++; }
        var solved = new ConstantForceBackend().Compute(batch, CancellationToken.None);
        Near(11, solved.Bodies[0].Position.X); Near(6, solved.Bodies[0].Position.Y); Near(-1, solved.Bodies[0].Position.Z);
        Near(6, solved.Bodies[0].Velocity.X); Near(4, solved.Bodies[0].Velocity.Y); Near(-2, solved.Bodies[0].Velocity.Z);
        Near(1, batch.Bodies[0].Position.X);
        var half = new ConstantForceBackend().Compute(new SimulationBatch(Stamp(), 1, batch.Bodies), CancellationToken.None);
        var halves = new ConstantForceBackend().Compute(new SimulationBatch(Stamp(2), 1, half.Bodies), CancellationToken.None);
        Near(solved.Bodies[0].Position.X, halves.Bodies[0].Position.X);
        Near(solved.Bodies[0].Position.Y, halves.Bodies[0].Position.Y);
        Reject(() => new WorkStamp(-1, 0, 0)); Reject(() => new WorkStamp(0, -1, 0)); Reject(() => new WorkStamp(0, 0, -1));
        Reject(() => new SimulationBatch(null, 1, source)); Reject(() => new SimulationBatch(Stamp(), 1, null));
        Reject(() => new SimulationBatch(Stamp(), 1, new SimulationBody[0]));
        Reject(() => new SimulationBatch(Stamp(), 1, new SimulationBody[] { null }));
        Reject(() => new SimulationBatch(Stamp(), 1, new[] { Body(), Body() }));
        var tooMany = new List<SimulationBody>(); for (int i = 0; i <= SimulationBatch.MaxBodies; i++) tooMany.Add(Body(i));
        Reject(() => new SimulationBatch(Stamp(), 1, tooMany));
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Reject(() => new SimulationBatch(Stamp(), bad, source));
            Reject(() => new SimulationBody(1, bad, new Vec(), new Vec(), new Vec()));
        }
        Reject(() => new SimulationBody(-1, 1, new Vec(), new Vec(), new Vec()));
        Reject(() => new SimulationBody(1, 1, new Vec(double.NaN, 0, 0), new Vec(), new Vec()));
        Reject(() => new SimulationBody(1, 1, new Vec(), new Vec(0, double.PositiveInfinity, 0), new Vec()));
        Reject(() => new SimulationBody(1, 1, new Vec(), new Vec(), new Vec(0, 0, double.NaN)));
        Reject(() => new SimulationWorker(null));

        var gate = new GateBackend();
        using (var worker = new SimulationWorker(gate))
        {
            SimulationBatch output;
            Check(worker.TryTake(Stamp(), out output) == ResultStatus.Empty && output == null, "Fresh worker not empty");
            Check(worker.TrySubmit(null) == SubmitStatus.Rejected, "Null submission accepted");
            Check(worker.TrySubmit(batch) == SubmitStatus.Accepted, "First work refused");
            Check(gate.Entered.Wait(5000), "Backend never entered");
            Check(gate.ThreadId != Thread.CurrentThread.ManagedThreadId, "Compute ran on caller thread");
            Check(worker.TrySubmit(Batch(2)) == SubmitStatus.Busy, "Running queue overflow accepted");
            Check(worker.TryTake(Stamp(), out output) == ResultStatus.Pending && output == null, "Pending exposed a result");
            gate.Release.Set(); Check(gate.Exited.Wait(5000), "Backend never exited");
            Check(Await(worker, Stamp(), out output) == ResultStatus.Ready, "Matching result rejected");
            Near(11, output.Bodies[0].Position.X);
            Check(worker.TryTake(Stamp(), out output) == ResultStatus.Empty && output == null, "Result delivered twice");
            Check(worker.TrySubmit(batch) == SubmitStatus.Rejected, "Duplicate tick accepted");
            Check(worker.TrySubmit(Batch(0)) == SubmitStatus.Rejected, "Backward tick accepted");
            long nextTick = 2;
            foreach (var expected in new[] { Stamp(99), Stamp(3, 99), Stamp(4, 2, 99) })
            {
                Check(worker.TrySubmit(Batch(nextTick++)) == SubmitStatus.Accepted, "Fresh submission refused");
                Check(Await(worker, expected, out output) == ResultStatus.Stale && output == null, "Stale result escaped");
            }
        }
        for (int kind = 0; kind < 9; kind++)
        using (var worker = new SimulationWorker(new BadBackend(kind)))
        {
            SimulationBatch output;
            Check(worker.TrySubmit(Batch()) == SubmitStatus.Accepted, "Fault test not submitted");
            Check(Await(worker, Stamp(), out output) == ResultStatus.Faulted && output == null, "Invalid backend result escaped");
            Check(worker.Fault != null, "Fault diagnosis missing");
            Check(worker.TrySubmit(Batch(2)) == SubmitStatus.Faulted, "Faulted worker accepted work");
        }
        var cancellationGate = new GateBackend(); var disposable = new SimulationWorker(cancellationGate);
        Check(disposable.TrySubmit(Batch()) == SubmitStatus.Accepted, "Dispose test not submitted");
        Check(cancellationGate.Entered.Wait(5000), "Dispose backend never entered");
        disposable.Dispose(); disposable.Dispose();
        Check(cancellationGate.Exited.Wait(5000), "Dispose did not cancel backend");
        SimulationBatch discarded;
        Check(disposable.TryTake(Stamp(), out discarded) == ResultStatus.Disposed && discarded == null, "Disposed result escaped");
        Check(disposable.TrySubmit(Batch(2)) == SubmitStatus.Disposed, "Disposed worker accepted work");
        var uncooperative = new UncooperativeBackend(); var abandoned = new SimulationWorker(uncooperative);
        Check(abandoned.TrySubmit(Batch()) == SubmitStatus.Accepted && uncooperative.Entered.Wait(5000), "Late-result test not entered");
        try
        {
            var disposed = new ManualResetEventSlim();
            var disposer = new Thread(() => { abandoned.Dispose(); disposed.Set(); }) { IsBackground = true };
            disposer.Start(); Check(disposed.Wait(5000), "Dispose waited for uncooperative compute");
            Check(abandoned.TryTake(Stamp(), out discarded) == ResultStatus.Disposed && discarded == null, "Abandoned work published");
        }
        finally { uncooperative.Release.Set(); abandoned.Dispose(); }
        Check(uncooperative.Exited.Wait(5000), "Test backend did not exit");
        Check(abandoned.TryTake(Stamp(), out discarded) == ResultStatus.Disposed && discarded == null, "Late output escaped disposal");
        var idle = new SimulationWorker(new ConstantForceBackend()); idle.Dispose();
        Check(idle.TrySubmit(Batch()) == SubmitStatus.Disposed, "Disposed idle worker accepted work");
        using (var worker = new SimulationWorker(new ConstantForceBackend()))
        {
            var overflow = new SimulationBatch(Stamp(), 2, new[] { new SimulationBody(1, 1, new Vec(double.MaxValue, 0, 0), new Vec(double.MaxValue, 0, 0), new Vec()) });
            Check(worker.TrySubmit(overflow) == SubmitStatus.Accepted, "Finite overflow input refused");
            Check(Await(worker, Stamp(), out discarded) == ResultStatus.Faulted, "Numerical overflow escaped");
        }
        Console.WriteLine("PASS " + assertions + " worker assertions");
    }
}
