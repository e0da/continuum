using System;
using System.Collections.Generic;
using System.Text.Json;
using KspContinuum;

static class Program
{
    sealed class FakeRuntime : IAeroCapturePatchRuntime
    {
        public AeroProviderFingerprint Provider { get; set; } = new AeroProviderFingerprint("stock-flight-integrator", "1.12.5", "Assembly-CSharp",
            AeroCaptureRun.StockAssemblySha256, AeroCaptureRun.StockAssemblyMvid);
        public AeroPatchProvenance Installed { get; set; } = Program.Provenance();
        public AeroPatchProvenance Inspected { get; set; } = Program.Provenance();
        public AeroCleanupOutcome Cleanup { get; set; } = AeroCleanupOutcome.RemovedOwnedPatches;
        public bool ThrowOnInstall { get; set; }
        public int installs, inspections, removals;
        public AeroPatchProvenance Install(string owner) { installs++; Check(owner == AeroCaptureRun.Owner); if (ThrowOnInstall) throw new InvalidOperationException("partial install"); return Installed; }
        public AeroPatchProvenance Inspect(string owner) { inspections++; return Inspected; }
        public AeroCleanupOutcome Remove(string owner) { removals++; return Cleanup; }
    }
    static int checks;
    static void Check(bool condition) { checks++; if (!condition) throw new Exception("Aero capture assertion " + checks); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static readonly string Hash = new string('a', 64);
    static AeroProviderFingerprint Provider() => new AeroProviderFingerprint("stock-flight-integrator", "1.12.5", "Assembly-CSharp", Hash, "10657063-2fc3-43a7-84fa-d39e75e877bf");
    static AeroPatchTarget Target(string method, AeroPatchEntry[] patches = null) => new AeroPatchTarget(method, patches ?? Array.Empty<AeroPatchEntry>());
    static AeroPatchProvenance Provenance() => new AeroPatchProvenance(Provider(), "continuum.capture", new[] {
        Target("FlightIntegrator.UpdateAerodynamics", new[] {
            new AeroPatchEntry("foreign.owner", "Foreign.Prefix", "prefix", 0, Hash),
            new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePrefix", "prefix", 1, Hash),
            new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePostfix", "postfix", 2, Hash,
                AeroPatchEntry.PriorityLast, null, new[] { "foreign.owner" }),
            new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdateFinalizer", "finalizer", 3, Hash, AeroPatchEntry.PriorityLast) }),
        Target("FlightIntegrator.ApplyAeroDrag", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.DragPrefix", "prefix", 0, Hash) }),
        Target("FlightIntegrator.ApplyAeroLift", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.LiftPrefix", "prefix", 0, Hash) }) });
    static AeroCaptureContext Step(int ordinal = 0, long epoch = 1) => new AeroCaptureContext("{00000000-0000-0000-0000-000000000001}",
        "00000000-0000-0000-0000-000000000002", "frame-1", epoch, 1, 1, 7, 1, ordinal, 100, 2, .02);
    static AeroPartContext Part(AeroCaptureContext step, long flightId = 1, double density = 1.2, AeroDragCubeState[] cubes = null) => new AeroPartContext(step, flightId,
        checked((int)flightId + 3), checked((int)flightId + 4), 100, density, 101325, 288.15, 340, .8, 2, 1.5, false,
        new Vec(1, 2, 3), new Vec(20, 0, 0), new Vec(-20, 0, 0), new Vec(0, .1, 0), new Vec(), 1,
        cubes ?? new[] { Cube() });
    static AeroDragCubeState Cube(double weight = .75) => new AeroDragCubeState("Default", weight, new Vec(.1, .2, .3), new Vec(1, 2, 3),
        new[] { 1d, 2, 3, 4, 5, 6 }, new[] { .1, .2, .3, .4, .5, .6 }, new[] { 2d, 3, 4, 5, 6, 7 }, new[] { 1d, 1, 1, 1, 1, 1 });
    static AeroBodyPublication Publication(AeroCaptureContext step, AeroPublicationKind kind, long flightId = 1, double density = 1.2) =>
        new AeroBodyPublication(Part(step, flightId, density), kind, AeroApplicationMode.AtWorldPosition,
            new Vec(0, -2, 0), new Vec(2, 2, 3), new Vec(0, 0, -2));

    static void Main()
    {
        var selection = AeroQualificationSelection.Parse(new[] { "ksp", "--continuum-aero-save", "scenarios",
            "--continuum-aero-checkpoint", "Jool Aerobrake" });
        Check(selection.Save == "scenarios" && selection.Checkpoint == "Jool Aerobrake");
        Check(Reject(() => AeroQualificationSelection.Parse(new[] { "--continuum-aero-save", "../saves", "--continuum-aero-checkpoint", "flight" })));
        Check(Reject(() => AeroQualificationSelection.Parse(new[] { "--continuum-aero-save", "scenarios", "--continuum-aero-checkpoint", "../persistent" })));
        Check(Reject(() => AeroQualificationSelection.Parse(new[] { "--continuum-aero-save", "scenarios", "--continuum-aero-save", "other", "--continuum-aero-checkpoint", "flight" })));
        Check(Reject(() => AeroQualificationSelection.Parse(new[] { "--continuum-aero-save", "scenarios" })));
        var step = Step(); var drag = Publication(step, AeroPublicationKind.BodyDrag); var source = new[] { drag };
        var sample = new AeroCaptureSample(step, source); source[0] = null;
        Check(sample.publications[0] == drag && step.sessionId == "00000000-0000-0000-0000-000000000001");
        bool readOnly = false; try { ((IList<AeroBodyPublication>)sample.publications)[0] = null; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly);
        var report = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample });
        Check(report.schema == "ksp-continuum-aero-capture/v1" && report.samples.Count == 1 && report.provenance.targets.Count == 3);
        Check(report.provenance.provider.assemblySha256 == Hash);
        var faces = new[] { 1d, 2, 3, 4, 5, 6 };
        var cube = new AeroDragCubeState("Asymmetric", .25, new Vec(), new Vec(1, 2, 3), faces,
            new[] { 6d, 5, 4, 3, 2, 1 }, new[] { 2d, 3, 4, 5, 6, 7 }, new[] { 1d, .9, .8, .7, .6, .5 });
        faces[5] = 99;
        var cubeSource = new[] { Cube(.75), cube };
        var blended = Part(step, cubes: cubeSource); cubeSource[0] = null;
        Check(cube.area[5] == 6 && blended.dragCubes.Count == 2 && blended.dragCubes[1].weight == .25);
        Check(Reject(() => Part(step, cubes: new AeroDragCubeState[AeroDragCubeState.MaximumBlendedCubes + 1])));
        using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
        {
            Check(json.RootElement.GetProperty("disposition").GetString() == "Valid");
            Check(json.RootElement.GetProperty("provenance").GetProperty("targets").GetArrayLength() == 3);
            Check(json.RootElement.GetProperty("samples")[0].GetProperty("publications")[0].GetProperty("context").GetProperty("dragCubes")[0].GetProperty("area")[5].GetDouble() == 6);
        }

        Check(Reject(() => new AeroProviderFingerprint("stock", "1", "assembly", "bad", Guid.NewGuid().ToString())));
        Check(Reject(() => new AeroProviderFingerprint("stock", "1", "assembly", Hash, Guid.Empty.ToString())));
        Check(Reject(() => new AeroCaptureContext(Guid.Empty.ToString(), Guid.NewGuid().ToString(), "frame", 1, 1, 1, 1, 1, 0, 0, 0, .02)));
        Check(Reject(() => new AeroPatchEntry("owner", "method", "around", 0, Hash)));
        var ordering = new[] { "other.owner" };
        var orderedPatch = new AeroPatchEntry("owner", "method", "prefix", 0, Hash, 200, ordering, new[] { "last.owner" }); ordering[0] = "mutated";
        Check(orderedPatch.harmonyPriority == 200 && orderedPatch.beforeOwners[0] == "other.owner" && orderedPatch.afterOwners[0] == "last.owner");
        Check(Reject(() => new AeroPatchEntry("owner", "method", "prefix", 0, Hash, 400, new[] { "duplicate", "duplicate" })));
        Check(Reject(() => new AeroPatchTarget("method", new[] { new AeroPatchEntry("owner", "method", "prefix", 1, Hash) })));
        Check(Reject(() => new AeroPatchProvenance(Provider(), "continuum.capture", new[] { Target("FlightIntegrator.ApplyAeroDrag"), Target("FlightIntegrator.ApplyAeroLift"), Target("Other") })));
        var missingOwnedPatch = new AeroPatchProvenance(Provider(), "continuum.capture", new[] {
            Target("FlightIntegrator.UpdateAerodynamics", new[] { new AeroPatchEntry("foreign.owner", "Foreign.Finalizer", "finalizer", 0, Hash) }),
            Target("FlightIntegrator.ApplyAeroDrag", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.DragPrefix", "prefix", 0, Hash) }),
            Target("FlightIntegrator.ApplyAeroLift", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.LiftPrefix", "prefix", 0, Hash) }) });
        Check(Reject(() => new AeroCaptureReport(missingOwnedPatch, AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample })));
        var wrongPriority = new AeroPatchProvenance(Provider(), "continuum.capture", new[] {
            Target("FlightIntegrator.UpdateAerodynamics", new[] {
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePrefix", "prefix", 0, Hash),
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePostfix", "postfix", 1, Hash),
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdateFinalizer", "finalizer", 2, Hash) }),
            Target("FlightIntegrator.ApplyAeroDrag", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.DragPrefix", "prefix", 0, Hash) }),
            Target("FlightIntegrator.ApplyAeroLift", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.LiftPrefix", "prefix", 0, Hash) }) });
        Check(Reject(() => new AeroCaptureReport(wrongPriority, AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample })));
        var wrongFinalizerPriority = new AeroPatchProvenance(Provider(), "continuum.capture", new[] {
            Target("FlightIntegrator.UpdateAerodynamics", new[] {
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePrefix", "prefix", 0, Hash),
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdatePostfix", "postfix", 1, Hash, AeroPatchEntry.PriorityLast),
                new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.UpdateFinalizer", "finalizer", 2, Hash) }),
            Target("FlightIntegrator.ApplyAeroDrag", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.DragPrefix", "prefix", 0, Hash) }),
            Target("FlightIntegrator.ApplyAeroLift", new[] { new AeroPatchEntry("continuum.capture", "KspContinuum.AeroCapture.LiftPrefix", "prefix", 0, Hash) }) });
        Check(Reject(() => new AeroCaptureReport(wrongFinalizerPriority, AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample })));
        Check(Reject(() => Step(AeroCaptureReport.MaximumPartsPerSample * 2)));
        Check(Reject(() => Part(step, density: double.NaN)));
        Check(Reject(() => new AeroPartContext(step, 1, 1, 2, 1, 1, 1, 1, 1, 0, 1, 1, false,
            new Vec(), new Vec(), new Vec(), new Vec(), new Vec(), .5, Array.Empty<AeroDragCubeState>())));
        Check(Reject(() => new AeroBodyPublication(Part(step), AeroPublicationKind.BodyDrag, AeroApplicationMode.AtCenterOfMass,
            new Vec(1, 0, 0), new Vec(), new Vec())));
        Check(Reject(() => new AeroBodyPublication(Part(step), AeroPublicationKind.BodyLift, AeroApplicationMode.AtWorldPosition,
            new Vec(0, -2, 0), new Vec(2, 2, 3), new Vec(0, 0, 2))));
        Check(Reject(() => new AeroCaptureSample(step, new[] { drag, drag })));
        Check(Reject(() => new AeroCaptureSample(step, new[] { Publication(Step(epoch: 2), AeroPublicationKind.BodyDrag) })));
        Check(Reject(() => new AeroCaptureSample(step, new[] { drag, Publication(Step(1), AeroPublicationKind.BodyLift, density: 1.1) })));

        var full = new AeroBodyPublication[AeroCaptureReport.MaximumPartsPerSample * 2];
        for (int i = 0; i < AeroCaptureReport.MaximumPartsPerSample; i++)
        {
            full[i * 2] = Publication(Step(i * 2), AeroPublicationKind.BodyDrag, i + 1);
            full[i * 2 + 1] = Publication(Step(i * 2 + 1), AeroPublicationKind.BodyLift, i + 1);
        }
        Check(new AeroCaptureSample(step, full).publications.Count == 256);

        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.BoundsExceeded,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>())));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Abstained, AeroCaptureReason.UnsupportedProvider,
            AeroCleanupOutcome.NotRegistered, new[] { sample })));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Abstained, AeroCaptureReason.PatchGraphMismatch,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>())));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.Failed, new[] { sample })));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.NotRegistered, new[] { sample })));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.OwnerDestroyed, new[] { sample })));
        var invalid = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Invalid, AeroCaptureReason.HookFailure,
            AeroCleanupOutcome.Failed, Array.Empty<AeroCaptureSample>());
        Check(invalid.cleanup == AeroCleanupOutcome.Failed);
        var tooMany = new AeroCaptureSample[AeroCaptureReport.MaximumSamples + 1];
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, tooMany)));

        var runtime = new FakeRuntime();
        using (var run = new AeroCaptureRun(runtime))
        {
            run.Start(); Check(run.TryPublish(sample));
        }
        Check(runtime.installs == 1 && runtime.inspections == 1 && runtime.removals == 1);
        Check(runtime.Cleanup == AeroCleanupOutcome.RemovedOwnedPatches);

        runtime = new FakeRuntime { Inspected = missingOwnedPatch };
        using (var run = new AeroCaptureRun(runtime)) { run.Start(); Check(run.TryPublish(sample)); }
        Check(runtime.removals == 1);

        runtime = new FakeRuntime { Provider = new AeroProviderFingerprint("stock-flight-integrator", "1.12.5", "Assembly-CSharp", Hash,
            "10657063-2fc3-43a7-84fa-d39e75e877bf") };
        using (var run = new AeroCaptureRun(runtime)) { run.Start(); }
        Check(runtime.installs == 0 && runtime.removals == 0);

        runtime = new FakeRuntime { ThrowOnInstall = true };
        AeroCaptureReport partialInstallReport;
        using (var run = new AeroCaptureRun(runtime)) { run.Start(); partialInstallReport = run.Report; }
        Check(runtime.installs == 1 && runtime.inspections == 1 && runtime.removals == 1);
        Check(partialInstallReport.disposition == AeroCaptureDisposition.Invalid &&
            partialInstallReport.reason == AeroCaptureReason.HookFailure &&
            partialInstallReport.cleanup == AeroCleanupOutcome.RemovedOwnedPatches);
        Console.WriteLine("Aero capture contract: " + checks + " assertions passed.");
    }
}
