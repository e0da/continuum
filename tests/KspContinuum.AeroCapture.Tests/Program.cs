using System;
using System.Collections.Generic;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition) { checks++; if (!condition) throw new Exception("Aero capture assertion " + checks); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static readonly string Hash = new string('a', 64);
    static AeroProviderFingerprint Provider() => new AeroProviderFingerprint("stock-flight-integrator", "1.12.5", "Assembly-CSharp", Hash, "10657063-2fc3-43a7-84fa-d39e75e877bf");
    static AeroPatchTarget Target(string method, AeroPatchEntry[] patches = null) => new AeroPatchTarget(method, patches ?? Array.Empty<AeroPatchEntry>());
    static AeroPatchProvenance Provenance() => new AeroPatchProvenance(Provider(), new[] {
        Target("FlightIntegrator.UpdateAerodynamics", new[] { new AeroPatchEntry("continuum.capture", "Capture.Finalizer", "finalizer", 0, Hash) }),
        Target("FlightIntegrator.ApplyAeroDrag", new[] { new AeroPatchEntry("continuum.capture", "Capture.DragPostfix", "postfix", 0, Hash) }),
        Target("FlightIntegrator.ApplyAeroLift", new[] { new AeroPatchEntry("continuum.capture", "Capture.LiftPostfix", "postfix", 0, Hash) }) });
    static AeroCaptureContext Step(int ordinal = 0, long epoch = 1) => new AeroCaptureContext("{00000000-0000-0000-0000-000000000001}",
        "00000000-0000-0000-0000-000000000002", "frame-1", epoch, 1, 1, 7, 1, ordinal, 100, 2, .02);
    static AeroPartContext Part(AeroCaptureContext step, long flightId = 1, double density = 1.2) => new AeroPartContext(step, flightId,
        checked((int)flightId + 3), checked((int)flightId + 4), 100, density, 101325, 288.15, 340, .8, 2, 1.5, false,
        new Vec(1, 2, 3), new Vec(20, 0, 0), new Vec(-20, 0, 0), new Vec(0, .1, 0), new Vec(), 1,
        new Vec(1, 2, 3), new Vec(.5, .6, .7));
    static AeroBodyPublication Publication(AeroCaptureContext step, AeroPublicationKind kind, long flightId = 1, double density = 1.2) =>
        new AeroBodyPublication(Part(step, flightId, density), kind, AeroApplicationMode.AtWorldPosition,
            new Vec(0, -2, 0), new Vec(2, 2, 3), new Vec(0, 0, -2));

    static void Main()
    {
        var step = Step(); var drag = Publication(step, AeroPublicationKind.BodyDrag); var source = new[] { drag };
        var sample = new AeroCaptureSample(step, source); source[0] = null;
        Check(sample.publications[0] == drag && step.sessionId == "00000000-0000-0000-0000-000000000001");
        bool readOnly = false; try { ((IList<AeroBodyPublication>)sample.publications)[0] = null; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly);
        var report = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample });
        Check(report.schema == "ksp-continuum-aero-capture/v1" && report.samples.Count == 1 && report.provenance.targets.Count == 3);
        Check(report.provenance.provider.assemblySha256 == Hash);
        using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
        {
            Check(json.RootElement.GetProperty("disposition").GetString() == "Valid");
            Check(json.RootElement.GetProperty("provenance").GetProperty("targets").GetArrayLength() == 3);
            Check(json.RootElement.GetProperty("samples")[0].GetProperty("publications")[0].GetProperty("context").GetProperty("dragCubeArea").GetProperty("X").GetDouble() == 1);
        }

        Check(Reject(() => new AeroProviderFingerprint("stock", "1", "assembly", "bad", Guid.NewGuid().ToString())));
        Check(Reject(() => new AeroProviderFingerprint("stock", "1", "assembly", Hash, Guid.Empty.ToString())));
        Check(Reject(() => new AeroCaptureContext(Guid.Empty.ToString(), Guid.NewGuid().ToString(), "frame", 1, 1, 1, 1, 1, 0, 0, 0, .02)));
        Check(Reject(() => new AeroPatchEntry("owner", "method", "around", 0, Hash)));
        Check(Reject(() => new AeroPatchTarget("method", new[] { new AeroPatchEntry("owner", "method", "prefix", 1, Hash) })));
        Check(Reject(() => new AeroPatchProvenance(Provider(), new[] { Target("FlightIntegrator.ApplyAeroDrag"), Target("FlightIntegrator.ApplyAeroLift"), Target("Other") })));
        Check(Reject(() => Step(AeroCaptureReport.MaximumPartsPerSample * 2)));
        Check(Reject(() => Part(step, density: double.NaN)));
        Check(Reject(() => new AeroPartContext(step, 1, 1, 2, 1, 1, 1, 1, 1, 0, 1, 1, false,
            new Vec(), new Vec(), new Vec(), new Vec(), new Vec(), .5, new Vec(), new Vec())));
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
        var invalid = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Invalid, AeroCaptureReason.HookFailure,
            AeroCleanupOutcome.Failed, Array.Empty<AeroCaptureSample>());
        Check(invalid.cleanup == AeroCleanupOutcome.Failed);
        var tooMany = new AeroCaptureSample[AeroCaptureReport.MaximumSamples + 1];
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, tooMany)));
        Console.WriteLine("Aero capture contract: " + checks + " assertions passed.");
    }
}
