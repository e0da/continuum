using System;
using System.Collections.Generic;
using System.Threading;

namespace KspContinuum
{
    public interface IActivePhysicsForceSource
    {
        Vec ForceFor(ActivePhysicsBody body);
    }

    public enum ActivePhysicsWorkloadStatus
    {
        Accepted, Busy, Rejected, Empty, Pending, Verified, Stale,
        TopologyChanged, FrameChanged, Aborted, Indeterminate, Faulted, Disposed
    }

    // Main-thread adapter from a live host snapshot to off-thread Continuum work and
    // back through the transactional publication boundary. It owns no Unity objects.
    public sealed class ActivePhysicsWorkloadAdapter : IDisposable
    {
        readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        readonly IActivePhysicsDriver driver;
        readonly SimulationWorker worker;
        readonly TakeoverCoordinator takeover;
        ActivePhysicsSnapshot submitted;
        bool disposed;

        public ActivePhysicsWorkloadAdapter(IActivePhysicsDriver driver, ISimulationBackend backend)
        {
            if (driver == null) throw new ArgumentNullException("driver");
            if (backend == null) throw new ArgumentNullException("backend");
            this.driver = driver;
            worker = new SimulationWorker(backend);
            takeover = new TakeoverCoordinator(driver);
        }

        public Exception Fault { get { return worker.Fault; } }

        public ActivePhysicsWorkloadStatus TrySubmit(
            ActivePhysicsStamp expected, double stepSeconds, IActivePhysicsForceSource forces)
        {
            RequireThread();
            if (disposed) return ActivePhysicsWorkloadStatus.Disposed;
            if (expected == null || forces == null) return ActivePhysicsWorkloadStatus.Rejected;

            ActivePhysicsSnapshot captured;
            try { captured = driver.Capture(); }
            catch (Exception) { return ActivePhysicsWorkloadStatus.Rejected; }
            ActivePhysicsWorkloadStatus stampStatus = Compare(expected, captured.Stamp);
            if (stampStatus != ActivePhysicsWorkloadStatus.Accepted) return stampStatus;

            var bodies = new List<SimulationBody>(captured.Bodies.Count);
            try
            {
                foreach (ActivePhysicsBody body in captured.Bodies)
                    bodies.Add(new SimulationBody(body.Id, body.Mass, body.Position, body.Velocity, forces.ForceFor(body)));
            }
            catch (Exception) { return ActivePhysicsWorkloadStatus.Rejected; }

            SimulationBatch batch;
            try
            {
                batch = new SimulationBatch(
                    new WorkStamp(expected.Tick, expected.TopologyGeneration, expected.FrameGeneration),
                    stepSeconds,
                    bodies);
            }
            catch (ArgumentException) { return ActivePhysicsWorkloadStatus.Rejected; }
            SubmitStatus status = worker.TrySubmit(batch);
            if (status == SubmitStatus.Accepted) submitted = captured;
            return Map(status);
        }

        public ActivePhysicsWorkloadStatus TryPublish(
            ActivePhysicsStamp expected, out TakeoverReceipt receipt)
        {
            RequireThread(); receipt = null;
            if (disposed) return ActivePhysicsWorkloadStatus.Disposed;
            if (submitted == null) return ActivePhysicsWorkloadStatus.Empty;
            if (expected == null) return ActivePhysicsWorkloadStatus.Rejected;

            ActivePhysicsWorkloadStatus expectedStatus = Compare(submitted.Stamp, expected);
            var workStamp = new WorkStamp(expected.Tick, expected.TopologyGeneration, expected.FrameGeneration);
            SimulationBatch result;
            ResultStatus resultStatus = worker.TryTake(workStamp, out result);
            if (resultStatus == ResultStatus.Pending) return ActivePhysicsWorkloadStatus.Pending;
            if (resultStatus != ResultStatus.Ready)
            {
                ActivePhysicsSnapshot abandoned = submitted; submitted = null;
                if (resultStatus == ResultStatus.Stale)
                    return Compare(abandoned.Stamp, expected);
                return Map(resultStatus);
            }

            ActivePhysicsSnapshot source = submitted; submitted = null;
            if (expectedStatus != ActivePhysicsWorkloadStatus.Accepted) return expectedStatus;
            var desiredBodies = new ActivePhysicsBody[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                ActivePhysicsBody original = source.Bodies[i];
                if (original.Id != result.GetId(i) || original.Mass != result.GetMass(i))
                    return ActivePhysicsWorkloadStatus.Rejected;
                desiredBodies[i] = new ActivePhysicsBody(
                    original.Id, original.Generation, original.Mass,
                    result.GetPosition(i), result.GetVelocity(i));
            }
            var desired = new ActivePhysicsSnapshot(source.Stamp, desiredBodies);
            TakeoverTransaction transaction;
            TakeoverStatus prepared = takeover.Prepare(source, desired, out transaction);
            if (prepared != TakeoverStatus.Prepared) return Map(prepared);
            receipt = transaction.Apply(transaction.Authority);
            return Map(receipt.Status);
        }

        public void Dispose()
        {
            RequireThread();
            if (disposed) return;
            disposed = true; submitted = null; worker.Dispose();
        }

        static ActivePhysicsWorkloadStatus Compare(ActivePhysicsStamp expected, ActivePhysicsStamp actual)
        {
            if (actual == null) return ActivePhysicsWorkloadStatus.Stale;
            if (actual.TopologyGeneration != expected.TopologyGeneration) return ActivePhysicsWorkloadStatus.TopologyChanged;
            if (actual.FrameGeneration != expected.FrameGeneration || actual.Frame != expected.Frame)
                return ActivePhysicsWorkloadStatus.FrameChanged;
            return actual.Tick == expected.Tick ? ActivePhysicsWorkloadStatus.Accepted : ActivePhysicsWorkloadStatus.Stale;
        }

        static ActivePhysicsWorkloadStatus Map(SubmitStatus status)
        {
            switch (status)
            {
                case SubmitStatus.Accepted: return ActivePhysicsWorkloadStatus.Accepted;
                case SubmitStatus.Busy: return ActivePhysicsWorkloadStatus.Busy;
                case SubmitStatus.Faulted: return ActivePhysicsWorkloadStatus.Faulted;
                case SubmitStatus.Disposed: return ActivePhysicsWorkloadStatus.Disposed;
                default: return ActivePhysicsWorkloadStatus.Rejected;
            }
        }

        static ActivePhysicsWorkloadStatus Map(ResultStatus status)
        {
            switch (status)
            {
                case ResultStatus.Empty: return ActivePhysicsWorkloadStatus.Empty;
                case ResultStatus.Pending: return ActivePhysicsWorkloadStatus.Pending;
                case ResultStatus.Faulted: return ActivePhysicsWorkloadStatus.Faulted;
                case ResultStatus.Disposed: return ActivePhysicsWorkloadStatus.Disposed;
                default: return ActivePhysicsWorkloadStatus.Stale;
            }
        }

        static ActivePhysicsWorkloadStatus Map(TakeoverStatus status)
        {
            switch (status)
            {
                case TakeoverStatus.Verified: return ActivePhysicsWorkloadStatus.Verified;
                case TakeoverStatus.Stale: return ActivePhysicsWorkloadStatus.Stale;
                case TakeoverStatus.TopologyChanged: return ActivePhysicsWorkloadStatus.TopologyChanged;
                case TakeoverStatus.FrameChanged: return ActivePhysicsWorkloadStatus.FrameChanged;
                case TakeoverStatus.Aborted: return ActivePhysicsWorkloadStatus.Aborted;
                case TakeoverStatus.Indeterminate: return ActivePhysicsWorkloadStatus.Indeterminate;
                case TakeoverStatus.Busy: return ActivePhysicsWorkloadStatus.Busy;
                default: return ActivePhysicsWorkloadStatus.Rejected;
            }
        }

        void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Active-physics workload changed host threads.");
        }
    }
}
