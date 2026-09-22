using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    [Flags]
    public enum ExecutionComponent
    {
        None = 0,
        Motion = 1,
        Mass = 2,
        Force = 4,
        Contact = 8,
        Structural = 16
    }

    public struct EntityKey : IEquatable<EntityKey>, IComparable<EntityKey>
    {
        public EntityKey(int slot, int generation)
        {
            if (slot < 0 || generation < 0) throw new ArgumentOutOfRangeException();
            Slot = slot; Generation = generation;
        }
        public int Slot { get; private set; }
        public int Generation { get; private set; }
        public int CompareTo(EntityKey other)
        {
            int slot = Slot.CompareTo(other.Slot);
            return slot == 0 ? Generation.CompareTo(other.Generation) : slot;
        }
        public bool Equals(EntityKey other) { return Slot == other.Slot && Generation == other.Generation; }
        public override bool Equals(object obj) { return obj is EntityKey && Equals((EntityKey)obj); }
        public override int GetHashCode() { return unchecked((Slot * 397) ^ Generation); }
        public override string ToString() { return Slot + ":" + Generation; }
    }

    public sealed class ExecutionEntity
    {
        public ExecutionEntity(EntityKey key, ExecutionComponent components, double mass,
            Vec position, Vec velocity, Vec force)
        {
            const ExecutionComponent allowed = ExecutionComponent.Motion | ExecutionComponent.Mass |
                ExecutionComponent.Force | ExecutionComponent.Contact | ExecutionComponent.Structural;
            if ((components & ~allowed) != 0) throw new ArgumentOutOfRangeException("components");
            if ((components & ExecutionComponent.Mass) != 0) AssemblyModel.Positive(mass);
            Validate(position); Validate(velocity); Validate(force);
            Key = key; Components = components; Mass = mass;
            Position = position; Velocity = velocity; Force = force;
        }
        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z);
        }
        public EntityKey Key { get; private set; }
        public ExecutionComponent Components { get; private set; }
        public double Mass { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
        public Vec Force { get; private set; }
    }

    public sealed class ExecutionSnapshot
    {
        readonly IReadOnlyList<ExecutionEntity> entities;
        public ExecutionSnapshot(WorkStamp stamp, long revision, IEnumerable<ExecutionEntity> source)
        {
            if (stamp == null || source == null || revision < 0) throw new ArgumentException("Stamp, revision, and entities are required.");
            var copy = new List<ExecutionEntity>(); var keys = new HashSet<EntityKey>();
            foreach (var entity in source)
            {
                if (entity == null || !keys.Add(entity.Key)) throw new ArgumentException("Entities must be nonnull with unique stable keys.");
                copy.Add(entity);
            }
            Stamp = stamp; Revision = revision; entities = new ReadOnlyCollection<ExecutionEntity>(copy);
        }
        public WorkStamp Stamp { get; private set; }
        public long Revision { get; private set; }
        public IReadOnlyList<ExecutionEntity> Entities { get { return entities; } }
    }

    public sealed class ExecutionQuery
    {
        public ExecutionQuery(string systemId, ExecutionComponent required, ExecutionComponent excluded, int laneWidth)
        {
            if (string.IsNullOrWhiteSpace(systemId) || required == ExecutionComponent.None || (required & excluded) != 0)
                throw new ArgumentException("A query needs a system ID and nonconflicting required components.");
            if (laneWidth != 1 && laneWidth != 4 && laneWidth != 8 && laneWidth != 16)
                throw new ArgumentOutOfRangeException("laneWidth");
            SystemId = systemId; Required = required; Excluded = excluded; LaneWidth = laneWidth;
        }
        public string SystemId { get; private set; }
        public ExecutionComponent Required { get; private set; }
        public ExecutionComponent Excluded { get; private set; }
        public int LaneWidth { get; private set; }
        public static ExecutionQuery Motion(string systemId, int laneWidth)
        {
            return new ExecutionQuery(systemId,
                ExecutionComponent.Motion | ExecutionComponent.Mass | ExecutionComponent.Force,
                ExecutionComponent.None, laneWidth);
        }
        public ExecutionQuery WithAll(ExecutionComponent components)
        { return new ExecutionQuery(SystemId, Required | components, Excluded & ~components, LaneWidth); }
        public ExecutionQuery Without(ExecutionComponent components)
        { return new ExecutionQuery(SystemId, Required & ~components, Excluded | components, LaneWidth); }
    }

    public sealed class MotionExecutionView
    {
        readonly EntityKey[] keys; readonly int[] sourceIndices;
        readonly double[] mass, px, py, pz, vx, vy, vz, fx, fy, fz;
        readonly IReadOnlyList<EntityKey> keyView;
        readonly IReadOnlyList<double> massView, pxView, pyView, pzView, vxView, vyView, vzView, fxView, fyView, fzView;
        internal MotionExecutionView(ExecutionSnapshot source, ExecutionQuery query, List<int> selected)
        {
            SourceStamp = source.Stamp; SourceRevision = source.Revision; SystemId = query.SystemId;
            Count = selected.Count; LaneWidth = query.LaneWidth;
            PaddedCount = Count == 0 ? 0 : checked(((Count + LaneWidth - 1) / LaneWidth) * LaneWidth);
            keys = new EntityKey[PaddedCount]; sourceIndices = new int[PaddedCount];
            mass = new double[PaddedCount]; px = new double[PaddedCount]; py = new double[PaddedCount]; pz = new double[PaddedCount];
            vx = new double[PaddedCount]; vy = new double[PaddedCount]; vz = new double[PaddedCount];
            fx = new double[PaddedCount]; fy = new double[PaddedCount]; fz = new double[PaddedCount];
            for (int lane = 0; lane < Count; lane++)
            {
                int sourceIndex = selected[lane]; ExecutionEntity entity = source.Entities[sourceIndex];
                keys[lane] = entity.Key; sourceIndices[lane] = sourceIndex; mass[lane] = entity.Mass;
                px[lane] = entity.Position.X; py[lane] = entity.Position.Y; pz[lane] = entity.Position.Z;
                vx[lane] = entity.Velocity.X; vy[lane] = entity.Velocity.Y; vz[lane] = entity.Velocity.Z;
                fx[lane] = entity.Force.X; fy[lane] = entity.Force.Y; fz[lane] = entity.Force.Z;
            }
            keyView = Array.AsReadOnly(keys); massView = Array.AsReadOnly(mass);
            pxView = Array.AsReadOnly(px); pyView = Array.AsReadOnly(py); pzView = Array.AsReadOnly(pz);
            vxView = Array.AsReadOnly(vx); vyView = Array.AsReadOnly(vy); vzView = Array.AsReadOnly(vz);
            fxView = Array.AsReadOnly(fx); fyView = Array.AsReadOnly(fy); fzView = Array.AsReadOnly(fz);
        }
        public WorkStamp SourceStamp { get; private set; }
        public long SourceRevision { get; private set; }
        public string SystemId { get; private set; }
        public int Count { get; private set; }
        public int PaddedCount { get; private set; }
        public int LaneWidth { get; private set; }
        public IReadOnlyList<EntityKey> Keys { get { return keyView; } }
        public IReadOnlyList<double> Masses { get { return massView; } }
        public IReadOnlyList<double> PositionX { get { return pxView; } }
        public IReadOnlyList<double> PositionY { get { return pyView; } }
        public IReadOnlyList<double> PositionZ { get { return pzView; } }
        public IReadOnlyList<double> VelocityX { get { return vxView; } }
        public IReadOnlyList<double> VelocityY { get { return vyView; } }
        public IReadOnlyList<double> VelocityZ { get { return vzView; } }
        public IReadOnlyList<double> ForceX { get { return fxView; } }
        public IReadOnlyList<double> ForceY { get { return fyView; } }
        public IReadOnlyList<double> ForceZ { get { return fzView; } }
        public EntityKey GetKey(int lane) { Check(lane); return keys[lane]; }
        public double GetMass(int lane) { Check(lane); return mass[lane]; }
        public Vec GetPosition(int lane) { Check(lane); return new Vec(px[lane], py[lane], pz[lane]); }
        public Vec GetVelocity(int lane) { Check(lane); return new Vec(vx[lane], vy[lane], vz[lane]); }
        public Vec GetForce(int lane) { Check(lane); return new Vec(fx[lane], fy[lane], fz[lane]); }
        internal int GetSourceIndex(int lane) { return sourceIndices[lane]; }
        void Check(int lane) { if (lane < 0 || lane >= Count) throw new ArgumentOutOfRangeException("lane"); }
    }

    public static class ExecutionViewCompiler
    {
        const ExecutionComponent MotionFields = ExecutionComponent.Motion | ExecutionComponent.Mass | ExecutionComponent.Force;
        public static MotionExecutionView CompileMotion(ExecutionSnapshot source, ExecutionQuery query)
        {
            if (source == null || query == null) throw new ArgumentException("Source and query are required.");
            if ((query.Required & MotionFields) != MotionFields)
                throw new ArgumentException("A motion view requires motion, mass, and force components.", "query");
            var selected = new List<int>();
            for (int i = 0; i < source.Entities.Count; i++)
            {
                ExecutionComponent components = source.Entities[i].Components;
                if ((components & query.Required) == query.Required && (components & query.Excluded) == 0) selected.Add(i);
            }
            return new MotionExecutionView(source, query, selected);
        }
    }

    public sealed class MotionExecutionResult
    {
        readonly Vec[] positions, velocities;
        public MotionExecutionResult(MotionExecutionView view, Vec[] resultPositions, Vec[] resultVelocities)
        {
            if (view == null || resultPositions == null || resultVelocities == null ||
                resultPositions.Length != view.Count || resultVelocities.Length != view.Count)
                throw new ArgumentException("A result must cover every selected lane exactly once.");
            View = view; positions = (Vec[])resultPositions.Clone(); velocities = (Vec[])resultVelocities.Clone();
            for (int i = 0; i < positions.Length; i++) { Validate(positions[i]); Validate(velocities[i]); }
        }
        static void Validate(Vec value) { AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z); }
        public MotionExecutionView View { get; private set; }
        internal Vec GetPosition(int lane) { return positions[lane]; }
        internal Vec GetVelocity(int lane) { return velocities[lane]; }
    }

    public enum ExecutionPublishStatus { Published, Stale, Invalid }
    public sealed class ExecutionPublication
    {
        internal ExecutionPublication(ExecutionPublishStatus status, ExecutionSnapshot snapshot)
        { Status = status; Snapshot = snapshot; }
        public ExecutionPublishStatus Status { get; private set; }
        public ExecutionSnapshot Snapshot { get; private set; }
    }

    public static class ExecutionPublisher
    {
        public static ExecutionPublication Publish(ExecutionSnapshot current, MotionExecutionResult result)
        {
            if (current == null || result == null) return new ExecutionPublication(ExecutionPublishStatus.Invalid, null);
            MotionExecutionView view = result.View;
            if (current.Revision != view.SourceRevision || !current.Stamp.Matches(view.SourceStamp))
                return new ExecutionPublication(ExecutionPublishStatus.Stale, null);
            var replacements = new Dictionary<int, ExecutionEntity>();
            for (int lane = 0; lane < view.Count; lane++)
            {
                int index = view.GetSourceIndex(lane);
                if (index < 0 || index >= current.Entities.Count || !current.Entities[index].Key.Equals(view.GetKey(lane)))
                    return new ExecutionPublication(ExecutionPublishStatus.Stale, null);
                ExecutionEntity before = current.Entities[index];
                replacements.Add(index, new ExecutionEntity(before.Key, before.Components, before.Mass,
                    result.GetPosition(lane), result.GetVelocity(lane), before.Force));
            }
            var next = new ExecutionEntity[current.Entities.Count];
            for (int i = 0; i < next.Length; i++) next[i] = replacements.ContainsKey(i) ? replacements[i] : current.Entities[i];
            return new ExecutionPublication(ExecutionPublishStatus.Published,
                new ExecutionSnapshot(current.Stamp, checked(current.Revision + 1), next));
        }
    }

    public interface IMotionExecutionBackend
    {
        MotionExecutionResult Compute(MotionExecutionView view, double stepSeconds);
    }

    public enum WorkRegularity { Regular, Conditional, Irregular }
    public enum DataResidency { Host, Device }
    public enum DependencyShape { Independent, DisjointIslands, SharedConstraints }
    public enum DeterminismRequirement { ExactOrder, Repeatable, BestEffort }

    public sealed class ExecutionWorkload
    {
        public ExecutionWorkload(int count, int laneWidth, WorkRegularity regularity, DataResidency residency,
            DependencyShape dependencies, double latencyBudgetMicroseconds, DeterminismRequirement determinism)
        {
            if (count < 0 || latencyBudgetMicroseconds <= 0 || double.IsNaN(latencyBudgetMicroseconds) ||
                double.IsInfinity(latencyBudgetMicroseconds)) throw new ArgumentOutOfRangeException();
            if (laneWidth != 1 && laneWidth != 4 && laneWidth != 8 && laneWidth != 16) throw new ArgumentOutOfRangeException("laneWidth");
            if (!Enum.IsDefined(typeof(WorkRegularity), regularity) || !Enum.IsDefined(typeof(DataResidency), residency) ||
                !Enum.IsDefined(typeof(DependencyShape), dependencies) || !Enum.IsDefined(typeof(DeterminismRequirement), determinism))
                throw new ArgumentOutOfRangeException();
            Count = count; LaneWidth = laneWidth; Regularity = regularity; Residency = residency;
            Dependencies = dependencies; LatencyBudgetMicroseconds = latencyBudgetMicroseconds; Determinism = determinism;
        }
        public int Count { get; private set; }
        public int LaneWidth { get; private set; }
        public WorkRegularity Regularity { get; private set; }
        public DataResidency Residency { get; private set; }
        public DependencyShape Dependencies { get; private set; }
        public double LatencyBudgetMicroseconds { get; private set; }
        public DeterminismRequirement Determinism { get; private set; }
    }

    public sealed class ExecutionBackendReport
    {
        readonly int[] laneWidths;
        public ExecutionBackendReport(string backendId, bool scalarReference, int maximumCount, int[] supportedLaneWidths,
            WorkRegularity maximumRegularity, DependencyShape maximumDependencies,
            DeterminismRequirement guarantee, DataResidency residency, double fixedMicroseconds,
            double perEntityNanoseconds, double transferMicroseconds)
        {
            if (string.IsNullOrWhiteSpace(backendId) || maximumCount < 0 || supportedLaneWidths == null || supportedLaneWidths.Length == 0)
                throw new ArgumentException("A backend needs an ID, capacity, and lane widths.");
            if (!Enum.IsDefined(typeof(WorkRegularity), maximumRegularity) ||
                !Enum.IsDefined(typeof(DependencyShape), maximumDependencies) ||
                !Enum.IsDefined(typeof(DeterminismRequirement), guarantee) || !Enum.IsDefined(typeof(DataResidency), residency))
                throw new ArgumentOutOfRangeException();
            if (fixedMicroseconds < 0 || perEntityNanoseconds < 0 || transferMicroseconds < 0 ||
                !Finite(fixedMicroseconds) || !Finite(perEntityNanoseconds) || !Finite(transferMicroseconds))
                throw new ArgumentOutOfRangeException();
            var seen = new HashSet<int>(); laneWidths = (int[])supportedLaneWidths.Clone();
            foreach (int width in laneWidths)
                if ((width != 1 && width != 4 && width != 8 && width != 16) || !seen.Add(width))
                    throw new ArgumentException("Lane widths must be unique supported widths.");
            BackendId = backendId; IsScalarReference = scalarReference; MaximumCount = maximumCount;
            MaximumRegularity = maximumRegularity; MaximumDependencies = maximumDependencies; Guarantee = guarantee;
            Residency = residency; FixedMicroseconds = fixedMicroseconds; PerEntityNanoseconds = perEntityNanoseconds;
            TransferMicroseconds = transferMicroseconds;
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal bool Supports(ExecutionWorkload workload)
        {
            if (workload.Count > MaximumCount || workload.Regularity > MaximumRegularity ||
                workload.Dependencies > MaximumDependencies || Guarantee > workload.Determinism) return false;
            for (int i = 0; i < laneWidths.Length; i++) if (laneWidths[i] == workload.LaneWidth) return true;
            return false;
        }
        internal double Estimate(ExecutionWorkload workload)
        {
            return FixedMicroseconds + PerEntityNanoseconds * workload.Count / 1000.0 +
                (Residency == workload.Residency ? 0 : TransferMicroseconds);
        }
        public string BackendId { get; private set; }
        public bool IsScalarReference { get; private set; }
        public int MaximumCount { get; private set; }
        public WorkRegularity MaximumRegularity { get; private set; }
        public DependencyShape MaximumDependencies { get; private set; }
        public DeterminismRequirement Guarantee { get; private set; }
        public DataResidency Residency { get; private set; }
        public double FixedMicroseconds { get; private set; }
        public double PerEntityNanoseconds { get; private set; }
        public double TransferMicroseconds { get; private set; }
    }

    public sealed class ExecutionRoute
    {
        internal ExecutionRoute(string backendId, double estimate, bool meetsBudget, bool fallback)
        { BackendId = backendId; EstimatedMicroseconds = estimate; MeetsLatencyBudget = meetsBudget; UsedScalarFallback = fallback; }
        public string BackendId { get; private set; }
        public double EstimatedMicroseconds { get; private set; }
        public bool MeetsLatencyBudget { get; private set; }
        public bool UsedScalarFallback { get; private set; }
    }

    public static class ExecutionBackendPolicy
    {
        public static ExecutionRoute Select(ExecutionWorkload workload, IEnumerable<ExecutionBackendReport> reports)
        {
            if (workload == null || reports == null) throw new ArgumentException("Workload and backend reports are required.");
            ExecutionBackendReport scalar = null, selected = null; double selectedCost = double.PositiveInfinity;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExecutionBackendReport report in reports)
            {
                if (report == null || !ids.Add(report.BackendId)) throw new ArgumentException("Backend reports must be nonnull with unique IDs.");
                if (report.IsScalarReference)
                {
                    if (scalar != null) throw new ArgumentException("Exactly one scalar reference is allowed.");
                    scalar = report;
                }
                if (!report.Supports(workload)) continue;
                double cost = report.Estimate(workload);
                if (selected == null || cost < selectedCost || (cost == selectedCost && string.CompareOrdinal(report.BackendId, selected.BackendId) < 0))
                { selected = report; selectedCost = cost; }
            }
            if (scalar == null || !scalar.Supports(workload)) throw new InvalidOperationException("An eligible scalar reference fallback is required.");
            if (selected == null) { selected = scalar; selectedCost = scalar.Estimate(workload); }
            bool fallback = selected.IsScalarReference;
            return new ExecutionRoute(selected.BackendId, selectedCost,
                selectedCost <= workload.LatencyBudgetMicroseconds, fallback);
        }
    }
}
