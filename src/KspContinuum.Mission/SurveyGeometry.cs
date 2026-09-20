using System;
using System.Collections.Generic;

namespace KspContinuum.Mission
{
    public struct SurveyVector
    {
        public double X, Y, Z;
        public SurveyVector(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static SurveyVector operator +(SurveyVector a, SurveyVector b) { return new SurveyVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static SurveyVector operator -(SurveyVector a, SurveyVector b) { return new SurveyVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static SurveyVector operator *(SurveyVector a, double k) { return new SurveyVector(a.X * k, a.Y * k, a.Z * k); }
        public double Magnitude { get { return Math.Sqrt(Dot(this, this)); } }
        public SurveyVector Unit { get { double m = Magnitude; if (!SurveyPolicy.Finite(m) || m == 0) throw new ArgumentException("Invalid direction."); return this * (1 / m); } }
        public static double Dot(SurveyVector a, SurveyVector b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static SurveyVector Cross(SurveyVector a, SurveyVector b) { return new SurveyVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    }

    public sealed class SurveySample
    {
        public double Latitude, Longitude, Height;
        public SurveyVector Position;
    }

    public sealed class SurveyFootprint
    {
        public double MaximumSlope, MinimumHeight, MaximumHeight, CenterHeight;
        public readonly List<SurveySample> Samples = new List<SurveySample>();
    }

    public static class SurveyGeometry
    {
        public static SurveyFootprint Sample(double radius, Func<double, double, double> height)
        {
            if (!SurveyPolicy.Finite(radius) || radius <= 0) throw new ArgumentException("Invalid body radius.");
            double lat = SurveyPolicy.Latitude * SurveyPolicy.Radians, lon = SurveyPolicy.Longitude * SurveyPolicy.Radians;
            var center = new SurveyVector(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), Math.Cos(lat) * Math.Sin(lon));
            var north = new SurveyVector(-Math.Sin(lat) * Math.Cos(lon), Math.Cos(lat), -Math.Sin(lat) * Math.Sin(lon));
            var east = new SurveyVector(-Math.Sin(lon), 0, Math.Cos(lon));
            int half = SurveyPolicy.GridHalfWidth, width = half * 2 + 1;
            var points = new SurveyVector[width, width];
            var result = new SurveyFootprint { MinimumHeight = double.PositiveInfinity, MaximumHeight = double.NegativeInfinity };
            for (int i = 0; i < width; i++)
                for (int j = 0; j < width; j++)
                {
                    SurveyVector offset = east * ((i - half) * SurveyPolicy.GridSpacing) + north * ((j - half) * SurveyPolicy.GridSpacing);
                    double distance = offset.Magnitude;
                    SurveyVector normal = distance == 0 ? center : center * Math.Cos(distance / radius) + offset.Unit * Math.Sin(distance / radius);
                    double latitude = Math.Asin(Math.Max(-1, Math.Min(1, normal.Y))) / SurveyPolicy.Radians;
                    double longitude = Math.Atan2(normal.Z, normal.X) / SurveyPolicy.Radians;
                    double altitude = height(latitude, longitude);
                    if (!SurveyPolicy.Finite(altitude) || radius + altitude <= 0) throw new ArgumentException("Invalid sampled terrain height.");
                    points[i, j] = normal * (radius + altitude);
                    result.MinimumHeight = Math.Min(result.MinimumHeight, altitude);
                    result.MaximumHeight = Math.Max(result.MaximumHeight, altitude);
                    if (i == half && j == half) result.CenterHeight = altitude;
                    result.Samples.Add(new SurveySample { Latitude = latitude, Longitude = longitude, Height = altitude, Position = points[i, j] });
                }
            for (int i = 0; i < width - 1; i++)
                for (int j = 0; j < width - 1; j++)
                {
                    result.MaximumSlope = Math.Max(result.MaximumSlope, Slope(points[i, j], points[i + 1, j], points[i, j + 1]));
                    result.MaximumSlope = Math.Max(result.MaximumSlope, Slope(points[i + 1, j + 1], points[i, j + 1], points[i + 1, j]));
                }
            return result;
        }

        static double Slope(SurveyVector a, SurveyVector b, SurveyVector c)
        {
            double cosine = Math.Abs(SurveyVector.Dot(SurveyVector.Cross(b - a, c - a).Unit, (a + b + c).Unit));
            return Math.Acos(Math.Max(0, Math.Min(1, cosine))) / SurveyPolicy.Radians;
        }

        public static double Elevation(SurveyVector normal, SurveyVector sun)
        {
            return Math.Asin(Math.Max(-1, Math.Min(1, SurveyVector.Dot(normal.Unit, sun.Unit)))) / SurveyPolicy.Radians;
        }

        public static bool Occludes(SurveyVector blocker, double radius, SurveyVector sun)
        {
            if (!SurveyPolicy.Finite(radius) || radius <= 0) throw new ArgumentException("Invalid occulting radius.");
            double distance = sun.Magnitude;
            double along = SurveyVector.Dot(blocker, sun.Unit);
            if (!SurveyPolicy.Finite(along)) throw new ArgumentException("Invalid occulting position.");
            return along > 0 && along < distance && (blocker - sun.Unit * along).Magnitude <= radius;
        }
    }
}
