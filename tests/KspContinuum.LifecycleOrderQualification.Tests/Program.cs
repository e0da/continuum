using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) { checks++; try { action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }

    static LifecycleOrderQualificationReport Fixture()
    {
        var report = new LifecycleOrderQualificationReport {
            evidence = "portable-helper-fixture", status = "qualified", integrityStatus = "verified-at-every-boundary",
            cleanupStatus = "removed-owned-hooks-probe-destroy-requested", installedBeforeCallbacks = 1, installedAfterCallbacks = 1,
            retainedTrials = LifecycleOrderQualificationReport.RequiredTrials, unity = "fixture-unity", ksp = "fixture-ksp",
            plugin = "fixture-plugin", trials = new LifecycleOrderTrial[LifecycleOrderQualificationReport.RequiredTrials],
        };
        for (int i = 0; i < report.trials.Length; i++)
        {
            double[] velocity = new double[3]; velocity[i] = i % 2 == 0 ? 2 : -2;
            double[] after = new double[3]; after[i] = velocity[i] * .02;
            report.trials[i] = new LifecycleOrderTrial { trial = i + 1, unityFrameBefore = 20,
                unityFrameAfter = 20, fixedTimeBefore = 4 + i * .02, fixedTimeAfter = 4 + i * .02,
                fixedDeltaSeconds = .02, initialPosition = new[] { 0.0, 0, 0 }, requestedVelocity = velocity,
                positionBeforeTarget = new[] { 0.0, 0, 0 }, positionAfterTarget = after,
                velocityAfterTarget = (double[])velocity.Clone() };
        }
        report.qualificationId = LifecycleOrderQualification.ComputeId(report);
        return report;
    }

    static int Main()
    {
        var valid = Fixture(); LifecycleOrderQualification.Validate(valid);
        Check(ReportJson.Encode(valid).Contains(valid.qualificationId), "qualification did not serialize");
        var changed = Fixture(); changed.trials[0].positionBeforeTarget[0] = .01;
        Reject(() => LifecycleOrderQualification.Validate(changed), "pre-target movement accepted");
        changed = Fixture(); changed.trials[1].positionAfterTarget[1] = 0;
        Reject(() => LifecycleOrderQualification.Validate(changed), "missing native integration accepted");
        changed = Fixture(); changed.trials[1].velocityAfterTarget[1] = 0;
        Reject(() => LifecycleOrderQualification.Validate(changed), "velocity mutation accepted");
        changed = Fixture(); changed.trials[0].unityFrameAfter++;
        Reject(() => LifecycleOrderQualification.Validate(changed), "cross-frame callbacks accepted");
        changed = Fixture(); changed.installedAfterCallbacks = 2;
        Reject(() => LifecycleOrderQualification.Validate(changed), "duplicate callback accepted");
        changed = Fixture(); changed.cleanupStatus = "cleanup-error";
        Reject(() => LifecycleOrderQualification.Validate(changed), "cleanup failure accepted");
        changed = Fixture(); changed.qualificationId = "sha256:forged";
        Reject(() => LifecycleOrderQualification.Validate(changed), "forged identity accepted");
        changed = Fixture(); changed.nativeTarget = "FriendlyName.Physics";
        Reject(() => LifecycleOrderQualification.Validate(changed), "named target accepted without native identity");
        var invalid = new LifecycleOrderQualificationReport { status = "invalid", reason = "loop replaced",
            integrityStatus = "invalidated", cleanupStatus = "complete" };
        LifecycleOrderQualification.Validate(invalid);
        invalid.cleanupStatus = "cleanup-error"; LifecycleOrderQualification.Validate(invalid);
        invalid.trials = new[] { Fixture().trials[0] };
        Reject(() => LifecycleOrderQualification.Validate(invalid), "invalid receipt retained evidence");
        Console.WriteLine("lifecycle order qualification checks: " + checks); return 0;
    }
}
