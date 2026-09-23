using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace KspContinuum
{
    public sealed class CoastingBody
    {
        public CoastingBody(int id, Vec position, Vec velocity)
        {
            if (id < 0) throw new ArgumentException("Body ID must be nonnegative.", "id");
            Validate(position); Validate(velocity);
            Id = id; Position = position; Velocity = velocity;
        }
        public int Id { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
        static void Validate(Vec value)
        { AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z); }
    }

    public sealed class CoastingSnapshot
    {
        readonly IReadOnlyList<CoastingBody> bodies;
        internal CoastingSnapshot(double timeSeconds, CoastingBody[] source)
        { TimeSeconds = timeSeconds; bodies = new ReadOnlyCollection<CoastingBody>((CoastingBody[])source.Clone()); }
        public double TimeSeconds { get; private set; }
        public IReadOnlyList<CoastingBody> Bodies { get { return bodies; } }
    }

    public sealed class CoastingAdvanceResult
    {
        internal CoastingAdvanceResult(CoastingSnapshot final, CoastingSnapshot[] publications)
        { Final = final; Publications = new ReadOnlyCollection<CoastingSnapshot>(publications); }
        public string StopEvent { get { return "target-time"; } }
        public CoastingSnapshot Final { get; private set; }
        public IReadOnlyList<CoastingSnapshot> Publications { get; private set; }
    }

    public sealed class CoastingEvaluationSettings
    {
        public CoastingEvaluationSettings(double anomalyTolerance = 1e-12, int maximumIterations = 32)
        {
            if (double.IsNaN(anomalyTolerance) || double.IsInfinity(anomalyTolerance) || anomalyTolerance <= 0 || maximumIterations < 1 || maximumIterations > 128)
                throw new ArgumentException("Finite positive tolerance and 1 through 128 iterations are required.");
            AnomalyTolerance = anomalyTolerance; MaximumIterations = maximumIterations;
        }
        public double AnomalyTolerance { get; private set; }
        public int MaximumIterations { get; private set; }
    }

    public sealed class CoastingEngine
    {
        public const int MaximumPublicationsPerAdvance = 4096;
        readonly CoastingBody[] origin;
        readonly double epoch, mu;
        readonly int workBatchSize;
        readonly CoastingEvaluationSettings settings;
        CoastingSnapshot current;

        public CoastingEngine(double epochSeconds, double gravitationalParameter, IEnumerable<CoastingBody> bodies,
            int workBatchSize, CoastingEvaluationSettings settings = null)
        {
            AssemblyModel.Finite(epochSeconds); AssemblyModel.Positive(gravitationalParameter);
            if (bodies == null || workBatchSize < 1) throw new ArgumentException("Bodies and a positive work batch size are required.");
            var copy = new List<CoastingBody>(); var ids = new HashSet<int>();
            foreach (CoastingBody body in bodies)
            {
                if (body == null || !ids.Add(body.Id)) throw new ArgumentException("Bodies must be nonnull with unique IDs.");
                copy.Add(new CoastingBody(body.Id, body.Position, body.Velocity));
            }
            if (copy.Count == 0 || copy.Count > SimulationBatch.MaxBodies) throw new ArgumentException("One through 4096 bodies are required.");
            epoch = epochSeconds; mu = gravitationalParameter; origin = copy.ToArray(); this.workBatchSize = workBatchSize;
            this.settings = settings ?? new CoastingEvaluationSettings();
            current = new CoastingSnapshot(epoch, origin);
        }

        public CoastingSnapshot Current { get { return current; } }

        public CoastingSnapshot SampleAt(double timeSeconds)
        {
            AssemblyModel.Finite(timeSeconds);
            return Evaluate(timeSeconds);
        }

        public CoastingAdvanceResult AdvanceTo(double targetTimeSeconds, double publicationIntervalSeconds = 0)
        {
            AssemblyModel.Finite(targetTimeSeconds); AssemblyModel.Finite(publicationIntervalSeconds);
            if (targetTimeSeconds <= current.TimeSeconds || publicationIntervalSeconds < 0)
                throw new ArgumentException("Target must advance engine time and publication interval cannot be negative.");
            var publications = new List<CoastingSnapshot>();
            if (publicationIntervalSeconds > 0)
            {
                double requested = Math.Ceiling((targetTimeSeconds - current.TimeSeconds) / publicationIntervalSeconds) - 1;
                if (double.IsInfinity(requested) || requested > MaximumPublicationsPerAdvance)
                    throw new ArgumentException("Publication request exceeds the bounded advance limit.");
                double sample = current.TimeSeconds + publicationIntervalSeconds;
                if (sample <= current.TimeSeconds)
                    throw new ArgumentException("Publication interval does not advance representable time.");
                while (sample < targetTimeSeconds)
                {
                    publications.Add(SampleAt(sample));
                    double next = sample + publicationIntervalSeconds;
                    if (next <= sample) throw new ArgumentException("Publication interval does not advance representable time.");
                    sample = next;
                }
            }
            CoastingSnapshot final = SampleAt(targetTimeSeconds);
            current = final;
            return new CoastingAdvanceResult(final, publications.ToArray());
        }

        CoastingSnapshot Evaluate(double time)
        {
            var result = new CoastingBody[origin.Length];
            int batches = 1 + (origin.Length - 1) / workBatchSize;
            Parallel.For(0, batches, batch =>
            {
                int first = batch * workBatchSize, last = Math.Min(origin.Length, first + workBatchSize);
                for (int i = first; i < last; i++) result[i] = UniversalKepler.Propagate(origin[i], time - epoch, mu, settings);
            });
            return new CoastingSnapshot(time, result);
        }
    }

    static class UniversalKepler
    {
        public static CoastingBody Propagate(CoastingBody body, double dt, double mu, CoastingEvaluationSettings settings)
        {
            if (dt == 0) return new CoastingBody(body.Id, body.Position, body.Velocity);
            Vec r0v = body.Position, v0v = body.Velocity;
            double r0 = Norm(r0v), v02 = Dot(v0v, v0v);
            if (r0 == 0) throw new ArgumentException("Central-gravity position is singular.");
            double rootMu = Math.Sqrt(mu), radial = Dot(r0v, v0v) / r0, alpha = 2 / r0 - v02 / mu;
            double x = Math.Abs(alpha) > 1e-12 ? rootMu * Math.Abs(alpha) * dt : rootMu * dt / r0;
            bool converged = false;
            for (int iteration = 0; iteration < settings.MaximumIterations; iteration++)
            {
                double z = alpha * x * x, c = C(z), s = S(z);
                double value = r0 * radial / rootMu * x * x * c + (1 - alpha * r0) * x * x * x * s + r0 * x - rootMu * dt;
                double derivative = r0 * radial / rootMu * x * (1 - z * s) + (1 - alpha * r0) * x * x * c + r0;
                if (Math.Abs(derivative) <= double.Epsilon || double.IsNaN(derivative) || double.IsInfinity(derivative)) break;
                double delta = value / derivative; x -= delta;
                if (Math.Abs(delta) <= settings.AnomalyTolerance * Math.Max(1, Math.Abs(x))) { converged = true; break; }
            }
            if (!converged) throw new InvalidOperationException("Universal anomaly solve did not converge.");
            double finalZ = alpha * x * x, finalC = C(finalZ), finalS = S(finalZ);
            double f = 1 - x * x / r0 * finalC, g = dt - x * x * x / rootMu * finalS;
            Vec position = r0v * f + v0v * g; double radius = Norm(position);
            double fdot = rootMu / (radius * r0) * (alpha * x * x * x * finalS - x);
            double gdot = 1 - x * x / radius * finalC;
            return new CoastingBody(body.Id, position, r0v * fdot + v0v * gdot);
        }

        static double C(double z)
        {
            if (z > 1e-8) { double root = Math.Sqrt(z); return (1 - Math.Cos(root)) / z; }
            if (z < -1e-8) { double root = Math.Sqrt(-z); return (Math.Cosh(root) - 1) / -z; }
            return 0.5 - z / 24 + z * z / 720 - z * z * z / 40320;
        }
        static double S(double z)
        {
            if (z > 1e-8) { double root = Math.Sqrt(z); return (root - Math.Sin(root)) / (root * root * root); }
            if (z < -1e-8) { double root = Math.Sqrt(-z); return (Math.Sinh(root) - root) / (root * root * root); }
            return 1.0 / 6 - z / 120 + z * z / 5040 - z * z * z / 362880;
        }
        static double Dot(Vec a, Vec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        static double Norm(Vec value) { return Math.Sqrt(Dot(value, value)); }
    }
}
