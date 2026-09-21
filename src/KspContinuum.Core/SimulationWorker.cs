using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace KspContinuum
{
    public sealed class WorkStamp
    {
        public WorkStamp(long tick, long topologyGeneration, long frameGeneration)
        {
            if (tick < 0 || topologyGeneration < 0 || frameGeneration < 0)
                throw new ArgumentException("Work generations and tick must be nonnegative.");
            Tick = tick; TopologyGeneration = topologyGeneration; FrameGeneration = frameGeneration;
        }
        public long Tick { get; private set; }
        public long TopologyGeneration { get; private set; }
        public long FrameGeneration { get; private set; }
        internal bool Matches(WorkStamp other)
        {
            return other != null && Tick == other.Tick && TopologyGeneration == other.TopologyGeneration && FrameGeneration == other.FrameGeneration;
        }
    }
    public sealed class SimulationBody
    {
        public SimulationBody(int id, double mass, Vec position, Vec velocity, Vec force)
        {
            if (id < 0) throw new ArgumentException("Body ID must be nonnegative.", "id");
            AssemblyModel.Positive(mass); Validate(position); Validate(velocity); Validate(force);
            Id = id; Mass = mass; Position = position; Velocity = velocity; Force = force;
        }
        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z);
        }
        public int Id { get; private set; }
        public double Mass { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
        public Vec Force { get; private set; }
    }
    public sealed class SimulationBatch
    {
        public const int MaxBodies = 4096;
        public SimulationBatch(WorkStamp stamp, double stepSeconds, IEnumerable<SimulationBody> bodies)
        {
            if (stamp == null || bodies == null) throw new ArgumentException("Stamp and bodies are required.");
            AssemblyModel.Positive(stepSeconds);
            var copy = new List<SimulationBody>(); var ids = new HashSet<int>();
            foreach (var body in bodies)
            {
                if (copy.Count == MaxBodies) throw new ArgumentException("Batch exceeds body limit.", "bodies");
                if (body == null || !ids.Add(body.Id)) throw new ArgumentException("Bodies must be nonnull with unique IDs.", "bodies");
                copy.Add(body);
            }
            if (copy.Count == 0) throw new ArgumentException("At least one body is required.", "bodies");
            Stamp = stamp; StepSeconds = stepSeconds; Bodies = new ReadOnlyCollection<SimulationBody>(copy);
        }
        public WorkStamp Stamp { get; private set; }
        public double StepSeconds { get; private set; }
        public IReadOnlyList<SimulationBody> Bodies { get; private set; }
    }
    public interface ISimulationBackend { SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation); }
    public sealed class ConstantForceBackend : ISimulationBackend
    {
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            if (batch == null) throw new ArgumentException("Batch is required.", "batch");
            var result = new SimulationBody[batch.Bodies.Count];
            double dt = batch.StepSeconds;
            for (int i = 0; i < result.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var b = batch.Bodies[i];
                var acceleration = new Vec(b.Force.X / b.Mass, b.Force.Y / b.Mass, b.Force.Z / b.Mass);
                var deltaVelocity = acceleration * dt;
                var position = b.Position + b.Velocity * dt + deltaVelocity * (0.5 * dt);
                result[i] = new SimulationBody(b.Id, b.Mass, position, b.Velocity + deltaVelocity, b.Force);
            }
            return new SimulationBatch(batch.Stamp, dt, result);
        }
    }
    public enum SubmitStatus { Accepted, Busy, Rejected, Faulted, Disposed }
    public enum ResultStatus { Empty, Pending, Ready, Stale, Faulted, Disposed }
    public sealed class SimulationWorker : IDisposable
    {
        readonly object sync = new object();
        readonly ISimulationBackend backend;
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        SimulationBatch pending, completed;
        Exception fault;
        bool busy, disposed;
        long lastTick = -1;
        int lifetimeOwners = 2;

        public SimulationWorker(ISimulationBackend backend)
        {
            if (backend == null) { cancellation.Dispose(); throw new ArgumentException("Backend is required.", "backend"); }
            this.backend = backend;
            var thread = new Thread(Run) { IsBackground = true, Name = "Continuum simulation worker" };
            try { thread.Start(); } catch { cancellation.Dispose(); throw; }
        }
        public Exception Fault { get { lock (sync) return fault; } }
        public SubmitStatus TrySubmit(SimulationBatch batch)
        {
            lock (sync)
            {
                if (disposed) return SubmitStatus.Disposed;
                if (fault != null) return SubmitStatus.Faulted;
                if (busy) return SubmitStatus.Busy;
                if (batch == null || batch.Stamp.Tick <= lastTick) return SubmitStatus.Rejected;
                lastTick = batch.Stamp.Tick; pending = batch; busy = true;
                Monitor.Pulse(sync);
                return SubmitStatus.Accepted;
            }
        }
        public ResultStatus TryTake(WorkStamp expectedStamp, out SimulationBatch result)
        {
            if (expectedStamp == null) throw new ArgumentException("Expected stamp is required.", "expectedStamp");
            lock (sync)
            {
                result = null;
                if (disposed) return ResultStatus.Disposed;
                if (fault != null) return ResultStatus.Faulted;
                if (!busy) return ResultStatus.Empty;
                if (completed == null) return ResultStatus.Pending;
                var candidate = completed; completed = null; busy = false;
                if (!candidate.Stamp.Matches(expectedStamp)) return ResultStatus.Stale;
                result = candidate;
                return ResultStatus.Ready;
            }
        }
        void Run()
        {
            try
            {
                while (true)
                {
                    SimulationBatch input;
                    lock (sync)
                    {
                        while (!disposed && pending == null) Monitor.Wait(sync);
                        if (disposed) return;
                        input = pending; pending = null;
                    }
                    var result = backend.Compute(input, cancellation.Token);
                    ValidateResult(input, result);
                    lock (sync)
                    {
                        if (disposed) return;
                        completed = result;
                    }
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    if (!disposed) { fault = error; busy = false; pending = null; completed = null; }
                }
            }
            finally { ReleaseLifetime(); }
        }
        static void ValidateResult(SimulationBatch input, SimulationBatch result)
        {
            if (result == null || !input.Stamp.Matches(result.Stamp) || input.StepSeconds != result.StepSeconds || input.Bodies.Count != result.Bodies.Count)
                throw new InvalidOperationException("Backend changed the batch contract.");
            for (int i = 0; i < input.Bodies.Count; i++)
            {
                var a = input.Bodies[i]; var b = result.Bodies[i];
                if (a.Id != b.Id || a.Mass != b.Mass || a.Force.X != b.Force.X || a.Force.Y != b.Force.Y || a.Force.Z != b.Force.Z)
                    throw new InvalidOperationException("Backend changed body identity, ordering, mass, or force.");
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true; busy = false; pending = null; completed = null;
                Monitor.Pulse(sync);
            }
            // User backend cancellation callbacks must not run on the caller's thread.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { cancellation.Cancel(); }
                catch (Exception error) { lock (sync) { if (fault == null) fault = error; } }
                finally { ReleaseLifetime(); }
            });
        }
        void ReleaseLifetime()
        {
            // The compute thread and cancellation dispatch each release one owner.
            if (Interlocked.Decrement(ref lifetimeOwners) == 0) cancellation.Dispose();
        }
    }
}
