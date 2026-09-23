using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value) { checks++; if (!value) throw new Exception("check " + checks + " failed"); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }

    static void Main()
    {
        Check(Reject(() => new AeroForceSubstitution(null, 1)));
        Check(Reject(() => new AeroForceSubstitution(new AeroForceSubstitutionReport(), 0)));
        var report = new AeroForceSubstitutionReport();
        var state = new AeroForceSubstitution(report, 2);
        Check(!state.TryBeginPublication(true, true));
        state.Start();
        Check(state.TryBeginPublication(true, true)); state.Published();
        Check(state.IsActive && report.substitutedPublications == 1 && report.stockFallbacks == 0);
        Check(state.TryBeginPublication(true, true)); state.Published();
        Check(!state.IsActive && report.status == "complete" && report.reason == "bounded-publication-limit-reached");
        Check(report.attemptedPublications == 2 && report.substitutedPublications == 2);

        report = new AeroForceSubstitutionReport(); state = new AeroForceSubstitution(report, 4); state.Start();
        Check(!state.TryBeginPublication(false, true));
        Check(report.status == "abstained" && report.stockFallbacks == 1 && report.substitutedPublications == 0);

        report = new AeroForceSubstitutionReport(); state = new AeroForceSubstitution(report, 4); state.Start();
        Check(!state.TryBeginPublication(true, false));
        Check(report.reason == "nonfinite-stock-publication" && report.stockFallbacks == 1);

        report = new AeroForceSubstitutionReport(); state = new AeroForceSubstitution(report, 4); state.Start();
        Check(state.TryBeginPublication(true, true)); state.PublicationFailed("InvalidOperationException");
        Check(report.status == "abstained" && report.reason == "publication-failed:InvalidOperationException" && report.stockFallbacks == 1);
        Check(ReportJson.Encode(report).Contains("\"unityIntegrationRemainedAuthoritative\":true"));
        var setDrag = new AeroSetDragSubstitutionReport { status = "complete", matchedCompleteOutputs = 288,
            shadowWarmupComparisons = 32, shadowMeasuredComparisons = 256, suppressedOriginalCalls = 256,
            maximumRelativeError = 1e-6, stopwatchFrequency = 10000000, patchGraphInspections = 4,
            patchGraphStopwatchTicks = 120, measuredPatchGraphInspections = 3,
            measuredPatchGraphStopwatchTicks = 90, authorityAdmissionAttested = true,
            authorityExitAttested = true, admissionStrategyStopwatchTicks = 2400 };
        Check(setDrag.RecordCleanup(true, true) && setDrag.cleanupStatus == "removed-owned-patches" &&
            setDrag.status == "complete");
        string encoded = ReportJson.Encode(setDrag);
        Check(encoded.Contains("\"schema\":\"ksp-continuum-set-drag-substitution/v2\"") &&
            encoded.Contains("\"suppressedOriginalCalls\":256") &&
            encoded.Contains("\"shadowMeasuredComparisons\":256") &&
            encoded.Contains("\"patchGraphStopwatchTicks\":120") &&
            encoded.Contains("\"measuredPatchGraphStopwatchTicks\":90") &&
            encoded.Contains("\"admissionStrategyStopwatchTicks\":2400") &&
            encoded.Contains("\"authorityAdmissionAttested\":true") &&
            encoded.Contains("\"authorityExitAttested\":true") &&
            encoded.Contains("\"cleanupStatus\":\"removed-owned-patches\"") &&
            encoded.Contains("\"stockForceApplicationRemainedAuthoritative\":true"));
        var cleanupFailure = new AeroSetDragSubstitutionReport { status = "complete",
            reason = "bounded-substitution-limit-reached" };
        Check(!cleanupFailure.RecordCleanup(true, false) && cleanupFailure.cleanupStatus == "failed" &&
            cleanupFailure.status == "abstained" && cleanupFailure.reason == "cleanup-failed");
        Check(cleanupFailure.RecordCleanup(false, false) == false && cleanupFailure.cleanupStatus == "failed");
        Console.WriteLine("PASS " + checks + " aero force substitution assertions");
    }
}
