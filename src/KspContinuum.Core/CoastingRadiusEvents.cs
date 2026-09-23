using System;

namespace KspContinuum
{
    public enum RadiusCrossingDirection { Inward, Outward }

    public sealed class RadiusCrossingSearch
    {
        public const int MaximumScanIntervals = 4096;
        public RadiusCrossingSearch(double startTimeSeconds, double endTimeSeconds, double radiusMeters,
            RadiusCrossingDirection direction, double scanIntervalSeconds, double timeToleranceSeconds = 1e-6)
        {
            AssemblyModel.Finite(startTimeSeconds); AssemblyModel.Finite(endTimeSeconds); AssemblyModel.Positive(radiusMeters);
            AssemblyModel.Positive(scanIntervalSeconds); AssemblyModel.Positive(timeToleranceSeconds);
            if (endTimeSeconds <= startTimeSeconds || !Enum.IsDefined(typeof(RadiusCrossingDirection), direction))
                throw new ArgumentException("Search end must follow start and direction must be known.");
            double intervals = Math.Ceiling((endTimeSeconds - startTimeSeconds) / scanIntervalSeconds);
            if (double.IsInfinity(intervals) || intervals > MaximumScanIntervals)
                throw new ArgumentException("Search exceeds the bounded scan interval limit.");
            StartTimeSeconds = startTimeSeconds; EndTimeSeconds = endTimeSeconds; RadiusMeters = radiusMeters;
            Direction = direction; ScanIntervalSeconds = scanIntervalSeconds; TimeToleranceSeconds = timeToleranceSeconds;
        }
        public double StartTimeSeconds { get; private set; }
        public double EndTimeSeconds { get; private set; }
        public double RadiusMeters { get; private set; }
        public RadiusCrossingDirection Direction { get; private set; }
        public double ScanIntervalSeconds { get; private set; }
        public double TimeToleranceSeconds { get; private set; }
    }

    public sealed class RadiusCrossingEvent
    {
        internal RadiusCrossingEvent(int bodyId, double time, CoastingBody body, int evaluations)
        { BodyId = bodyId; TimeSeconds = time; Body = body; Evaluations = evaluations; }
        public string Kind { get { return "radius-crossing"; } }
        public int BodyId { get; private set; }
        public double TimeSeconds { get; private set; }
        public CoastingBody Body { get; private set; }
        public int Evaluations { get; private set; }
    }

    public static class CoastingEventScheduler
    {
        public static RadiusCrossingEvent FindFirst(CoastingEngine engine, RadiusCrossingSearch search)
        {
            if (engine == null || search == null) throw new ArgumentException("Engine and search are required.");
            CoastingSnapshot previous = engine.SampleAt(search.StartTimeSeconds); int evaluations = 1;
            double previousTime = search.StartTimeSeconds;
            while (previousTime < search.EndTimeSeconds)
            {
                double nextTime = Math.Min(search.EndTimeSeconds, previousTime + search.ScanIntervalSeconds);
                if (nextTime <= previousTime) throw new ArgumentException("Scan interval does not advance representable time.");
                CoastingSnapshot next = engine.SampleAt(nextTime); evaluations++;
                RadiusCrossingEvent first = null;
                for (int i = 0; i < previous.Bodies.Count; i++)
                {
                    CoastingBody before = previous.Bodies[i], after = next.Bodies[i];
                    if (before.Id != after.Id) throw new InvalidOperationException("Coasting membership changed during event search.");
                    double a = Radius(before.Position) - search.RadiusMeters, b = Radius(after.Position) - search.RadiusMeters;
                    if (!Crossed(a, b, search.Direction)) continue;
                    RadiusCrossingEvent candidate = Refine(engine, before.Id, previousTime, nextTime, search, ref evaluations);
                    if (first == null || candidate.TimeSeconds < first.TimeSeconds ||
                        candidate.TimeSeconds == first.TimeSeconds && candidate.BodyId < first.BodyId) first = candidate;
                }
                if (first != null) return new RadiusCrossingEvent(first.BodyId, first.TimeSeconds, first.Body, evaluations);
                previous = next; previousTime = nextTime;
            }
            return null;
        }

        static RadiusCrossingEvent Refine(CoastingEngine engine, int bodyId, double low, double high,
            RadiusCrossingSearch search, ref int evaluations)
        {
            for (int iteration = 0; iteration < 64 && high - low > search.TimeToleranceSeconds; iteration++)
            {
                double middle = low + (high - low) * .5;
                if (middle <= low || middle >= high) break;
                CoastingBody sample = engine.SampleBodyAt(bodyId, middle); evaluations++;
                double sign = Radius(sample.Position) - search.RadiusMeters;
                bool crossedFromLow = search.Direction == RadiusCrossingDirection.Outward ? sign >= 0 : sign <= 0;
                if (crossedFromLow) high = middle; else low = middle;
            }
            CoastingBody body = engine.SampleBodyAt(bodyId, high); evaluations++;
            return new RadiusCrossingEvent(bodyId, high, body, evaluations);
        }

        static bool Crossed(double before, double after, RadiusCrossingDirection direction)
        { return direction == RadiusCrossingDirection.Outward ? before < 0 && after >= 0 : before > 0 && after <= 0; }
        static double Radius(Vec value) { return Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z); }
    }
}
