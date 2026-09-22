namespace KspContinuum
{
    public sealed class NativeBoundaryMonoTiming
    {
        public double medianNanoseconds, p95Nanoseconds, nanosecondsPerBodyAtMedian;
    }

    public sealed class NativeBoundaryMonoRow
    {
        public int bodies, steps;
        public NativeBoundaryMonoTiming managed, native;
        public double managedToNativeRatio;
    }

    public sealed class NativeBoundaryMonoReport
    {
        public string schema, runtime, processArchitecture, operatingSystem, scope;
        public int samplesPerCase, warmupsPerCase;
        public double stepSeconds;
        public NativeBoundaryMonoTiming noop;
        public NativeBoundaryMonoRow[] rows;
    }
}
