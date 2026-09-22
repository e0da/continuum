using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace KspContinuum
{
    public sealed class IslandLink
    {
        public IslandLink(int bodyA, int bodyB)
        {
            if (bodyA < 0 || bodyB < 0 || bodyA == bodyB)
                throw new ArgumentException("A link requires two different nonnegative body IDs.");
            BodyA = bodyA; BodyB = bodyB;
        }
        public int BodyA { get; private set; }
        public int BodyB { get; private set; }
    }

    public sealed class StructuralIslandPlan
    {
        readonly IReadOnlyList<IReadOnlyList<int>> bodyIndices;
        internal StructuralIslandPlan(List<IReadOnlyList<int>> islands)
        { bodyIndices = new ReadOnlyCollection<IReadOnlyList<int>>(islands); }
        public IReadOnlyList<IReadOnlyList<int>> BodyIndices { get { return bodyIndices; } }
    }

    public static class DeterministicIslandDecomposer
    {
        public static StructuralIslandPlan Decompose(SimulationBatch batch, IEnumerable<IslandLink> links)
        {
            if (batch == null || links == null) throw new ArgumentException("Batch and links are required.");
            int count = batch.Count;
            var indexById = new Dictionary<int, int>(count);
            var parent = new int[count];
            for (int i = 0; i < count; i++) { indexById.Add(batch.GetId(i), i); parent[i] = i; }
            foreach (var link in links)
            {
                if (link == null) throw new ArgumentException("Links must be nonnull.", "links");
                int a, b;
                if (!indexById.TryGetValue(link.BodyA, out a) || !indexById.TryGetValue(link.BodyB, out b))
                    throw new ArgumentException("Every link endpoint must belong to the batch.", "links");
                Union(parent, a, b);
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i); List<int> group;
                if (!groups.TryGetValue(root, out group)) { group = new List<int>(); groups.Add(root, group); }
                group.Add(i);
            }
            var result = new List<IReadOnlyList<int>>(groups.Count);
            foreach (var group in groups.Values)
            {
                group.Sort(delegate(int a, int b) { return batch.GetId(a).CompareTo(batch.GetId(b)); });
                result.Add(new ReadOnlyCollection<int>(group));
            }
            result.Sort(delegate(IReadOnlyList<int> a, IReadOnlyList<int> b)
            { return batch.GetId(a[0]).CompareTo(batch.GetId(b[0])); });
            return new StructuralIslandPlan(result);
        }
        static int Find(int[] parent, int value)
        {
            while (parent[value] != value) { parent[value] = parent[parent[value]]; value = parent[value]; }
            return value;
        }
        static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a), rootB = Find(parent, b);
            if (rootA == rootB) return;
            if (rootA < rootB) parent[rootB] = rootA; else parent[rootA] = rootB;
        }
    }

    public interface IStructuralIslandKernel
    {
        IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> bodyIndices,
            CancellationToken cancellation);
    }

    public sealed class RigidTranslationIslandKernel : IStructuralIslandKernel
    {
        public IReadOnlyList<SimulationBody> Compute(SimulationBatch batch, IReadOnlyList<int> bodyIndices,
            CancellationToken cancellation)
        {
            if (batch == null || bodyIndices == null || bodyIndices.Count == 0)
                throw new ArgumentException("Batch and a nonempty island are required.");
            double mass = 0; var weightedPosition = new Vec(); var momentum = new Vec(); var force = new Vec();
            for (int i = 0; i < bodyIndices.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested(); int index = bodyIndices[i]; double m = batch.GetMass(index);
                mass += m; weightedPosition += batch.GetPosition(index) * m;
                momentum += batch.GetVelocity(index) * m; force += batch.GetForce(index);
            }
            AssemblyModel.Positive(mass);
            Vec center = weightedPosition * (1 / mass), velocity = momentum * (1 / mass);
            double dt = batch.StepSeconds; Vec deltaVelocity = force * (dt / mass);
            Vec nextCenter = center + velocity * dt + deltaVelocity * (.5 * dt);
            Vec nextVelocity = velocity + deltaVelocity;
            var output = new SimulationBody[bodyIndices.Count];
            for (int i = 0; i < bodyIndices.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested(); int index = bodyIndices[i];
                output[i] = new SimulationBody(batch.GetId(index), batch.GetMass(index),
                    nextCenter + batch.GetPosition(index) + center * -1, nextVelocity, batch.GetForce(index));
            }
            return output;
        }
    }

    public sealed class StructuralIslandBackend : ISimulationBackend
    {
        readonly IslandLink[] links; readonly int maxConcurrency; readonly IStructuralIslandKernel kernel;
        public StructuralIslandBackend(IEnumerable<IslandLink> links, int maxConcurrency)
            : this(links, maxConcurrency, new RigidTranslationIslandKernel()) { }
        public StructuralIslandBackend(IEnumerable<IslandLink> links, int maxConcurrency, IStructuralIslandKernel kernel)
        {
            if (links == null || kernel == null) throw new ArgumentException("Links and kernel are required.");
            if (maxConcurrency < 1 || maxConcurrency > 64) throw new ArgumentOutOfRangeException("maxConcurrency");
            this.links = new List<IslandLink>(links).ToArray(); this.maxConcurrency = maxConcurrency; this.kernel = kernel;
        }
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            if (batch == null) throw new ArgumentException("Batch is required.", "batch");
            cancellation.ThrowIfCancellationRequested();
            StructuralIslandPlan plan = DeterministicIslandDecomposer.Decompose(batch, links);
            var staged = new SimulationBody[batch.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellation };
            Parallel.For(0, plan.BodyIndices.Count, options, delegate(int islandIndex)
            {
                IReadOnlyList<int> indices = plan.BodyIndices[islandIndex];
                IReadOnlyList<SimulationBody> bodies = kernel.Compute(batch, indices, cancellation);
                ValidateIsland(batch, indices, bodies);
                for (int i = 0; i < indices.Count; i++) staged[indices[i]] = bodies[i];
            });
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < staged.Length; i++)
                if (staged[i] == null) throw new InvalidOperationException("Island execution did not produce every body.");
            return new SimulationBatch(batch.Stamp, batch.StepSeconds, staged);
        }
        static void ValidateIsland(SimulationBatch input, IReadOnlyList<int> indices, IReadOnlyList<SimulationBody> output)
        {
            if (output == null || output.Count != indices.Count)
                throw new InvalidOperationException("Island kernel changed the island body count.");
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i]; SimulationBody body = output[i]; Vec force = input.GetForce(index);
                if (body == null || body.Id != input.GetId(index) || body.Mass != input.GetMass(index)
                    || body.Force.X != force.X || body.Force.Y != force.Y || body.Force.Z != force.Z)
                    throw new InvalidOperationException("Island kernel changed body identity, ordering, mass, or force.");
            }
        }
    }
}
