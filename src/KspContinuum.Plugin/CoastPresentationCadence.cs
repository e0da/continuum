using System;

namespace KspContinuum
{
    public sealed class CoastPresentationCadence
    {
        readonly double intervalSeconds;
        readonly bool direct;
        bool initialized;
        double startTime, endTime, lastTime;
        CoastingBody start, end;

        public CoastPresentationCadence(double intervalSeconds)
        {
            if (double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds) || intervalSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            this.intervalSeconds = intervalSeconds;
        }

        CoastPresentationCadence() { direct = true; }

        public static CoastPresentationCadence Direct() { return new CoastPresentationCadence(); }

        public double IntervalSeconds { get { return intervalSeconds; } }
        public bool IsDirect { get { return direct; } }
        public long EngineSampleCount { get; private set; }

        public CoastingBody Evaluate(double universalTime, Func<double, CoastingBody> sample)
        {
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime))
                throw new ArgumentOutOfRangeException(nameof(universalTime));
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (initialized && universalTime < lastTime)
                throw new InvalidOperationException("Presentation universal time moved backwards.");
            if (direct)
            {
                initialized = true; lastTime = universalTime; EngineSampleCount++;
                return sample(universalTime);
            }
            if (!initialized)
            {
                startTime = universalTime; endTime = universalTime + intervalSeconds;
                if (!(endTime > startTime)) throw new InvalidOperationException("Publication interval did not advance time.");
                start = sample(startTime); end = sample(endTime); EngineSampleCount += 2; initialized = true;
            }
            else if (universalTime >= endTime)
            {
                if (universalTime == endTime) { startTime = endTime; start = end; EngineSampleCount++; }
                else { startTime = universalTime; start = sample(startTime); EngineSampleCount += 2; }
                endTime = startTime + intervalSeconds;
                if (!(endTime > startTime)) throw new InvalidOperationException("Publication interval did not advance time.");
                end = sample(endTime);
            }
            lastTime = universalTime;
            double h = endTime - startTime, u = (universalTime - startTime) / h;
            double u2 = u * u, u3 = u2 * u;
            double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u;
            double h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
            Vec position = start.Position * h00 + start.Velocity * (h10 * h) + end.Position * h01 + end.Velocity * (h11 * h);
            Vec velocity = start.Position * ((6 * u2 - 6 * u) / h) + start.Velocity * (3 * u2 - 4 * u + 1) +
                end.Position * ((-6 * u2 + 6 * u) / h) + end.Velocity * (3 * u2 - 2 * u);
            return new CoastingBody(start.Id, position, velocity);
        }
    }
}
