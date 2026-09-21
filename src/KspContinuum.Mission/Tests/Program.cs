using System;
using KspContinuum.Mission;

static class Program
{
    static int assertions;
    static void Check(bool value) { assertions++; if (!value) throw new Exception("Mission acceptance assertion " + assertions); }
    static void Main()
    {
        CheckpointTests.Run(Check);
        Check(Math.Abs(SurveyPolicy.Distance(60000, 0, 179.5, 0, -179.5) - Math.PI * 60000 / 180) < 1e-6);
        Check(Math.Abs(SurveyPolicy.FutureLongitude(-10, 100, 400) - 80) < 1e-9);
        Check(SurveyPolicy.FindWindow(0, 300, 120, t => t >= 120 && t <= 300) == 120);
        Check(double.IsNaN(SurveyPolicy.FindWindow(0, 300, 120, t => t < 90)));
        Check(SurveyPolicy.ArrivalForecast(new[] { 20.0, 22.0, 24.0 }, 0.5) == 21);
        Check(SurveyPolicy.ArrivalForecast(new[] { 20.0, 22.0, 24.0 }, 2) == 24);
        Check(double.IsNaN(SurveyPolicy.ArrivalForecast(new[] { 20.0, 22.0 }, 2)));
        Check(double.IsNaN(SurveyPolicy.ArrivalForecast(new[] { 20.0, 22.0 }, double.NaN)));
        Check(SurveyPolicy.Accept(100, 20, 10, 0.009, false));
        Check(!SurveyPolicy.Accept(100.01, 20, 10, 0.009, false));
        Check(!SurveyPolicy.Accept(100, 19.99, 10, 0.009, false));
        Check(!SurveyPolicy.Accept(100, 20, 10.01, 0.009, false));
        Check(!SurveyPolicy.Accept(100, 20, 10, 0.01, false));
        Check(!SurveyPolicy.Accept(100, 20, 10, 0.009, true));
        Check(!SurveyPolicy.Accept(double.NaN, 20, 10, 0, false));
        Check(SurveyPolicy.ValidAttemptId("CSP-0002-A001"));
        Check(!SurveyPolicy.ValidAttemptId("../CSP-0002-A001"));
        Check(!SurveyPolicy.ValidAttemptId("CSP-0001-A001"));
        Check(!SurveyPolicy.ValidAttemptId(null));
        var flat = SurveyGeometry.Sample(60000, (lat, lon) => 0);
        Check(flat.Samples.Count == 529 && flat.MaximumSlope < 0.02);
        var slope = SurveyGeometry.Sample(60000, (lat, lon) => Math.Tan(3 * SurveyPolicy.Radians) * 60000 * (lat - SurveyPolicy.Latitude) * SurveyPolicy.Radians);
        Check(slope.MaximumSlope > 2.9 && slope.MaximumSlope < 3.1);
        bool nonfiniteRejected = false;
        try { SurveyGeometry.Sample(60000, (lat, lon) => double.NaN); } catch (ArgumentException) { nonfiniteRejected = true; }
        Check(nonfiniteRejected);
        Check(Math.Abs(SurveyGeometry.Elevation(new SurveyVector(0, 1, 0), new SurveyVector(1, 0, 0))) < 1e-9);
        Check(SurveyGeometry.Occludes(new SurveyVector(0, 5, 0), 1, new SurveyVector(0, 10, 0)));
        Check(!SurveyGeometry.Occludes(new SurveyVector(0, -5, 0), 1, new SurveyVector(0, 10, 0)));
        Check(!SurveyGeometry.Occludes(new SurveyVector(2, 5, 0), 1, new SurveyVector(0, 10, 0)));
        var cleanup = new MissionCleanup();
        int controllerReleased = 0, recorderReleased = 0, requestReleased = 0;
        Check(cleanup.Release("foreign-controller") == null);
        Check(cleanup.ReleaseAll().Count == 0);
        cleanup.Track("controller", () => controllerReleased++);
        cleanup.Track("recorder", () => recorderReleased++);
        cleanup.Track("staging-request", () => requestReleased++);
        Check(cleanup.Release("controller") == null);
        Check(controllerReleased == 1);
        Check(cleanup.Release("controller") == null);
        Check(controllerReleased == 1);
        cleanup.Track("throwing-controller", () => { throw new InvalidOperationException("controller cleanup failed"); });
        var failures = cleanup.ReleaseAll();
        Check(failures.Count == 1 && failures[0].Message == "controller cleanup failed");
        Check(recorderReleased == 1 && requestReleased == 1 && controllerReleased == 1);
        Check(cleanup.ReleaseAll().Count == 0);
        Check(recorderReleased == 1 && requestReleased == 1);
        Check(MissionCompatibility.IsSupported("2.15.0.0", "2.15.3.0"));
        Check(!MissionCompatibility.IsSupported("2.15.0.0", "2.15.2.0"));
        Check(!MissionCompatibility.IsSupported("2.15.3.0", "2.15.3.0"));
        Check(!MissionCompatibility.IsSupported("2.15.0.0", null));
        Check(!MissionCompatibility.ShouldExit(false, false));
        Check(MissionCompatibility.ShouldExit(true, false));
        Check(MissionCompatibility.ShouldExit(false, true));
        Check(MissionCompatibility.ShouldExit(true, true));
        var gate = new LandingAcceptance();
        Check(!gate.Observe(100, true));
        Check(!gate.Observe(129.99, true));
        Check(gate.Observe(130, true));
        Check(!gate.Observe(131, false));
        Check(!gate.Observe(132, true));
        Check(!gate.Observe(161.99, true));
        Check(gate.Observe(162, true));
        Check(!gate.Observe(120, true));
        Check(!gate.Observe(double.NaN, true));
        Check(!gate.Observe(1000, true));
        Check(!LandingAcceptance.Qualifies("Kerbin", true, true, 0, 0, true));
        Check(!LandingAcceptance.Qualifies("Minmus", false, true, 0, 0, true));
        Check(!LandingAcceptance.Qualifies("Minmus", true, false, 0, 0, true));
        Check(!LandingAcceptance.Qualifies("Minmus", true, true, 0.01, 0, true));
        Check(!LandingAcceptance.Qualifies("Minmus", true, true, 0, 0.2, true));
        Check(!LandingAcceptance.Qualifies("Minmus", true, true, 0, 0, false));
        Check(!LandingAcceptance.Qualifies("Minmus", true, true, 0, double.NaN, true));
        Check(LandingAcceptance.Qualifies("Minmus", true, true, 0, 0.19, true));
        Console.WriteLine("Mission acceptance: " + assertions + " assertions passed.");
    }
}
