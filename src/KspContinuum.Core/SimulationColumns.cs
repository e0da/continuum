using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace KspContinuum
{
    internal sealed class SimulationColumns
    {
        readonly int[] ids;
        readonly double[] mass, px, py, pz, vx, vy, vz, fx, fy, fz;
        public int Count { get { return ids.Length; } }
        public SimulationColumns(int[] sourceIds, double[] masses, Vec[] positions, Vec[] velocities, Vec[] forces)
        {
            if (sourceIds == null || masses == null || positions == null || velocities == null || forces == null)
                throw new ArgumentException("Every capture array is required.");
            int count = sourceIds.Length;
            if (count < 1 || count > SimulationBatch.MaxBodies || masses.Length != count || positions.Length != count || velocities.Length != count || forces.Length != count)
                throw new ArgumentException("Capture arrays must have matching lengths from 1 through 4096.");
            ids = (int[])sourceIds.Clone(); mass = (double[])masses.Clone();
            px = new double[count]; py = new double[count]; pz = new double[count];
            vx = new double[count]; vy = new double[count]; vz = new double[count];
            fx = new double[count]; fy = new double[count]; fz = new double[count];
            var unique = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (ids[i] < 0 || !unique.Add(ids[i])) throw new ArgumentException("IDs must be nonnegative and unique.");
                AssemblyModel.Positive(mass[i]);
                var p = positions[i]; var v = velocities[i]; var f = forces[i];
                Validate(p.X, p.Y, p.Z); Validate(v.X, v.Y, v.Z); Validate(f.X, f.Y, f.Z);
                px[i] = p.X; py[i] = p.Y; pz[i] = p.Z;
                vx[i] = v.X; vy[i] = v.Y; vz[i] = v.Z;
                fx[i] = f.X; fy[i] = f.Y; fz[i] = f.Z;
            }
        }
        SimulationColumns(SimulationColumns source)
        {
            // These envelope arrays are privately owned and never mutated after capture.
            ids = source.ids; mass = source.mass; fx = source.fx; fy = source.fy; fz = source.fz;
            px = new double[Count]; py = new double[Count]; pz = new double[Count];
            vx = new double[Count]; vy = new double[Count]; vz = new double[Count];
        }
        static void Validate(double x, double y, double z)
        {
            AssemblyModel.Finite(x); AssemblyModel.Finite(y); AssemblyModel.Finite(z);
        }
        public int GetId(int index) { return ids[index]; }
        public double GetMass(int index) { return mass[index]; }
        public Vec GetPosition(int index) { return new Vec(px[index], py[index], pz[index]); }
        public Vec GetVelocity(int index) { return new Vec(vx[index], vy[index], vz[index]); }
        public Vec GetForce(int index) { return new Vec(fx[index], fy[index], fz[index]); }
        public IReadOnlyList<SimulationBody> MaterializeBodies()
        {
            var result = new SimulationBody[Count];
            for (int i = 0; i < Count; i++) result[i] = new SimulationBody(ids[i], mass[i], GetPosition(i), GetVelocity(i), GetForce(i));
            return new ReadOnlyCollection<SimulationBody>(result);
        }
        public SimulationColumns Integrate(double dt, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = new SimulationColumns(this);
            for (int i = 0; i < Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                double dx = fx[i] / mass[i] * dt, dy = fy[i] / mass[i] * dt, dz = fz[i] / mass[i] * dt;
                double x = px[i] + vx[i] * dt + dx * (.5 * dt);
                double y = py[i] + vy[i] * dt + dy * (.5 * dt);
                double z = pz[i] + vz[i] * dt + dz * (.5 * dt);
                double ux = vx[i] + dx, uy = vy[i] + dy, uz = vz[i] + dz;
                Validate(x, y, z); Validate(ux, uy, uz);
                result.px[i] = x; result.py[i] = y; result.pz[i] = z;
                result.vx[i] = ux; result.vy[i] = uy; result.vz[i] = uz;
            }
            return result;
        }
    }
}
