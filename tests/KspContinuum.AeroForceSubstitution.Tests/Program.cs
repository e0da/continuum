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
        Console.WriteLine("PASS " + checks + " aero force substitution assertions");
    }
}
