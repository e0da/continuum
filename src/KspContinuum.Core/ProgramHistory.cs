using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KspContinuum
{
    public sealed class ProgramBody
    {
        public ProgramBody(int id, long generation, double mass, double radius, Vec position, Vec velocity)
        {
            if (id < 0 || generation < 0) throw new ArgumentException("Invalid body identity.");
            AssemblyModel.Positive(mass); AssemblyModel.Positive(radius);
            Validate(position); Validate(velocity);
            Id = id; Generation = generation; Mass = mass; Radius = radius;
            Position = position; Velocity = velocity;
        }
        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z);
        }
        public int Id { get; private set; }
        public long Generation { get; private set; }
        public double Mass { get; private set; }
        public double Radius { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
    }

    public sealed class ProgramSnapshot
    {
        public const int MaximumBodies = 100000;
        public ProgramSnapshot(string frame, long tick, double epoch, IEnumerable<ProgramBody> bodies)
            : this(frame, "continuum-toy/v1", 0, 0, tick, epoch, bodies) { }
        public ProgramSnapshot(string frame, string modelFingerprint, long topologyGeneration,
            long frameGeneration, long tick, double epoch, IEnumerable<ProgramBody> bodies)
        {
            if (string.IsNullOrEmpty(frame) || frame.Length > 256 || string.IsNullOrEmpty(modelFingerprint) ||
                modelFingerprint.Length > 256 || topologyGeneration < 0 || frameGeneration < 0 || tick < 0 || bodies == null)
                throw new ArgumentException("Frame, tick, and bodies are required.");
            AssemblyModel.Finite(epoch);
            var copy = new List<ProgramBody>(); var ids = new HashSet<int>();
            foreach (var body in bodies)
            {
                if (copy.Count == MaximumBodies || body == null || !ids.Add(body.Id))
                    throw new ArgumentException("Snapshot membership is invalid.");
                copy.Add(body);
            }
            if (copy.Count == 0) throw new ArgumentException("Snapshot is empty.");
            copy.Sort((a, b) => a.Id.CompareTo(b.Id));
            Frame = frame; ModelFingerprint = modelFingerprint; TopologyGeneration = topologyGeneration;
            FrameGeneration = frameGeneration; Tick = tick; Epoch = epoch;
            Bodies = new ReadOnlyCollection<ProgramBody>(copy);
            Hash = ComputeHash();
        }
        public string Frame { get; private set; }
        public string ModelFingerprint { get; private set; }
        public long TopologyGeneration { get; private set; }
        public long FrameGeneration { get; private set; }
        public long Tick { get; private set; }
        public double Epoch { get; private set; }
        public IReadOnlyList<ProgramBody> Bodies { get; private set; }
        public string Hash { get; private set; }

        string ComputeHash()
        {
            using (var stream = new MemoryStream())
            {
                WriteString(stream, "ksp-continuum-program-snapshot/v1"); WriteString(stream, Frame);
                WriteString(stream, ModelFingerprint); WriteInt64(stream, TopologyGeneration);
                WriteInt64(stream, FrameGeneration); WriteInt64(stream, Tick); WriteDouble(stream, Epoch);
                WriteInt32(stream, Bodies.Count);
                foreach (var body in Bodies)
                {
                    WriteInt32(stream, body.Id); WriteInt64(stream, body.Generation);
                    WriteDouble(stream, body.Mass); WriteDouble(stream, body.Radius);
                    WriteVec(stream, body.Position); WriteVec(stream, body.Velocity);
                }
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        static void WriteVec(Stream stream, Vec value)
        { WriteDouble(stream, value.X); WriteDouble(stream, value.Y); WriteDouble(stream, value.Z); }
        static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); WriteInt32(stream, bytes.Length); stream.Write(bytes, 0, bytes.Length);
        }
        static void WriteInt32(Stream stream, int value)
        { var bytes = new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) }; stream.Write(bytes, 0, bytes.Length); }
        static void WriteInt64(Stream stream, long value)
        { var bytes = new byte[8]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(value >> (i * 8)); stream.Write(bytes, 0, bytes.Length); }
        static void WriteDouble(Stream stream, double value)
        { WriteInt64(stream, BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value)); }
    }

    public sealed class ProgramEvent
    {
        internal ProgramEvent(string branchId, long sequence, string kind, string authority, string parentHash,
            string resultHash, double logicalTime, string payloadHash, string previousEventHash, string eventHash)
        {
            BranchId = branchId; Sequence = sequence; Kind = kind; Authority = authority;
            ParentHash = parentHash; ResultHash = resultHash; LogicalTime = logicalTime; PayloadHash = payloadHash;
            PreviousEventHash = previousEventHash; EventHash = eventHash;
        }
        public string BranchId { get; private set; }
        public long Sequence { get; private set; }
        public string Kind { get; private set; }
        public string Authority { get; private set; }
        public string ParentHash { get; private set; }
        public string ResultHash { get; private set; }
        public double LogicalTime { get; private set; }
        public string PayloadHash { get; private set; }
        public string PreviousEventHash { get; private set; }
        public string EventHash { get; private set; }
    }

    public sealed class ProgramAuthority
    {
        internal ProgramAuthority(string leaseId, string branchId, string expectedHead, long generation,
            string subsystem, double startTime, double endTime, int[] bodyIds)
        {
            LeaseId = leaseId; BranchId = branchId; ExpectedHead = expectedHead; Generation = generation;
            Subsystem = subsystem; StartTime = startTime; EndTime = endTime; BodyIds = new ReadOnlyCollection<int>(bodyIds);
        }
        public string LeaseId { get; private set; }
        public string BranchId { get; private set; }
        public string ExpectedHead { get; private set; }
        public long Generation { get; private set; }
        public string Subsystem { get; private set; }
        public double StartTime { get; private set; }
        public double EndTime { get; private set; }
        public IReadOnlyList<int> BodyIds { get; private set; }
    }

    public sealed class ProgramWork
    {
        internal ProgramWork(ProgramSnapshot snapshot, ProgramAuthority authority)
        { Snapshot = snapshot; Authority = authority; }
        public ProgramSnapshot Snapshot { get; private set; }
        public ProgramAuthority Authority { get; private set; }
    }

    public enum ProgramStatus { Committed, Acquired, Rejected, Missing, Busy, Stale, Cancelled, Faulted, InvalidResult }

    public sealed class ProgramHistory
    {
        sealed class Branch
        {
            internal Branch(string id, string parent, ProgramSnapshot head)
            { Id = id; Parent = parent; Head = head; }
            internal readonly string Id, Parent;
            internal ProgramSnapshot Head;
            internal long Generation, Sequence;
            internal string EventHead = "";
            internal bool Busy;
            internal ProgramAuthority ActiveAuthority;
        }
        readonly object sync = new object();
        readonly Dictionary<string, Branch> branches = new Dictionary<string, Branch>(StringComparer.Ordinal);
        readonly Dictionary<string, ProgramSnapshot> snapshots = new Dictionary<string, ProgramSnapshot>(StringComparer.Ordinal);
        readonly List<ProgramEvent> events = new List<ProgramEvent>();

        public ProgramStatus CreateRoot(string branchId, ProgramSnapshot snapshot)
        {
            if (!ValidName(branchId) || snapshot == null) return ProgramStatus.Rejected;
            lock (sync)
            {
                if (branches.ContainsKey(branchId)) return ProgramStatus.Rejected;
                var branch = new Branch(branchId, null, snapshot); branches.Add(branchId, branch); snapshots[snapshot.Hash] = snapshot;
                Append(branch, "root", "program", snapshot.Hash, snapshot.Hash, snapshot.Epoch, HashText("root"));
                return ProgramStatus.Committed;
            }
        }
        public ProgramStatus Fork(string branchId, string parentId, string expectedHead)
        {
            if (!ValidName(branchId) || !ValidName(parentId) || string.IsNullOrEmpty(expectedHead)) return ProgramStatus.Rejected;
            lock (sync)
            {
                Branch parent;
                if (!branches.TryGetValue(parentId, out parent)) return ProgramStatus.Missing;
                if (branches.ContainsKey(branchId)) return ProgramStatus.Rejected;
                if (parent.Head.Hash != expectedHead) return ProgramStatus.Stale;
                var child = new Branch(branchId, parentId, parent.Head); child.EventHead = parent.EventHead; branches.Add(branchId, child);
                Append(child, "fork", "program", parent.Head.Hash, child.Head.Hash, child.Head.Epoch, HashText(parentId));
                return ProgramStatus.Committed;
            }
        }
        public ProgramSnapshot Capture(string branchId)
        { lock (sync) { Branch branch; return branches.TryGetValue(branchId, out branch) ? branch.Head : null; } }

        public ProgramStatus Acquire(string leaseId, string branchId, string expectedHead, double startTime,
            double endTime, IEnumerable<int> bodyIds, out ProgramAuthority authority)
        { return Acquire(leaseId, branchId, expectedHead, "motion", startTime, endTime, bodyIds, out authority); }
        public ProgramStatus Acquire(string leaseId, string branchId, string expectedHead, string subsystem,
            double startTime, double endTime, IEnumerable<int> bodyIds, out ProgramAuthority authority)
        {
            authority = null;
            if (!ValidName(leaseId) || !ValidName(branchId) || subsystem != "motion" || string.IsNullOrEmpty(expectedHead) || bodyIds == null)
                return ProgramStatus.Rejected;
            try { AssemblyModel.Finite(startTime); AssemblyModel.Finite(endTime); }
            catch (ArgumentException) { return ProgramStatus.Rejected; }
            if (endTime < startTime) return ProgramStatus.Rejected;
            var ids = new List<int>(); var unique = new HashSet<int>();
            foreach (var id in bodyIds) if (id < 0 || !unique.Add(id)) return ProgramStatus.Rejected; else ids.Add(id);
            ids.Sort(); if (ids.Count == 0) return ProgramStatus.Rejected;
            lock (sync)
            {
                Branch branch;
                if (!branches.TryGetValue(branchId, out branch)) return ProgramStatus.Missing;
                if (branch.Busy) return ProgramStatus.Busy;
                if (branch.Head.Hash != expectedHead) return ProgramStatus.Stale;
                if (startTime != branch.Head.Epoch) return ProgramStatus.Rejected;
                var members = new HashSet<int>(); foreach (var body in branch.Head.Bodies) members.Add(body.Id);
                foreach (var id in ids) if (!members.Contains(id)) return ProgramStatus.Rejected;
                branch.Busy = true;
                authority = new ProgramAuthority(leaseId, branchId, expectedHead, branch.Generation,
                    subsystem, startTime, endTime, ids.ToArray());
                branch.ActiveAuthority = authority;
                return ProgramStatus.Acquired;
            }
        }
        public ProgramStatus Execute(ProgramAuthority authority, string kind, string payload,
            Func<ProgramWork, CancellationToken, ProgramSnapshot> solver, CancellationToken cancellation)
        {
            if (authority == null) return ProgramStatus.Rejected;
            if (!ValidName(kind) || payload == null || solver == null)
            {
                Abort(authority); return ProgramStatus.Rejected;
            }
            Branch admitted; ProgramSnapshot input;
            lock (sync)
            {
                if (!branches.TryGetValue(authority.BranchId, out admitted)) return ProgramStatus.Missing;
                if (!admitted.Busy || !object.ReferenceEquals(admitted.ActiveAuthority, authority) ||
                    admitted.Generation != authority.Generation || admitted.Head.Hash != authority.ExpectedHead)
                    return ProgramStatus.Stale;
                if (cancellation.IsCancellationRequested) { Release(admitted, authority); return ProgramStatus.Cancelled; }
                input = admitted.Head;
            }
            ProgramSnapshot output = null; ProgramStatus outcome = ProgramStatus.Committed;
            try { output = solver(new ProgramWork(input, authority), cancellation); }
            catch (OperationCanceledException) { outcome = cancellation.IsCancellationRequested ? ProgramStatus.Cancelled : ProgramStatus.Faulted; }
            catch (Exception) { outcome = ProgramStatus.Faulted; }
            lock (sync)
            {
                Branch current;
                if (!branches.TryGetValue(authority.BranchId, out current)) return ProgramStatus.Missing;
                try
                {
                    if (cancellation.IsCancellationRequested) return ProgramStatus.Cancelled;
                    if (!object.ReferenceEquals(current.ActiveAuthority, authority) || current.Generation != authority.Generation ||
                        current.Head.Hash != authority.ExpectedHead) return ProgramStatus.Stale;
                    if (outcome != ProgramStatus.Committed) return outcome;
                    if (!ValidResult(input, output, authority)) return ProgramStatus.InvalidResult;
                    snapshots[output.Hash] = output; current.Head = output; current.Generation++;
                    Append(current, kind, authority.LeaseId, input.Hash, output.Hash, output.Epoch, HashText(payload));
                    return ProgramStatus.Committed;
                }
                finally { Release(current, authority); }
            }
        }
        public ProgramStatus Abort(ProgramAuthority authority)
        {
            if (authority == null) return ProgramStatus.Rejected;
            lock (sync)
            {
                Branch branch;
                if (!branches.TryGetValue(authority.BranchId, out branch)) return ProgramStatus.Missing;
                if (!object.ReferenceEquals(branch.ActiveAuthority, authority)) return ProgramStatus.Stale;
                Release(branch, authority); return ProgramStatus.Cancelled;
            }
        }
        public ProgramStatus Observe(string branchId, ProgramSnapshot snapshot, string source)
        {
            if (snapshot == null || !ValidName(source)) return ProgramStatus.Rejected;
            lock (sync)
            {
                Branch branch;
                if (!branches.TryGetValue(branchId, out branch)) return ProgramStatus.Missing;
                var parent = branch.Head.Hash; snapshots[snapshot.Hash] = snapshot; branch.Head = snapshot; branch.Generation++;
                branch.Busy = false; branch.ActiveAuthority = null;
                Append(branch, "observation", source, parent, snapshot.Hash, snapshot.Epoch, HashText(source));
                return ProgramStatus.Committed;
            }
        }
        public IReadOnlyList<ProgramEvent> Events()
        { lock (sync) { return new ReadOnlyCollection<ProgramEvent>(new List<ProgramEvent>(events)); } }
        public ProgramSnapshot GetSnapshot(string hash)
        { lock (sync) { ProgramSnapshot value; return hash != null && snapshots.TryGetValue(hash, out value) ? value : null; } }
        public bool Verify(string branchId)
        {
            lock (sync)
            {
                Branch branch; if (!branches.TryGetValue(branchId, out branch)) return false;
                string previous = branch.Parent == null ? "" : null;
                foreach (var item in events)
                {
                    if (item.BranchId != branchId) continue;
                    if (previous == null) previous = item.PreviousEventHash;
                    if (item.PreviousEventHash != previous || item.EventHash != EventHash(item.BranchId, item.Sequence,
                        item.Kind, item.Authority, item.ParentHash, item.ResultHash, item.LogicalTime, item.PayloadHash, item.PreviousEventHash)) return false;
                    previous = item.EventHash;
                }
                return previous == branch.EventHead && snapshots.ContainsKey(branch.Head.Hash);
            }
        }

        static bool ValidResult(ProgramSnapshot input, ProgramSnapshot output, ProgramAuthority authority)
        {
            if (output == null || output.Frame != input.Frame || output.ModelFingerprint != input.ModelFingerprint ||
                output.TopologyGeneration != input.TopologyGeneration || output.FrameGeneration != input.FrameGeneration || output.Tick <= input.Tick ||
                output.Epoch < authority.StartTime || output.Epoch > authority.EndTime || output.Bodies.Count != input.Bodies.Count)
                return false;
            for (int i = 0; i < input.Bodies.Count; i++)
            {
                var before = input.Bodies[i]; var after = output.Bodies[i];
                if (before.Id != after.Id || before.Generation != after.Generation || before.Mass != after.Mass || before.Radius != after.Radius)
                    return false;
                bool controlled = false; foreach (var id in authority.BodyIds) if (id == before.Id) controlled = true;
                if (!controlled && (before.Position.X != after.Position.X || before.Position.Y != after.Position.Y || before.Position.Z != after.Position.Z ||
                    before.Velocity.X != after.Velocity.X || before.Velocity.Y != after.Velocity.Y || before.Velocity.Z != after.Velocity.Z)) return false;
            }
            return true;
        }
        void Append(Branch branch, string kind, string authority, string parent, string result, double time, string payloadHash)
        {
            long sequence = branch.Sequence++;
            string hash = EventHash(branch.Id, sequence, kind, authority, parent, result, time, payloadHash, branch.EventHead);
            events.Add(new ProgramEvent(branch.Id, sequence, kind, authority, parent, result, time, payloadHash, branch.EventHead, hash));
            branch.EventHead = hash;
        }
        static string EventHash(string branch, long sequence, string kind, string authority, string parent,
            string result, double time, string payload, string previous)
        { return HashText(string.Join("\n", new[] { "ksp-continuum-program-event/v1", branch, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), kind, authority, parent, result, (time == 0 ? 0 : time).ToString("R", System.Globalization.CultureInfo.InvariantCulture), payload, previous })); }
        static void Release(Branch branch, ProgramAuthority authority)
        {
            if (!object.ReferenceEquals(branch.ActiveAuthority, authority)) return;
            branch.ActiveAuthority = null; branch.Busy = false;
        }
        static string HashText(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        static bool ValidName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }
    }
}
