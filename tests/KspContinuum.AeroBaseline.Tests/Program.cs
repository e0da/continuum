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
        bool shielded = false, AeroDragCubeState[] cubes = null, int ordinal = 0, AeroSetDragInputs setDragInputs = null) => new AeroPartContext(Step(ordinal), id,
        checked((int)id + 10), checked((int)id + 20), 10, density, 100000, 280, 330, .5, 1, 1, shielded,
        center, air * -1, air, new Vec(), new Vec(attitude.X, attitude.Y, attitude.Z), attitude.W,
        cubes ?? new[] { Cube(new Vec(.2, -.1, .3)) }, setDragInputs ?? SetDragInputs());
    static AeroSetDragInputs SetDragInputs()
    {
        var curve = new AeroFloatCurveDefinition(0, 0, new[] { new AeroCurveKey(0, 1, 0, 0, 0, 0, 0) });
        return new AeroSetDragInputs(new double[6], new double[6], new AeroSurfaceCurveDefinitions(curve, curve, curve, curve), curve, curve);
    }

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

    static void SetDragCaptureSufficiency()
    {
        var cube = new AeroDragCubeState("0", 1, new Vec(0, 0, -5.3640002306565293E-7),
            new Vec(2.5, 7.5479998588562012, 2.5),
            new[] { 57d, 57, 6.25, 6.25, 57, 57 },
            new[] { 0.80440002679824829, 0.80440002679824829, 0.80440002679824829,
                0.80440002679824829, 0.80440002679824829, 0.80440002679824829 },
            new[] { 2.1710000038146973, 2.1710000038146973, 6.2290000915527344,
                6.2290000915527344, 2.1710000038146973, 2.1710000038146973 }, new double[6]);
        var cubes = new[] { cube };
        var firstDirection = new Vec(-0.029881614937938034, 0.9995357712381214, -0.005964350166957455);
        var secondDirection = new Vec(-0.029890425402045367, 0.999535451188317, -0.005966529116822439);
        AeroSetDragInputAudit audit = AeroSetDragDiagnostic.Audit(firstDirection, cubes);
        Check(audit.Disposition == AeroSetDragReproductionDisposition.Insufficient,
            "captured drag cubes do not contain SetDrag runtime state");
        Check(audit.MissingRuntimeInputs.Contains("DragCubeList.areaOccluded[6]"),
            "occluded face areas are the dynamic missing input");
        Check(audit.MissingRuntimeInputs.Contains("DragCubeList.weightedDrag[6] after attachment occlusion"),
            "post-occlusion drag coefficients are also dynamic missing inputs");
        Check(audit.MissingRuntimeInputs.Contains("DragCubeList.SurfaceCurves") &&
            audit.MissingRuntimeInputs.Contains("DragCubeList.DragCurveCd") &&
            audit.MissingRuntimeInputs.Contains("DragCubeList.DragCurveCdPower"),
            "stock curve parameters are also outside the capture schema");

        AeroSetDragComparison comparison = AeroSetDragDiagnostic.Compare(firstDirection, 3.5900653078953515,
            1.7085930109024048, secondDirection, 3.5900653078953515, 2.5672118663787842,
            cubes, 1e-4, .3);
        Check(comparison.ExposesHiddenRuntimeState, "representative live labels expose hidden SetDrag state");
        Check(comparison.DirectionDelta < 1e-5 && comparison.MachDelta == 0 && comparison.ProxyRelativeDelta < 1e-4,
            "representative captured inputs are near-identical");
        Check(comparison.ObservedRelativeDelta > .33,
            "near-identical captured inputs have materially different stock AreaDrag labels");

        var neighboring = AeroSetDragDiagnostic.Compare(firstDirection, 3.5900653078953515,
            1.7085930109024048, new Vec(-0.02988575325752721, 0.9995356576634218, -0.005966136361507335),
            3.5900653078953515, 1.7088568210601807, cubes, 1e-4, .3);
        Check(!neighboring.ExposesHiddenRuntimeState && neighboring.ObservedRelativeDelta < .001,
            "diagnostic does not flag neighboring live labels that agree");
    }

    static AeroFloatCurveDefinition ConstantCurve(double value, int weightedMode = 0) =>
        new AeroFloatCurveDefinition(0, 0, new[] { new AeroCurveKey(0, value, 0, 0, 0, 0, weightedMode) });
    static AeroFloatCurveDefinition LinearCurve(double left, double right) =>
        new AeroFloatCurveDefinition(0, 0, new[] {
            new AeroCurveKey(0, left, right - left, right - left, 0, 0, 0),
            new AeroCurveKey(1, right, right - left, right - left, 0, 0, 0) });
    static AeroSetDragInputs ReconstructionInputs(double[] areas, double[] drag,
        AeroFloatCurveDefinition tail = null) => new AeroSetDragInputs(areas, drag,
            new AeroSurfaceCurveDefinitions(tail ?? ConstantCurve(.2), ConstantCurve(.4), ConstantCurve(2), ConstantCurve(.8)),
            LinearCurve(0, 2), ConstantCurve(2));

    static void SetDragReconstruction()
    {
        var areas = new[] { 1d, 2, 3, 4, 5, 6 };
        var drag = new[] { 2d, 2, 2, 2, 2, 2 };
        AeroSetDragResult positive = AeroSetDragReconstruction.Evaluate(new Vec(3, 0, 0), .5,
            ReconstructionInputs(areas, drag));
        Check(positive.Disposition == AeroSetDragDisposition.Valid && positive.Reason == AeroSetDragReason.None,
            "supported SetDrag inputs are reconstructed");
        Near(33.6, positive.AreaDragSquareMeters, "hand-computed positive-face SetDrag", 1e-12);
        Near(36, AeroSetDragReconstruction.Evaluate(new Vec(-1, 0, 0), .5,
            ReconstructionInputs(areas, drag)).AreaDragSquareMeters, "opposing face selection", 1e-12);

        var scaledAreas = new[] { 3d, 6, 9, 12, 15, 18 };
        Near(positive.AreaDragSquareMeters * 3, AeroSetDragReconstruction.Evaluate(new Vec(1, 0, 0), .5,
            ReconstructionInputs(scaledAreas, drag)).AreaDragSquareMeters, "occluded-area scaling", 1e-12);
        Near(0, AeroSetDragReconstruction.Evaluate(new Vec(1, 2, 3), .5,
            ReconstructionInputs(new double[6], drag)).AreaDragSquareMeters, "zero occluded area", 0);

        var subunitAreas = new[] { 4d, 0, 0, 0, 0, 0 };
        var subunitDrag = new[] { .25d, 2, 2, 2, 2, 2 };
        Near(1.6, AeroSetDragReconstruction.Evaluate(new Vec(1, 0, 0), .5,
            ReconstructionInputs(subunitAreas, subunitDrag)).AreaDragSquareMeters,
            "subunit drag uses Cd and Mach-power curves", 1e-12);

        var context = Part(1, new Vec(), new Vec(-10, 0, 0), Rotation.Identity,
            setDragInputs: ReconstructionInputs(areas, drag));
        Near(positive.AreaDragSquareMeters, AeroSetDragReconstruction.Evaluate(context).AreaDragSquareMeters,
            "part context derives normalized local drag direction", 1e-12);
        Check(AeroSetDragReconstruction.Evaluate(Part(1, new Vec(), new Vec(), Rotation.Identity,
            setDragInputs: ReconstructionInputs(areas, drag))).Reason == AeroSetDragReason.ZeroFlow,
            "zero flow has a deterministic zero result");

        AeroSetDragResult weighted = AeroSetDragReconstruction.Evaluate(new Vec(1, 0, 0), .5,
            ReconstructionInputs(areas, drag, ConstantCurve(.2, 1)));
        Check(weighted.Disposition == AeroSetDragDisposition.Abstained &&
            weighted.Reason == AeroSetDragReason.UnsupportedWeightedCurve, "weighted curves abstain explicitly");
        var bounded = new AeroFloatCurveDefinition(0, 0, new[] {
            new AeroCurveKey(0, .2, 0, 0, 0, 0, 0), new AeroCurveKey(1, .2, 0, 0, 0, 0, 0) });
        AeroSetDragResult outside = AeroSetDragReconstruction.Evaluate(new Vec(1, 0, 0), 2,
            ReconstructionInputs(areas, drag, bounded));
        Check(outside.Disposition == AeroSetDragDisposition.Abstained &&
            outside.Reason == AeroSetDragReason.OutsideCurveDomain, "curve extrapolation abstains explicitly");
    }

    static int Main()
    {
        DynamicPressureAndFaces(); MetamorphicBehavior(); DomainAndBatches(); RegimeMatrix(); SetDragCaptureSufficiency();
        SetDragReconstruction();
        Console.WriteLine("PASS " + checks + " aerodynamic baseline assertions");
        return 0;
    }
}
