using System;

namespace KspContinuum
{
    public sealed class CoastPresentationCadence
    {
        readonly double intervalSeconds;
        bool published;
        double nextUniversalTime, lastUniversalTime;

        public CoastPresentationCadence(double intervalSeconds)
        {
            if (double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds) || intervalSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            this.intervalSeconds = intervalSeconds;
        }

        public double IntervalSeconds { get { return intervalSeconds; } }

        public bool ShouldPublish(double universalTime)
        {
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime))
                throw new ArgumentOutOfRangeException(nameof(universalTime));
            if (!published)
            {
                published = true;
                lastUniversalTime = universalTime;
                nextUniversalTime = universalTime + intervalSeconds;
                return true;
            }
            if (universalTime < lastUniversalTime)
                throw new InvalidOperationException("Presentation universal time moved backwards.");
            lastUniversalTime = universalTime;
            if (universalTime < nextUniversalTime) return false;
            double elapsedIntervals = Math.Floor((universalTime - nextUniversalTime) / intervalSeconds) + 1;
            nextUniversalTime += elapsedIntervals * intervalSeconds;
            return true;
        }
    }
}
