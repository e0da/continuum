using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Near(double expected, double actual, string message, double tolerance = 1e-11)
    { Check(Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)), message + ": " + actual); }
    static void Near(Vec expected, Vec actual, string message, double tolerance = 1e-11)
    { Near(expected.X, actual.X, message + " x", tolerance); Near(expected.Y, actual.Y, message + " y", tolerance); Near(expected.Z, actual.Z, message + " z", tolerance); }

    static AeroCaptureContext Step(int ordinal = 0) => new AeroCaptureContext("00000000-0000-0000-0000-000000000001",
        "00000000-0000-0000-0000-000000000002", "frame", 1, 1, 1, 1, 1, ordinal, 0, 0, .02);
    static AeroDragCubeState Cube(Vec center, double weight = 1) => new AeroDragCubeState("cube", weight, center,
        new Vec(1, 1, 1), new[] { 2d, 3, 5, 7, 11, 13 }, new[] { .5, .4, .3, .2, .1, .05 },
        new[] { 1d, 1, 1, 1, 1, 1 }, new double[6]);
    static AeroPartContext Part(long id, Vec center, Vec air, Rotation attitude, double density = 1.2,
        bool shielded = false, AeroDragCubeState[] cubes = null, int ordinal = 0) => new AeroPartContext(Step(ordinal), id,
        checked((int)id + 10), checked((int)id + 20), 10, density, 100000, 280, 330, .5, 1, 1, shielded,
        center, air * -1, air, new Vec(), new Vec(attitude.X, attitude.Y, attitude.Z), attitude.W,
        cubes ?? new[] { Cube(new Vec(.2, -.1, .3)) });

    static void DynamicPressureAndFaces()
    {
        var result = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(), new Vec(10, 0, 0), Rotation.Identity));
        Near(60, result.DynamicPressurePascals, "dynamic pressure");
        Near(1.2, result.WeightedProjectedAreaSquareMeters, "positive X motion selects negative X airflow face");
        Near(new Vec(-72, 0, 0), result.ForceNewtons, "force opposes relative motion");
        var reverse = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(), new Vec(-10, 0, 0), Rotation.Identity));
        Near(1, reverse.WeightedProjectedAreaSquareMeters, "negative X motion selects positive X airflow face");
        Near(new Vec(60, 0, 0), reverse.ForceNewtons, "reverse force");
    }

    static void MetamorphicBehavior()
    {
        var basePart = Part(1, new Vec(1, 2, 3), new Vec(8, -3, 2), Rotation.Identity);
        var baseline = AeroDragCubeBaseline.Evaluate(basePart);
        var translated = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(101, -48, 10), new Vec(8, -3, 2), Rotation.Identity));
        Near(baseline.ForceNewtons, translated.ForceNewtons, "translation invariant force");
        Near(new Vec(100, -50, 7), new Vec(translated.WorldApplicationPosition.X - baseline.WorldApplicationPosition.X,
            translated.WorldApplicationPosition.Y - baseline.WorldApplicationPosition.Y,
            translated.WorldApplicationPosition.Z - baseline.WorldApplicationPosition.Z), "translated application point");
        Near(baseline.TorqueAboutPartCenterOfMassNewtonMeters, translated.TorqueAboutPartCenterOfMassNewtonMeters, "translation invariant torque");

        Rotation frame = Rotation.AxisAngle(new Vec(1, 2, 3), .7);
        Vec shifted = new Vec(-20, 4, 7);
        var rotated = AeroDragCubeBaseline.Evaluate(Part(1, frame.Rotate(basePart.worldCenterOfMass) + shifted,
            frame.Rotate(basePart.relativeAirVelocity), frame));
        Near(frame.Rotate(baseline.ForceNewtons), rotated.ForceNewtons, "rotation equivariant force", 2e-11);
        Near(frame.Rotate(baseline.TorqueAboutPartCenterOfMassNewtonMeters), rotated.TorqueAboutPartCenterOfMassNewtonMeters,
            "rotation equivariant torque", 2e-11);

        var dense = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(), new Vec(8, -3, 2), Rotation.Identity, 2.4));
        Near(baseline.ForceNewtons * 2, dense.ForceNewtons, "density scaling");
        var fast = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(1, 2, 3), new Vec(16, -6, 4), Rotation.Identity));
        Near(baseline.ForceNewtons * 4, fast.ForceNewtons, "speed squared scaling");
        Near(baseline.TorqueAboutPartCenterOfMassNewtonMeters * 4, fast.TorqueAboutPartCenterOfMassNewtonMeters, "torque speed scaling");
    }

    static void DomainAndBatches()
    {
        foreach (var part in new[] {
            Part(1, new Vec(), new Vec(), Rotation.Identity),
            Part(1, new Vec(), new Vec(10, 0, 0), Rotation.Identity, 0),
            Part(1, new Vec(), new Vec(10, 0, 0), Rotation.Identity, shielded: true) })
            Near(new Vec(), AeroDragCubeBaseline.Evaluate(part).ForceNewtons, "zero response");
        var absent = AeroDragCubeBaseline.Evaluate(Part(1, new Vec(), new Vec(1, 0, 0), Rotation.Identity,
            cubes: Array.Empty<AeroDragCubeState>()));
        Check(absent.Disposition == AeroBaselineDisposition.Abstained && absent.Reason == AeroBaselineReason.NoDragCubes,
            "missing geometry must abstain");

        var inputs = new List<AeroPartContext>();
        for (int index = 0; index < 257; index++) inputs.Add(Part(index + 1, new Vec(index, 0, 0),
            new Vec(index % 11 - 5, index % 7 - 3, index % 5 - 2), Rotation.AxisAngle(new Vec(1, 2, 3), index * .01),
            ordinal: index % 256));
        AeroBaselineResult[] scalar = AeroDragCubeBaseline.EvaluateScalar(inputs);
        AeroBaselineResult[] parallel = AeroDragCubeBaseline.EvaluateParallel(inputs, 4);
        for (int index = 0; index < scalar.Length; index++)
        {
            Check(scalar[index].FlightId == inputs[index].flightId && parallel[index].FlightId == scalar[index].FlightId,
                "permutation/order identity " + index);
            Near(scalar[index].ForceNewtons, parallel[index].ForceNewtons, "parallel force " + index, 0);
            Near(scalar[index].TorqueAboutPartCenterOfMassNewtonMeters, parallel[index].TorqueAboutPartCenterOfMassNewtonMeters,
                "parallel torque " + index, 0);
        }
        AeroBaselineResult[] replay = AeroDragCubeBaseline.EvaluateParallel(inputs, 4);
        for (int index = 0; index < replay.Length; index++) Near(parallel[index].ForceNewtons, replay[index].ForceNewtons,
            "parallel replay " + index, 0);
    }

    static bool Reject<T>(Func<T> action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    static void RegimeMatrix()
    {
        Check(AeroRegimeMatrix.Cases.Count == AeroRegimeMatrix.MaximumCases, "matrix must retain its declared bound");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var regimes = new HashSet<AeroSpeedRegime>();
        var attitudes = new HashSet<AeroAttitudeCase>();
        var geometries = new HashSet<AeroGeometryCase>();
        var altitudes = new HashSet<double>();
        var densities = new HashSet<double>();
        foreach (AeroRegimeCase item in AeroRegimeMatrix.Cases)
        {
            Check(ids.Add(item.Id), "case IDs must be unique");
            regimes.Add(item.SpeedRegime); attitudes.Add(item.Attitude); geometries.Add(item.Geometry);
            altitudes.Add(item.AltitudeMeters); densities.Add(item.DensityKilogramsPerCubicMeter);
            AeroPartContext input = item.CreateInput();
            Near(item.Mach, input.mach, "case Mach survives capture contract", 0);
            Near(item.Mach * item.SpeedOfSoundMetersPerSecond, input.relativeAirVelocity.X, "case speed derives from Mach", 1e-12);
            Check(input.dragCubes.Count == 1, "representative geometry uses the capture drag-cube contract");
        }
        Check(regimes.Count == 4, "matrix spans four speed regimes");
        Check(attitudes.Count == 4, "matrix spans four attitudes");
        Check(geometries.Count == 3, "matrix spans three representative geometries");
        Check(altitudes.Count == 4 && densities.Count == 4, "matrix spans four atmosphere states");

        string first = AeroRegimeMatrix.Cases[0].Id, last = AeroRegimeMatrix.Cases[AeroRegimeMatrix.Cases.Count - 1].Id;
        var selected = AeroRegimeMatrix.Select(new[] { last, first });
        Check(selected.Count == 2 && selected[0].Id == first && selected[1].Id == last,
            "selection order must be canonical and independent of request order");
        Check(selected[1].CreateInput().flightId == AeroRegimeMatrix.MaximumCases,
            "case identity remains stable when the selection is sparse");
        Check(Reject(() => AeroRegimeMatrix.Select(Array.Empty<string>())), "empty selection rejected");
        Check(Reject(() => AeroRegimeMatrix.Select(new[] { first, first })), "duplicate selection rejected");
        Check(Reject(() => AeroRegimeMatrix.Select(new[] { "unknown" })), "unknown selection rejected");

        var allIds = new List<string>();
        foreach (AeroRegimeCase item in AeroRegimeMatrix.Cases) allIds.Add(item.Id);
        var run = AeroRegimeMatrix.Evaluate(allIds);
        Check(run.Count == AeroRegimeMatrix.MaximumCases, "bounded experiment evaluates every selected case");
        for (int index = 0; index < run.Count; index++)
        {
            Check(run[index].Case.Id == AeroRegimeMatrix.Cases[index].Id, "experiment order is deterministic");
            Check(run[index].Baseline.Disposition == AeroBaselineDisposition.Valid, "matrix case is inside baseline domain");
            Check(double.IsFinite(run[index].Baseline.ForceNewtons.X) && double.IsFinite(run[index].Baseline.ForceNewtons.Y) &&
                double.IsFinite(run[index].Baseline.ForceNewtons.Z), "matrix result is finite");
        }
        var replay = AeroRegimeMatrix.Evaluate(allIds);
        for (int index = 0; index < run.Count; index++)
            Near(run[index].Baseline.ForceNewtons, replay[index].Baseline.ForceNewtons, "matrix replay " + index, 0);
    }

    static int Main()
    {
        DynamicPressureAndFaces(); MetamorphicBehavior(); DomainAndBatches(); RegimeMatrix();
        Console.WriteLine("PASS " + checks + " aerodynamic baseline assertions");
        return 0;
    }
}
