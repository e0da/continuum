using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum TakeoverPhase { Prepared, Applying, Verified, Aborted, Indeterminate }
    public enum TakeoverStatus { Prepared, Verified, Aborted, Indeterminate, Busy, Rejected, Stale, TopologyChanged, FrameChanged }

    public sealed class ActivePhysicsStamp
    {
        public ActivePhysicsStamp(long tick, long topologyGeneration, long frameGeneration, string frame)
        {
            if (tick < 0 || topologyGeneration < 0 || frameGeneration < 0 || string.IsNullOrEmpty(frame) || frame.Length > 256)
                throw new ArgumentException("A valid active-physics stamp is required.");
            Tick = tick; TopologyGeneration = topologyGeneration; FrameGeneration = frameGeneration; Frame = frame;
        }
        public long Tick { get; private set; }
        public long TopologyGeneration { get; private set; }
        public long FrameGeneration { get; private set; }
        public string Frame { get; private set; }
        public bool Matches(ActivePhysicsStamp other)
        {
            return other != null && Tick == other.Tick && TopologyGeneration == other.TopologyGeneration &&
                FrameGeneration == other.FrameGeneration && Frame == other.Frame;
        }
    }

    public sealed class ActivePhysicsBody
    {
        public ActivePhysicsBody(int id, long generation, double mass, Vec position, Vec velocity)
        {
            if (id < 0 || generation < 0) throw new ArgumentException("Invalid body identity.");
            AssemblyModel.Positive(mass); Validate(position); Validate(velocity);
            Id = id; Generation = generation; Mass = mass; Position = position; Velocity = velocity;
        }
        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z);
        }
        public int Id { get; private set; }
        public long Generation { get; private set; }
        public double Mass { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
    }

    public sealed class ActivePhysicsSnapshot
    {
        public ActivePhysicsSnapshot(ActivePhysicsStamp stamp, IEnumerable<ActivePhysicsBody> bodies)
        {
            if (stamp == null || bodies == null) throw new ArgumentException("Stamp and bodies are required.");
            var copy = new List<ActivePhysicsBody>(); var ids = new HashSet<int>();
            foreach (var body in bodies)
            {
                if (body == null || !ids.Add(body.Id)) throw new ArgumentException("Body membership must be unique and nonnull.");
                copy.Add(body);
            }
            if (copy.Count == 0) throw new ArgumentException("At least one body is required.");
            copy.Sort((a, b) => a.Id.CompareTo(b.Id));
            Stamp = stamp; Bodies = new ReadOnlyCollection<ActivePhysicsBody>(copy);
        }
        public ActivePhysicsStamp Stamp { get; private set; }
        public IReadOnlyList<ActivePhysicsBody> Bodies { get; private set; }
    }

    public interface IActivePhysicsDriver
    {
        ActivePhysicsSnapshot Capture();
        bool TryWrite(ActivePhysicsBody body);
    }

    public sealed class TakeoverAuthority
    {
        internal TakeoverAuthority(Guid owner, long sequence) { Owner = owner; Sequence = sequence; }
        internal Guid Owner { get; private set; }
        public long Sequence { get; private set; }
    }

    public sealed class TakeoverWrite
    {
        internal TakeoverWrite(ActivePhysicsBody before, ActivePhysicsBody desired)
        { Before = before; Desired = desired; }
        public ActivePhysicsBody Before { get; private set; }
        public ActivePhysicsBody Desired { get; private set; }
        public bool Attempted { get; internal set; }
        public bool Accepted { get; internal set; }
        public bool RollbackAttempted { get; internal set; }
        public bool RollbackAccepted { get; internal set; }
    }

    public sealed class TakeoverReceipt
    {
        internal TakeoverReceipt(TakeoverPhase phase, TakeoverStatus status, string reason,
            ActivePhysicsSnapshot before, IReadOnlyList<TakeoverWrite> writes)
        { Phase = phase; Status = status; Reason = reason; Before = before; Writes = writes; }
        public TakeoverPhase Phase { get; private set; }
        public TakeoverStatus Status { get; private set; }
        public string Reason { get; private set; }
        public ActivePhysicsSnapshot Before { get; private set; }
        public IReadOnlyList<TakeoverWrite> Writes { get; private set; }
    }

    public sealed class TakeoverTransaction
    {
        readonly TakeoverCoordinator owner;
        internal TakeoverTransaction(TakeoverCoordinator owner, TakeoverAuthority authority,
            ActivePhysicsSnapshot before, ActivePhysicsSnapshot desired, List<TakeoverWrite> writes)
        {
            this.owner = owner; Authority = authority; Before = before; Desired = desired;
            Writes = new ReadOnlyCollection<TakeoverWrite>(writes); Phase = TakeoverPhase.Prepared;
        }
        public TakeoverAuthority Authority { get; private set; }
        public ActivePhysicsSnapshot Before { get; private set; }
        public ActivePhysicsSnapshot Desired { get; private set; }
        public IReadOnlyList<TakeoverWrite> Writes { get; private set; }
        public TakeoverPhase Phase { get; internal set; }
        public TakeoverReceipt Apply(TakeoverAuthority authority) { return owner.Apply(this, authority); }
        public TakeoverReceipt Abort(TakeoverAuthority authority) { return owner.Abort(this, authority); }
    }

    public sealed class TakeoverCoordinator
    {
        readonly object sync = new object(); readonly Guid id = Guid.NewGuid(); readonly IActivePhysicsDriver driver;
        long sequence; TakeoverTransaction active;
        public TakeoverCoordinator(IActivePhysicsDriver driver)
        { this.driver = driver ?? throw new ArgumentNullException("driver"); }

        public TakeoverStatus Prepare(ActivePhysicsStamp expected, ActivePhysicsSnapshot desired, out TakeoverTransaction transaction)
        {
            transaction = null;
            lock (sync)
            {
                if (active != null) return TakeoverStatus.Busy;
                if (expected == null || desired == null || !desired.Stamp.Matches(expected)) return TakeoverStatus.Rejected;
                ActivePhysicsSnapshot before;
                try { before = driver.Capture(); }
                catch (Exception) { return TakeoverStatus.Rejected; }
                var mismatch = CompareStamp(expected, before.Stamp);
                if (mismatch != TakeoverStatus.Prepared) return mismatch;
                if (!SameEnvelope(before, desired)) return TakeoverStatus.Rejected;
                var writes = new List<TakeoverWrite>();
                for (int i = 0; i < before.Bodies.Count; i++) writes.Add(new TakeoverWrite(before.Bodies[i], desired.Bodies[i]));
                var authority = new TakeoverAuthority(id, checked(++sequence));
                active = transaction = new TakeoverTransaction(this, authority, before, desired, writes);
                return TakeoverStatus.Prepared;
            }
        }

        internal TakeoverReceipt Apply(TakeoverTransaction transaction, TakeoverAuthority authority)
        {
            lock (sync)
            {
                if (!Owns(transaction, authority) || transaction.Phase != TakeoverPhase.Prepared)
                    return Receipt(transaction, transaction == null ? TakeoverPhase.Aborted : transaction.Phase,
                        TakeoverStatus.Rejected, "Authority token is not current.");
                ActivePhysicsSnapshot current;
                try { current = driver.Capture(); }
                catch (Exception) { return Finish(transaction, TakeoverPhase.Aborted, TakeoverStatus.Aborted, "Pre-write capture failed; no write was attempted."); }
                var mismatch = CompareStamp(transaction.Before.Stamp, current.Stamp);
                if (mismatch != TakeoverStatus.Prepared)
                    return Finish(transaction, TakeoverPhase.Aborted, mismatch, "Authority changed before the first write.");
                if (!SameState(transaction.Before, current))
                    return Finish(transaction, TakeoverPhase.Aborted, TakeoverStatus.Stale, "State changed before the first write.");
                transaction.Phase = TakeoverPhase.Applying;
                foreach (var write in transaction.Writes)
                {
                    write.Attempted = true;
                    try { write.Accepted = driver.TryWrite(write.Desired); }
                    catch (Exception) { write.Accepted = false; }
                    if (!write.Accepted) return Compensate(transaction, "A driver write failed.");
                }
                ActivePhysicsSnapshot readback;
                try { readback = driver.Capture(); }
                catch (Exception) { return Compensate(transaction, "Post-write readback failed."); }
                if (!SameState(transaction.Desired, readback)) return Compensate(transaction, "Post-write readback did not match the desired state.");
                return Finish(transaction, TakeoverPhase.Verified, TakeoverStatus.Verified, "All writes were read back exactly.");
            }
        }

        internal TakeoverReceipt Abort(TakeoverTransaction transaction, TakeoverAuthority authority)
        {
            lock (sync)
            {
                if (!Owns(transaction, authority) || transaction.Phase != TakeoverPhase.Prepared)
                    return Receipt(transaction, transaction == null ? TakeoverPhase.Aborted : transaction.Phase,
                        TakeoverStatus.Rejected, "Authority token is not current.");
                return Finish(transaction, TakeoverPhase.Aborted, TakeoverStatus.Aborted, "Prepared transaction was explicitly aborted.");
            }
        }

        TakeoverReceipt Compensate(TakeoverTransaction transaction, string reason)
        {
            for (int i = transaction.Writes.Count - 1; i >= 0; i--)
            {
                var write = transaction.Writes[i]; if (!write.Attempted) continue;
                write.RollbackAttempted = true;
                try { write.RollbackAccepted = driver.TryWrite(write.Before); }
                catch (Exception) { write.RollbackAccepted = false; }
            }
            ActivePhysicsSnapshot restored = null;
            try { restored = driver.Capture(); } catch (Exception) { }
            if (restored != null && SameState(transaction.Before, restored))
                return Finish(transaction, TakeoverPhase.Aborted, TakeoverStatus.Aborted, reason + " Before-image restoration was verified.");
            return Finish(transaction, TakeoverPhase.Indeterminate, TakeoverStatus.Indeterminate,
                reason + " Before-image restoration could not be verified.");
        }

        TakeoverReceipt Finish(TakeoverTransaction transaction, TakeoverPhase phase, TakeoverStatus status, string reason)
        {
            transaction.Phase = phase; if (object.ReferenceEquals(active, transaction)) active = null;
            return Receipt(transaction, phase, status, reason);
        }
        static TakeoverReceipt Receipt(TakeoverTransaction transaction, TakeoverPhase phase, TakeoverStatus status, string reason)
        {
            return new TakeoverReceipt(phase, status, reason, transaction == null ? null : transaction.Before,
                transaction == null ? new ReadOnlyCollection<TakeoverWrite>(new List<TakeoverWrite>()) : transaction.Writes);
        }
        bool Owns(TakeoverTransaction transaction, TakeoverAuthority authority)
        {
            return transaction != null && authority != null && object.ReferenceEquals(active, transaction) &&
                object.ReferenceEquals(transaction.Authority, authority) && authority.Owner == id;
        }
        static TakeoverStatus CompareStamp(ActivePhysicsStamp expected, ActivePhysicsStamp actual)
        {
            if (actual == null) return TakeoverStatus.Stale;
            if (actual.TopologyGeneration != expected.TopologyGeneration) return TakeoverStatus.TopologyChanged;
            if (actual.FrameGeneration != expected.FrameGeneration || actual.Frame != expected.Frame) return TakeoverStatus.FrameChanged;
            return actual.Tick == expected.Tick ? TakeoverStatus.Prepared : TakeoverStatus.Stale;
        }
        static bool SameEnvelope(ActivePhysicsSnapshot before, ActivePhysicsSnapshot desired)
        {
            if (before.Bodies.Count != desired.Bodies.Count) return false;
            for (int i = 0; i < before.Bodies.Count; i++)
            {
                var a = before.Bodies[i]; var b = desired.Bodies[i];
                if (a.Id != b.Id || a.Generation != b.Generation || a.Mass != b.Mass) return false;
            }
            return true;
        }
        static bool SameState(ActivePhysicsSnapshot expected, ActivePhysicsSnapshot actual)
        {
            if (actual == null || !expected.Stamp.Matches(actual.Stamp) || !SameEnvelope(expected, actual)) return false;
            for (int i = 0; i < expected.Bodies.Count; i++)
            {
                var a = expected.Bodies[i]; var b = actual.Bodies[i];
                if (a.Position.X != b.Position.X || a.Position.Y != b.Position.Y || a.Position.Z != b.Position.Z ||
                    a.Velocity.X != b.Velocity.X || a.Velocity.Y != b.Velocity.Y || a.Velocity.Z != b.Velocity.Z) return false;
            }
            return true;
        }
    }
}
