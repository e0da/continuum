using System;
using System.Linq;

namespace KspContinuum
{
    public sealed class PerformanceWorkloadIdentity
    {
        public string system { get; set; } = "";
        public string workload { get; set; } = "";
        public string fixtureSha256 { get; set; } = "";
        public string configurationSha256 { get; set; } = "";
        public int items { get; set; }
        public int steps { get; set; }
        public double? stepSeconds { get; set; }
    }

    public sealed class PerformanceAllocationSamples
    {
        public bool available { get; set; }
        public string kind { get; set; } = "unavailable";
        public string scope { get; set; } = "unavailable";
        public long[] bytes { get; set; } = Array.Empty<long>();
    }

    public sealed class PerformancePhaseSamples
    {
        public double[] milliseconds { get; set; } = Array.Empty<double>();
        public PerformanceAllocationSamples allocations { get; set; } = new PerformanceAllocationSamples();
    }

    public sealed class PerformanceObservation
    {
        public string schema { get; set; } = "continuum-performance-observation/v1";
        public PerformanceWorkloadIdentity workload { get; set; } = new PerformanceWorkloadIdentity();
        public string strategy { get; set; } = "";
        public string environmentSha256 { get; set; } = "";
        public string measurementProtocol { get; set; } = "";
        public string sampleProtocol { get; set; } = "";
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
        public string environmentSha256 { get; set; } = "";
        public string measurementProtocol { get; set; } = "";
        public string sampleProtocol { get; set; } = "";
        public int samples { get; set; }
        public double baselineMedianMilliseconds { get; set; }
        public double candidateMedianMilliseconds { get; set; }
        public double speedup { get; set; }
        public double? candidateToBaselineAllocationRatio { get; set; }
        public string allocationKind { get; set; } = "unavailable";
        public string allocationScope { get; set; } = "unavailable";
        public double maximumRegressionFraction { get; set; }
        public bool withinMaximumRegression { get; set; }
    }

    public static class PerformanceObservations
    {
        public static void Validate(PerformanceObservation value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.schema != "continuum-performance-observation/v1" || value.workload == null ||
                String.IsNullOrWhiteSpace(value.workload.system) || String.IsNullOrWhiteSpace(value.workload.workload) ||
                !Sha256(value.workload.fixtureSha256) || !Sha256(value.workload.configurationSha256) ||
                value.workload.items <= 0 || value.workload.steps <= 0 ||
                value.workload.stepSeconds.HasValue && (!(value.workload.stepSeconds.Value > 0) ||
                    Double.IsInfinity(value.workload.stepSeconds.Value)) ||
                String.IsNullOrWhiteSpace(value.strategy) || !Sha256(value.environmentSha256) ||
                String.IsNullOrWhiteSpace(value.measurementProtocol) || String.IsNullOrWhiteSpace(value.sampleProtocol))
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
            if (baseline.environmentSha256 != candidate.environmentSha256 ||
                baseline.measurementProtocol != candidate.measurementProtocol ||
                baseline.sampleProtocol != candidate.sampleProtocol ||
                baseline.total.milliseconds.Length != candidate.total.milliseconds.Length)
                throw new ArgumentException("Performance observations use incompatible environments or measurement protocols.");
            if (!(maximumRegressionFraction >= 0) || Double.IsInfinity(maximumRegressionFraction))
                throw new ArgumentException("Maximum regression must be finite and nonnegative.");
            double baselineMedian = Median(baseline.total.milliseconds);
            double candidateMedian = Median(candidate.total.milliseconds);
            if (baselineMedian <= 0 || candidateMedian <= 0)
                throw new ArgumentException("Comparison requires positive total medians.");
            double speedup = baselineMedian / candidateMedian;
            double candidateToBaselineLatencyRatio = candidateMedian / baselineMedian;
            double maximumRatio = 1 + maximumRegressionFraction;
            if (!Finite(speedup) || !Finite(candidateToBaselineLatencyRatio) || !Finite(maximumRatio))
                throw new ArgumentException("Performance comparison produced a nonfinite latency ratio.");
            double? allocationRatio = null;
            if (ComparableAllocations(baseline.total.allocations, candidate.total.allocations))
            {
                double baselineAllocation = Median(baseline.total.allocations.bytes.Select(value => (double)value).ToArray());
                double candidateAllocation = Median(candidate.total.allocations.bytes.Select(value => (double)value).ToArray());
                allocationRatio = baselineAllocation == 0 ? (candidateAllocation == 0 ? 1 : null)
                    : candidateAllocation / baselineAllocation;
                if (allocationRatio.HasValue && !Finite(allocationRatio.Value))
                    throw new ArgumentException("Performance comparison produced a nonfinite allocation ratio.");
            }
            return new PerformanceComparison {
                workload = candidate.workload,
                baselineStrategy = baseline.strategy,
                candidateStrategy = candidate.strategy,
                environmentSha256 = candidate.environmentSha256,
                measurementProtocol = candidate.measurementProtocol,
                sampleProtocol = candidate.sampleProtocol,
                samples = candidate.total.milliseconds.Length,
                baselineMedianMilliseconds = baselineMedian,
                candidateMedianMilliseconds = candidateMedian,
                speedup = speedup,
                candidateToBaselineAllocationRatio = allocationRatio,
                allocationKind = allocationRatio.HasValue ? candidate.total.allocations.kind : "unavailable",
                allocationScope = allocationRatio.HasValue ? candidate.total.allocations.scope : "unavailable",
                maximumRegressionFraction = maximumRegressionFraction,
                withinMaximumRegression = candidateToBaselineLatencyRatio <= maximumRatio
            };
        }

        static int ValidatePhase(PerformancePhaseSamples phase, string name, int expected)
        {
            if (phase == null || phase.milliseconds == null || phase.allocations == null ||
                phase.milliseconds.Length == 0 ||
                expected >= 0 && phase.milliseconds.Length != expected)
                throw new ArgumentException("Performance phase " + name + " has inconsistent samples.");
            PerformanceAllocationSamples allocations = phase.allocations;
            if (allocations.bytes == null || allocations.available &&
                    (allocations.bytes.Length != phase.milliseconds.Length || String.IsNullOrWhiteSpace(allocations.kind) ||
                        String.IsNullOrWhiteSpace(allocations.scope) || allocations.kind == "unavailable" || allocations.scope == "unavailable") ||
                !allocations.available && (allocations.bytes.Length != 0 || allocations.kind != "unavailable" || allocations.scope != "unavailable"))
                throw new ArgumentException("Performance phase " + name + " has invalid allocation availability.");
            for (int i = 0; i < phase.milliseconds.Length; i++)
                if (phase.milliseconds[i] < 0 || Double.IsNaN(phase.milliseconds[i]) || Double.IsInfinity(phase.milliseconds[i]) ||
                    allocations.available && allocations.bytes[i] < 0)
                    throw new ArgumentException("Performance phase " + name + " has an invalid sample.");
            return phase.milliseconds.Length;
        }

        static bool SameWorkload(PerformanceWorkloadIdentity left, PerformanceWorkloadIdentity right)
        {
            return left.system == right.system && left.workload == right.workload &&
                left.fixtureSha256 == right.fixtureSha256 && left.configurationSha256 == right.configurationSha256 &&
                left.items == right.items && left.steps == right.steps && left.stepSeconds == right.stepSeconds;
        }

        static bool ComparableAllocations(PerformanceAllocationSamples left, PerformanceAllocationSamples right)
        {
            return left.available && right.available && left.kind == right.kind && left.scope == right.scope;
        }

        static bool Sha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') &&
                    !(character >= 'A' && character <= 'F')) return false;
            return true;
        }

        static double Median(double[] values)
        {
            double[] sorted = (double[])values.Clone(); Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2
                : sorted[middle];
        }

        static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
