using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum AeroSpeedRegime { Subsonic, Transonic, Supersonic, Hypersonic }
    public enum AeroAttitudeCase { Axial, LowAngle, HighAngle, Broadside }
    public enum AeroGeometryCase { Capsule, SlenderBody, FlatPlate }

    public sealed class AeroRegimeCase
    {
        readonly int ordinal;
        internal AeroRegimeCase(int ordinal, string id, AeroSpeedRegime speedRegime, AeroAttitudeCase attitude,
            AeroGeometryCase geometry, double mach, double altitude, double density, double pressure,
            double temperature, double speedOfSound, double angleOfAttack, double sideslip)
        {
            this.ordinal = ordinal; Id = id; SpeedRegime = speedRegime; Attitude = attitude; Geometry = geometry; Mach = mach;
            AltitudeMeters = altitude; DensityKilogramsPerCubicMeter = density; StaticPressurePascals = pressure;
            TemperatureKelvin = temperature; SpeedOfSoundMetersPerSecond = speedOfSound;
            AngleOfAttackDegrees = angleOfAttack; SideslipDegrees = sideslip;
        }

        public string Id { get; private set; }
        public AeroSpeedRegime SpeedRegime { get; private set; }
        public AeroAttitudeCase Attitude { get; private set; }
        public AeroGeometryCase Geometry { get; private set; }
        public double Mach { get; private set; }
        public double AltitudeMeters { get; private set; }
        public double DensityKilogramsPerCubicMeter { get; private set; }
        public double StaticPressurePascals { get; private set; }
        public double TemperatureKelvin { get; private set; }
        public double SpeedOfSoundMetersPerSecond { get; private set; }
        public double AngleOfAttackDegrees { get; private set; }
        public double SideslipDegrees { get; private set; }

        public AeroPartContext CreateInput()
        {
            double radians = Math.PI / 180;
            Rotation attitude = Rotation.AxisAngle(new Vec(0, 0, 1), AngleOfAttackDegrees * radians) *
                Rotation.AxisAngle(new Vec(0, 1, 0), SideslipDegrees * radians);
            double speed = Mach * SpeedOfSoundMetersPerSecond;
            var step = new AeroCaptureContext("00000000-0000-0000-0000-000000000001",
                "00000000-0000-0000-0000-000000000002", "aero-regime-matrix", 1, 1, 1,
                ordinal + 1, 1, ordinal, ordinal * .02, ordinal * .02, .02);
            return new AeroPartContext(step, ordinal + 1, ordinal + 101, ordinal + 201, 1,
                DensityKilogramsPerCubicMeter, StaticPressurePascals, TemperatureKelvin,
                SpeedOfSoundMetersPerSecond, Mach, 1, 1, false, new Vec(), new Vec(speed, 0, 0),
                new Vec(speed, 0, 0), new Vec(), new Vec(attitude.X, attitude.Y, attitude.Z), attitude.W,
                new[] { GeometryCube(Geometry) });
        }

        static AeroDragCubeState GeometryCube(AeroGeometryCase geometry)
        {
            if (geometry == AeroGeometryCase.Capsule)
                return Cube("capsule", new Vec(1.25, 1.25, 1.25), new[] { 1.2, 1.2, 1.6, 1.6, 1.6, 1.6 },
                    new[] { .9, .9, .8, .8, .8, .8 }, new Vec(.08, 0, 0));
            if (geometry == AeroGeometryCase.SlenderBody)
                return Cube("slender-body", new Vec(3.5, .7, .7), new[] { .45, .65, 2.4, 2.4, 2.4, 2.4 },
                    new[] { .3, .45, .85, .85, .85, .85 }, new Vec(-.15, 0, 0));
            return Cube("flat-plate", new Vec(.12, 2.4, 1.6), new[] { 3.8, 3.8, .2, .2, .3, .3 },
                new[] { 1.15, 1.15, .25, .25, .3, .3 }, new Vec(0, .12, 0));
        }

        static AeroDragCubeState Cube(string name, Vec size, double[] area, double[] drag, Vec center)
        {
            return new AeroDragCubeState(name, 1, center, size, area, drag,
                new[] { size.X, size.X, size.Y, size.Y, size.Z, size.Z }, new[] { 1d, 1, 1, 1, 1, 1 });
        }
    }

    public sealed class AeroRegimeExperimentResult
    {
        internal AeroRegimeExperimentResult(AeroRegimeCase experimentCase, AeroPartContext input, AeroBaselineResult result)
        { Case = experimentCase; Input = input; Baseline = result; }
        public AeroRegimeCase Case { get; private set; }
        public AeroPartContext Input { get; private set; }
        public AeroBaselineResult Baseline { get; private set; }
    }

    public static class AeroRegimeMatrix
    {
        public const int MaximumCases = 16;
        static readonly ReadOnlyCollection<AeroRegimeCase> cases = Build();
        public static ReadOnlyCollection<AeroRegimeCase> Cases { get { return cases; } }

        public static ReadOnlyCollection<AeroRegimeCase> Select(IEnumerable<string> caseIds)
        {
            if (caseIds == null) throw new ArgumentNullException("caseIds");
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in caseIds)
            {
                if (id == null || !requested.Add(id) || requested.Count > MaximumCases)
                    throw new ArgumentException("Regime selection must contain unique known case IDs within the matrix bound.", "caseIds");
            }
            if (requested.Count == 0) throw new ArgumentException("Regime selection cannot be empty.", "caseIds");
            var selected = new List<AeroRegimeCase>();
            foreach (AeroRegimeCase item in cases) if (requested.Remove(item.Id)) selected.Add(item);
            if (requested.Count != 0) throw new ArgumentException("Regime selection contains an unknown case ID.", "caseIds");
            return new ReadOnlyCollection<AeroRegimeCase>(selected);
        }

        public static ReadOnlyCollection<AeroRegimeExperimentResult> Evaluate(IEnumerable<string> caseIds)
        {
            ReadOnlyCollection<AeroRegimeCase> selected = Select(caseIds);
            var results = new List<AeroRegimeExperimentResult>(selected.Count);
            for (int index = 0; index < selected.Count; index++)
            {
                AeroPartContext input = selected[index].CreateInput();
                results.Add(new AeroRegimeExperimentResult(selected[index], input, AeroDragCubeBaseline.Evaluate(input)));
            }
            return new ReadOnlyCollection<AeroRegimeExperimentResult>(results);
        }

        static ReadOnlyCollection<AeroRegimeCase> Build()
        {
            var regimes = new[] { AeroSpeedRegime.Subsonic, AeroSpeedRegime.Transonic,
                AeroSpeedRegime.Supersonic, AeroSpeedRegime.Hypersonic };
            var mach = new[] { .3, .95, 2.0, 6.0 };
            var altitude = new[] { 0d, 5000, 15000, 30000 };
            var density = new[] { 1.225, .736, .1948, .01841 };
            var pressure = new[] { 101325d, 54019, 12045, 1171 };
            var temperature = new[] { 288.15, 255.65, 216.65, 226.65 };
            var sound = new[] { 340.3, 320.5, 295.1, 301.7 };
            var attitudes = new[] { AeroAttitudeCase.Axial, AeroAttitudeCase.LowAngle,
                AeroAttitudeCase.HighAngle, AeroAttitudeCase.Broadside };
            var aoa = new[] { 0d, 10, 35, 90 };
            var slip = new[] { 0d, 5, -15, 25 };
            var geometries = new[] { AeroGeometryCase.Capsule, AeroGeometryCase.SlenderBody, AeroGeometryCase.FlatPlate };
            var result = new List<AeroRegimeCase>(MaximumCases);
            for (int regime = 0; regime < regimes.Length; regime++)
                for (int attitude = 0; attitude < attitudes.Length; attitude++)
                {
                    int atmosphere = (regime + attitude) % altitude.Length;
                    AeroGeometryCase geometry = geometries[(regime * attitudes.Length + attitude) % geometries.Length];
                    string id = regimes[regime].ToString().ToLowerInvariant() + "-" + attitudes[attitude].ToString().ToLowerInvariant() +
                        "-" + geometry.ToString().ToLowerInvariant();
                    result.Add(new AeroRegimeCase(result.Count, id, regimes[regime], attitudes[attitude], geometry, mach[regime],
                        altitude[atmosphere], density[atmosphere], pressure[atmosphere], temperature[atmosphere], sound[atmosphere],
                        aoa[attitude], slip[attitude]));
                }
            if (result.Count != MaximumCases) throw new InvalidOperationException("Aerodynamic regime matrix has an invalid bound.");
            return new ReadOnlyCollection<AeroRegimeCase>(result);
        }
    }
}
