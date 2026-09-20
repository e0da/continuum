using System;
using System.Text.RegularExpressions;

namespace KspContinuum.Mission
{
    public static class SurveyPolicy
    {
        public const double Latitude = -4.794139, Longitude = -11.575088;
        public const double MaximumSlope = 2, MaximumDistance = 100, MinimumSun = 20, MaximumTilt = 10, MaximumAngularSpeed = 0.01;
        public const int GridHalfWidth = 11;
        public const double GridSpacing = 10, SunSampleSeconds = 60;
        public const double Radians = Math.PI / 180;

        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        public static double Distance(double radius, double lat1, double lon1, double lat2, double lon2)
        {
            if (!Finite(radius) || radius <= 0 || !Finite(lat1) || !Finite(lon1) || !Finite(lat2) || !Finite(lon2)) return double.NaN;
            double a = Math.Pow(Math.Sin((lat2 - lat1) * Radians / 2), 2) + Math.Cos(lat1 * Radians) * Math.Cos(lat2 * Radians) * Math.Pow(Math.Sin((lon2 - lon1) * Radians / 2), 2);
            return 2 * radius * Math.Asin(Math.Sqrt(Math.Max(0, Math.Min(1, a))));
        }

        public static double FutureLongitude(double longitude, double elapsed, double period)
        {
            if (!Finite(longitude) || !Finite(elapsed) || !Finite(period) || period == 0) throw new ArgumentException("Invalid body rotation.");
            return ((longitude + 360 * elapsed / period) % 360 + 540) % 360 - 180;
        }

        public static double FindWindow(double start, double horizon, double duration, Func<double, bool> qualifies)
        {
            if (!Finite(start) || !Finite(horizon) || !Finite(duration) || horizon <= 0 || duration <= 0 || duration > horizon || horizon > 1000000)
                throw new ArgumentException("Invalid daylight search bounds.");
            int length = (int)Math.Ceiling(duration / SunSampleSeconds);
            int consecutive = 0;
            int count = (int)Math.Floor(horizon / SunSampleSeconds);
            for (int i = 0; i <= count; i++)
            {
                consecutive = qualifies(start + i * SunSampleSeconds) ? consecutive + 1 : 0;
                if (consecutive >= length + 1) return start + (i - length) * SunSampleSeconds;
            }
            return double.NaN;
        }

        public static double ArrivalForecast(double[] secondSamples, double elapsed)
        {
            if (secondSamples == null || secondSamples.Length < 2 || !Finite(elapsed) || elapsed < 0 || elapsed > secondSamples.Length - 1) return double.NaN;
            int i = Math.Min((int)Math.Floor(elapsed), secondSamples.Length - 2);
            return secondSamples[i] + (secondSamples[i + 1] - secondSamples[i]) * (elapsed - i);
        }

        public static bool Accept(double distance, double sun, double tilt, double angularSpeed, bool eclipsed)
        {
            return Finite(distance) && distance >= 0 && distance <= MaximumDistance && Finite(sun) && sun >= MinimumSun &&
                Finite(tilt) && tilt >= 0 && tilt <= MaximumTilt && Finite(angularSpeed) && angularSpeed >= 0 && angularSpeed < MaximumAngularSpeed && !eclipsed;
        }

        public static bool ValidAttemptId(string value) { return value != null && Regex.IsMatch(value, "\\ACSP-0002-A[0-9]{3,6}\\z"); }
    }
}
