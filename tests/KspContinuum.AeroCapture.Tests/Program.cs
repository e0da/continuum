using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition) { checks++; if (!condition) throw new Exception("Aero capture assertion " + checks); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static readonly string Hash = new string('a', 64);
    static AeroProviderFingerprint Provider() => new AeroProviderFingerprint("stock-flight-integrator", "1.12.5", "Assembly-CSharp", Hash, "10657063-2fc3-43a7-84fa-d39e75e877bf");
    static AeroPatchProvenance Provenance() => new AeroPatchProvenance(Provider(), "FlightIntegrator.ApplyAeroDrag",
        new[] { new AeroPatchEntry("continuum.capture", "Capture.Postfix", "postfix", 0, Hash) });
    static AeroCaptureContext Step(int ordinal = 0) => new AeroCaptureContext("00000000-0000-0000-0000-000000000001",
        "00000000-0000-0000-0000-000000000002", "frame-1", 1, 1, 1, 7, 1, ordinal, 100, 2, .02);
    static AeroPartContext Part(AeroCaptureContext step) => new AeroPartContext(step, 1, 4, 5, 100, 1.2, .8,
        new Vec(1, 2, 3), new Vec(20, 0, 0), new Vec(-20, 0, 0));
    static AeroBodyPublication Drag(AeroCaptureContext step) => new AeroBodyPublication(Part(step), AeroPublicationKind.BodyDrag,
        AeroApplicationMode.AtWorldPosition, new Vec(0, -2, 0), new Vec(2, 2, 3), new Vec(0, 0, -2));

    static void Main()
    {
        var step = Step(); var publication = Drag(step); var source = new[] { publication };
        var sample = new AeroCaptureSample(step, source); source[0] = null;
        Check(sample.publications[0] == publication);
        bool readOnly = false; try { ((IList<AeroBodyPublication>)sample.publications)[0] = null; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly);
        var report = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, new[] { sample });
        Check(report.schema == "ksp-continuum-aero-capture/v1" && report.samples.Count == 1);
        Check(report.provenance.provider.assemblySha256 == Hash);

        Check(Reject(() => new AeroProviderFingerprint("stock", "1", "assembly", "bad", Guid.NewGuid().ToString())));
        Check(Reject(() => new AeroPatchEntry("owner", "method", "around", 0, Hash)));
        Check(Reject(() => new AeroPatchProvenance(Provider(), "method", new[] {
            new AeroPatchEntry("owner", "method", "prefix", 0, Hash), new AeroPatchEntry("owner", "method", "prefix", 1, Hash) })));
        Check(Reject(() => new AeroPatchProvenance(Provider(), "method", new[] {
            new AeroPatchEntry("owner", "method", "prefix", 1, Hash) })));
        Check(Reject(() => Step(AeroCaptureReport.MaximumPartsPerSample)));
        Check(Reject(() => new AeroPartContext(step, 1, 1, 2, 1, double.NaN, 0, new Vec(), new Vec(), new Vec())));
        Check(Reject(() => new AeroBodyPublication(Part(step), AeroPublicationKind.BodyDrag, AeroApplicationMode.AtCenterOfMass,
            new Vec(1, 0, 0), new Vec(), new Vec())));
        Check(Reject(() => new AeroBodyPublication(Part(step), AeroPublicationKind.BodyLift, AeroApplicationMode.AtWorldPosition,
            new Vec(0, -2, 0), new Vec(2, 2, 3), new Vec(0, 0, 2))));
        Check(Reject(() => new AeroCaptureSample(step, new[] { publication, publication })));
        Check(Reject(() => new AeroCaptureSample(step, new[] { Drag(new AeroCaptureContext("00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002", "frame-1", 2, 1, 1, 7, 1, 0, 100, 2, .02)) })));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.BoundsExceeded,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>())));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Abstained, AeroCaptureReason.UnsupportedProvider,
            AeroCleanupOutcome.NotRegistered, new[] { sample })));
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Abstained, AeroCaptureReason.PatchGraphMismatch,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>())));
        var abstained = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Abstained, AeroCaptureReason.UnsupportedProvider,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>());
        Check(abstained.reason == AeroCaptureReason.UnsupportedProvider && abstained.samples.Count == 0);
        var invalid = new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Invalid, AeroCaptureReason.PatchGraphMismatch,
            AeroCleanupOutcome.RemovedOwnedPatches, Array.Empty<AeroCaptureSample>());
        Check(invalid.reason == AeroCaptureReason.PatchGraphMismatch);

        var tooMany = new AeroCaptureSample[AeroCaptureReport.MaximumSamples + 1];
        Check(Reject(() => new AeroCaptureReport(Provenance(), AeroCaptureDisposition.Valid, AeroCaptureReason.None,
            AeroCleanupOutcome.RemovedOwnedPatches, tooMany)));
        Console.WriteLine("Aero capture contract: " + checks + " assertions passed.");
    }
}
