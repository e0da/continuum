using System;
using System.Linq;

namespace KspContinuum.Mission
{
    internal sealed class SurveySunModel
    {
        readonly CelestialBody body, sun;
        readonly double epoch;
        readonly SurveyVector x, y, z;

        internal SurveySunModel(CelestialBody body)
        {
            this.body = body;
            sun = Planetarium.fetch.Sun;
            if (sun == null) throw new InvalidOperationException("Native Sun unavailable.");
            epoch = Planetarium.GetUniversalTime();
            x = Vector(body.GetSurfaceNVector(0, 0));
            y = Vector(body.GetSurfaceNVector(90, 0));
            z = Vector(body.GetSurfaceNVector(0, 90));
            if (body.rotates && (!SurveyPolicy.Finite(body.rotationPeriod) || body.rotationPeriod == 0))
                throw new InvalidOperationException("Invalid survey body rotation period.");
        }

        internal void Evaluate(double ut, double latitude, double longitude, double altitude, out double elevation, out bool eclipsed)
        {
            if (!SurveyPolicy.Finite(ut) || !SurveyPolicy.Finite(latitude) || !SurveyPolicy.Finite(longitude) || !SurveyPolicy.Finite(altitude))
                throw new InvalidOperationException("Nonfinite site geometry.");
            double lat = latitude * SurveyPolicy.Radians;
            double lon = (body.rotates ? SurveyPolicy.FutureLongitude(longitude, ut - epoch, body.rotationPeriod) : longitude) * SurveyPolicy.Radians;
            SurveyVector normal = x * (Math.Cos(lat) * Math.Cos(lon)) + y * Math.Sin(lat) + z * (Math.Cos(lat) * Math.Sin(lon));
            SurveyVector position = Vector(body.getTruePositionAtUT(ut)) + normal * (body.Radius + altitude);
            SurveyVector toSun = Vector(sun.getTruePositionAtUT(ut)) - position;
            elevation = SurveyGeometry.Elevation(normal, toSun);
            eclipsed = FlightGlobals.Bodies.Any(other => other != body && other != sun &&
                SurveyGeometry.Occludes(Vector(other.getTruePositionAtUT(ut)) - position, other.Radius, toSun));
        }

        internal static SurveyVector Vector(Vector3d value) { return new SurveyVector(value.x, value.y, value.z); }
    }

    internal sealed class SurveyObservation
    {
        internal double Distance, SunElevation, RadialTilt, TerrainTilt, AngularSpeed;
        internal bool Eclipsed, HasTerrainNormal;
        internal bool Qualifies { get { return HasTerrainNormal && SurveyPolicy.Accept(Distance, SunElevation, TerrainTilt, AngularSpeed, Eclipsed); } }

        internal static SurveyObservation Read(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            var result = new SurveyObservation();
            result.Distance = SurveyPolicy.Distance(body.Radius, vessel.latitude, vessel.longitude, SurveyPolicy.Latitude, SurveyPolicy.Longitude);
            new SurveySunModel(body).Evaluate(Planetarium.GetUniversalTime(), vessel.latitude, vessel.longitude, vessel.altitude,
                out result.SunElevation, out result.Eclipsed);
            SurveyVector up = SurveySunModel.Vector(vessel.rootPart.transform.up);
            SurveyVector radial = SurveySunModel.Vector(body.GetSurfaceNVector(vessel.latitude, vessel.longitude));
            result.RadialTilt = Angle(up, radial);
            SurveyVector terrain = SurveySunModel.Vector(vessel.vesselTransform.TransformDirection(vessel.terrainNormal));
            result.HasTerrainNormal = vessel.heightFromTerrain >= 0 && SurveyPolicy.Finite(vessel.heightFromTerrain) && SurveyPolicy.Finite(terrain.Magnitude) && terrain.Magnitude > 0.5;
            result.TerrainTilt = result.HasTerrainNormal ? Angle(up, terrain) : double.NaN;
            result.AngularSpeed = vessel.angularVelocity.magnitude;
            return result;
        }

        static double Angle(SurveyVector a, SurveyVector b)
        {
            return Math.Acos(Math.Max(-1, Math.Min(1, SurveyVector.Dot(a.Unit, b.Unit)))) / SurveyPolicy.Radians;
        }
    }
}
