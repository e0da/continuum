using System;
using System.Collections.Generic;
using System.Threading;

namespace KspContinuum
{
    // Translational rigid grouping: every configured island keeps its captured
    // offsets while aggregate force and momentum advance its center of mass.
    public sealed class RigidClusterBackend : ISimulationBackend
    {
        readonly int[][] clusters;
        readonly bool allBodies;

        public RigidClusterBackend() { allBodies = true; clusters = new int[0][]; }

        public RigidClusterBackend(IEnumerable<IEnumerable<int>> bodyIdClusters)
        {
            if (bodyIdClusters == null) throw new ArgumentException("Clusters are required.", "bodyIdClusters");
            var copied = new List<int[]>();
            var seen = new HashSet<int>();
            foreach (IEnumerable<int> source in bodyIdClusters)
            {
                if (source == null) throw new ArgumentException("Clusters must be nonnull.", "bodyIdClusters");
                var ids = new List<int>();
                foreach (int id in source)
                {
                    if (id < 0 || !seen.Add(id)) throw new ArgumentException("Cluster body IDs must be unique and nonnegative.", "bodyIdClusters");
                    ids.Add(id);
                }
                if (ids.Count == 0) throw new ArgumentException("Clusters must not be empty.", "bodyIdClusters");
                copied.Add(ids.ToArray());
            }
            if (copied.Count == 0) throw new ArgumentException("At least one cluster is required.", "bodyIdClusters");
            clusters = copied.ToArray();
        }

        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
        {
            if (batch == null) throw new ArgumentException("Batch is required.", "batch");
            var indices = Index(batch);
            int[][] activeClusters = clusters;
            if (allBodies)
            {
                var ids = new int[batch.Count];
                for (int i = 0; i < ids.Length; i++) ids[i] = batch.GetId(i);
                activeClusters = new[] { ids };
            }
            var output = new SimulationBody[batch.Count];
            double dt = batch.StepSeconds;
            for (int clusterIndex = 0; clusterIndex < activeClusters.Length; clusterIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                int[] cluster = activeClusters[clusterIndex];
                double mass = 0;
                Vec center = new Vec(), momentum = new Vec(), force = new Vec();
                for (int i = 0; i < cluster.Length; i++)
                {
                    int index = indices[cluster[i]];
                    double bodyMass = batch.GetMass(index);
                    mass += bodyMass;
                    center += batch.GetPosition(index) * bodyMass;
                    momentum += batch.GetVelocity(index) * bodyMass;
                    force += batch.GetForce(index);
                }
                AssemblyModel.Positive(mass);
                center = center * (1 / mass);
                Vec velocity = momentum * (1 / mass);
                Vec acceleration = force * (1 / mass);
                Vec nextCenter = center + velocity * dt + acceleration * (0.5 * dt * dt);
                Vec nextVelocity = velocity + acceleration * dt;
                Finite(nextCenter); Finite(nextVelocity);
                for (int i = 0; i < cluster.Length; i++)
                {
                    int index = indices[cluster[i]];
                    Vec offset = batch.GetPosition(index) + center * -1;
                    output[index] = new SimulationBody(batch.GetId(index), batch.GetMass(index),
                        nextCenter + offset, nextVelocity, batch.GetForce(index));
                }
            }
            return new SimulationBatch(batch.Stamp, dt, output);
        }

        Dictionary<int, int> Index(SimulationBatch batch)
        {
            if (batch.Count == 0) throw new InvalidOperationException("A cluster batch cannot be empty.");
            var result = new Dictionary<int, int>(batch.Count);
            for (int i = 0; i < batch.Count; i++) result.Add(batch.GetId(i), i);
            int configured = 0;
            int[][] activeClusters = clusters;
            if (allBodies)
            {
                var ids = new int[batch.Count];
                for (int i = 0; i < ids.Length; i++) ids[i] = batch.GetId(i);
                activeClusters = new[] { ids };
            }
            for (int c = 0; c < activeClusters.Length; c++)
                for (int i = 0; i < activeClusters[c].Length; i++)
                {
                    configured++;
                    if (!result.ContainsKey(activeClusters[c][i])) throw new InvalidOperationException("A configured cluster body is absent from the batch.");
                }
            if (configured != batch.Count) throw new InvalidOperationException("Every batch body must belong to exactly one cluster.");
            return result;
        }

        static void Finite(Vec value)
        {
            AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z);
        }
    }
}
