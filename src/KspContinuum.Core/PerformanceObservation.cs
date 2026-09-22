using System;
using System.Linq;

namespace KspContinuum
{
    public sealed class PerformanceWorkloadIdentity
    {
        public string system { get; set; } = "";
        public string workload { get; set; } = "";
        public string fixtureSha256 { get; set; } = "";
        public int items { get; set; }
        public int steps { get; set; }
    }

    public sealed class PerformancePhaseSamples
    {
        public double[] milliseconds { get; set; } = Array.Empty<double>();
        public long[] allocatedBytes { get; set; } = Array.Empty<long>();
    }

    public sealed class PerformanceObservation
    {
        public string schema { get; set; } = "continuum-performance-observation/v1";
        public PerformanceWorkloadIdentity workload { get; set; } = new PerformanceWorkloadIdentity();
        public string strategy { get; set; } = "";
        public PerformancePhaseSamples capture { get; set; } = new PerformancePhaseSamples();
        public PerformancePhaseSamples pack { get; set; } = new PerformancePhaseSamples();
        public PerformancePhaseSamples compute { get; set; } = new PerformancePhaseSamples();
        public PerformancePhaseSamples synchronize { get; set; } = new PerformancePhaseSamples();
        public PerformancePhaseSamples publish { get; set; } = new PerformancePhaseSamples();
        public PerformancePhaseSamples total { get; set; } = new PerformancePhaseSamples();
    }

    public sealed class PerformanceComparison
    {
        public string schema { get; set; } = "continuum-performance-comparison/v1";
        public PerformanceWorkloadIdentity workload { get; set; } = new PerformanceWorkloadIdentity();
        public string baselineStrategy { get; set; } = "";
        public string candidateStrategy { get; set; } = "";
        public double baselineMedianMilliseconds { get; set; }
        public double candidateMedianMilliseconds { get; set; }
        public double speedup { get; set; }
        public double? candidateToBaselineAllocationRatio { get; set; }
        public bool withinMaximumRegression { get; set; }
    }

    public static class PerformanceObservations
    {
        public static void Validate(PerformanceObservation value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.schema != "continuum-performance-observation/v1" || value.workload == null ||
                String.IsNullOrWhiteSpace(value.workload.system) || String.IsNullOrWhiteSpace(value.workload.workload) ||
                value.workload.fixtureSha256.Length != 64 || value.workload.items <= 0 || value.workload.steps <= 0 ||
                String.IsNullOrWhiteSpace(value.strategy))
                throw new ArgumentException("Performance observation identity is incomplete.");
            int samples = ValidatePhase(value.capture, "capture", -1);
            ValidatePhase(value.pack, "pack", samples);
            ValidatePhase(value.compute, "compute", samples);
            ValidatePhase(value.synchronize, "synchronize", samples);
            ValidatePhase(value.publish, "publish", samples);
            ValidatePhase(value.total, "total", samples);
        }

        public static PerformanceComparison Compare(PerformanceObservation baseline,
            PerformanceObservation candidate, double maximumRegressionFraction)
        {
            Validate(baseline); Validate(candidate);
            if (!SameWorkload(baseline.workload, candidate.workload))
                throw new ArgumentException("Performance observations describe different workloads.");
            if (!(maximumRegressionFraction >= 0) || Double.IsInfinity(maximumRegressionFraction))
                throw new ArgumentException("Maximum regression must be finite and nonnegative.");
            double baselineMedian = Median(baseline.total.milliseconds);
            double candidateMedian = Median(candidate.total.milliseconds);
            if (baselineMedian <= 0 || candidateMedian <= 0)
                throw new ArgumentException("Comparison requires positive total medians.");
            double baselineAllocation = Median(baseline.total.allocatedBytes.Select(value => (double)value).ToArray());
            double candidateAllocation = Median(candidate.total.allocatedBytes.Select(value => (double)value).ToArray());
            return new PerformanceComparison {
                workload = candidate.workload,
                baselineStrategy = baseline.strategy,
                candidateStrategy = candidate.strategy,
                baselineMedianMilliseconds = baselineMedian,
                candidateMedianMilliseconds = candidateMedian,
                speedup = baselineMedian / candidateMedian,
                candidateToBaselineAllocationRatio = baselineAllocation == 0
                    ? (candidateAllocation == 0 ? 1 : null)
                    : candidateAllocation / baselineAllocation,
                withinMaximumRegression = candidateMedian <= baselineMedian * (1 + maximumRegressionFraction)
            };
        }

        static int ValidatePhase(PerformancePhaseSamples phase, string name, int expected)
        {
            if (phase == null || phase.milliseconds == null || phase.allocatedBytes == null ||
                phase.milliseconds.Length == 0 || phase.milliseconds.Length != phase.allocatedBytes.Length ||
                expected >= 0 && phase.milliseconds.Length != expected)
                throw new ArgumentException("Performance phase " + name + " has inconsistent samples.");
            for (int i = 0; i < phase.milliseconds.Length; i++)
                if (phase.milliseconds[i] < 0 || Double.IsNaN(phase.milliseconds[i]) || Double.IsInfinity(phase.milliseconds[i]) ||
                    phase.allocatedBytes[i] < 0)
                    throw new ArgumentException("Performance phase " + name + " has an invalid sample.");
            return phase.milliseconds.Length;
        }

        static bool SameWorkload(PerformanceWorkloadIdentity left, PerformanceWorkloadIdentity right)
        {
            return left.system == right.system && left.workload == right.workload &&
                left.fixtureSha256 == right.fixtureSha256 && left.items == right.items && left.steps == right.steps;
        }

        static double Median(double[] values)
        {
            double[] sorted = (double[])values.Clone(); Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        }
    }
}
