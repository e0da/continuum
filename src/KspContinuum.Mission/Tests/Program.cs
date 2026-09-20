using System;
using KspContinuum.Mission;

static class Program
{
    static int assertions;
    static void Check(bool value) { assertions++; if (!value) throw new Exception("Mission acceptance assertion " + assertions); }
    static void Main()
    {
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
