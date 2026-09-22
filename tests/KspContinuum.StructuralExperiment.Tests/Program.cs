using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message) { checks++; try { action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }

    static StructuralExperimentReport Fixture()
    {
        const double step = .02;
        var trace = new StructuralTraceSample[StructuralExperimentReport.RequiredSamples];
        var bodies = new StructuralBodySample[trace.Length];
        for (int i = 0; i < trace.Length; i++)
        {
            double displacement = Math.Exp(-i * .01) * Math.Sin(i * .2);
            double velocity = i == 0 ? .2 / step : (displacement - trace[i - 1].relativeDisplacement) / step;
            trace[i] = new StructuralTraceSample { physicsEpoch = i + 10, relativeDisplacement = displacement, relativeVelocity = velocity };
            bodies[i] = new StructuralBodySample {
                physicsEpoch = i + 10, topologyGeneration = 4, frameGeneration = 7, originEventCount = 2,
                unityFrame = 100 + i, fixedTimeSeconds = 20 + i * step,
                bodyAWorldCenterOfMass = new[] { 0.0, 0, 0 }, bodyARotation = new[] { 0.0, 0, 0, 1 },
                bodyAVelocity = new[] { 0.0, 0, 0 }, bodyAAngularVelocity = new[] { 0.0, 0, 0 },
                bodyBWorldCenterOfMass = new[] { 1.0 + displacement, 0, 0 }, bodyBRotation = new[] { 0.0, 0, 0, 1 },
                bodyBVelocity = new[] { velocity, 0, 0 }, bodyBAngularVelocity = new[] { 0.0, 0, 0 },
            };
        }
        return new StructuralExperimentReport {
            evidence = "portable-helper-fixture", status = "complete", receiptValidity = "valid",
            runEligibility = "eligible", experimentQualified = "not-evaluated", cleanupStatus = "complete",
            contactObservationStatus = "observed-none", vesselId = "fixture-vessel", bodyAInstanceId = 1,
            topology = "fixture-config-sha256",
            injectionCallback = "fixture-pre-solver", observationCallback = "fixture-post-solver",
            lifecycleQualificationId = "fixture-lifecycle-sha256",
            bodyBInstanceId = 2, jointInstanceId = 3, retainedSamples = trace.Length, stepSeconds = step,
            impulseMagnitude = .01, worldAxis = new[] { 1.0, 0, 0 },
            baselineBodyAWorldCenterOfMass = new[] { 0.0, 0, 0 },
            baselineBodyBWorldCenterOfMass = new[] { 1.0, 0, 0 },
            referenceRelativeCenterOfMass = new[] { 1.0, 0, 0 }, requestedBodyAImpulse = new[] { .01, 0, 0 },
            requestedBodyBImpulse = new[] { -.01, 0, 0 }, requestedNetImpulse = new[] { 0.0, 0, 0 },
            trace = new StructuralTrace { evidence = "portable-helper-fixture", topology = "fixture-config-sha256",
                stepSeconds = step, samples = trace }, bodySamples = bodies,
        };
    }

    static int Main()
    {
        var valid = Fixture(); StructuralExperiment.Validate(valid);
        Check(ReportJson.Encode(valid).Contains("\"receiptValidity\":\"valid\""), "receipt did not serialize");
        Check(StructuralResponse.FitAndPredict(valid.trace, 40).heldOutSamples == 80, "40/80 analysis split changed");
        var changed = Fixture(); changed.bodySamples[8].originEventCount++; Reject(() => StructuralExperiment.Validate(changed), "origin change accepted");
        changed = Fixture(); changed.bodySamples[8].fixedTimeSeconds += .001; Reject(() => StructuralExperiment.Validate(changed), "time drift accepted");
        changed = Fixture(); changed.trace.samples[8].physicsEpoch++; Reject(() => StructuralExperiment.Validate(changed), "epoch gap accepted");
        changed = Fixture(); changed.trace.samples[8].relativeDisplacement += .01; Reject(() => StructuralExperiment.Validate(changed), "derived tampering accepted");
        changed = Fixture(); changed.requestedBodyBImpulse[0] = -.02; Reject(() => StructuralExperiment.Validate(changed), "unbalanced request accepted");
        changed = Fixture(); changed.worldAxis[0] = 2; Reject(() => StructuralExperiment.Validate(changed), "unnormalized axis accepted");
        changed = Fixture(); changed.cleanupStatus = "failed"; Reject(() => StructuralExperiment.Validate(changed), "cleanup failure accepted");
        changed = Fixture(); changed.runEligibility = "eligible"; changed.contactObservationStatus = "unavailable";
        Reject(() => StructuralExperiment.Validate(changed), "missing contact evidence accepted as eligible");
        changed = Fixture(); changed.experimentQualified = "true"; Reject(() => StructuralExperiment.Validate(changed), "capture claimed qualification");
        changed = Fixture(); changed.trace.evidence = "native-adapter-observation";
        Reject(() => StructuralExperiment.Validate(changed), "nested provenance mismatch accepted");
        changed = Fixture(); changed.referenceRelativeCenterOfMass[0] += .1;
        for (int i = 0; i < changed.trace.samples.Length; i++) changed.trace.samples[i].relativeDisplacement -= .1;
        Reject(() => StructuralExperiment.Validate(changed), "shifted displacement reference accepted");
        var invalid = new StructuralExperimentReport { evidence = "portable-helper-fixture", status = "invalid",
            reason = "callback order unqualified", receiptValidity = "valid-invalidated-run", runEligibility = "ineligible",
            cleanupStatus = "complete" };
        StructuralExperiment.Validate(invalid); Check(ReportJson.Encode(invalid).Contains("callback order unqualified"), "invalid receipt lost reason");
        invalid.experimentQualified = "true"; Reject(() => StructuralExperiment.Validate(invalid), "invalid receipt claimed qualification");
        invalid.experimentQualified = "not-evaluated"; invalid.stepSeconds = double.NaN;
        Reject(() => StructuralExperiment.Validate(invalid), "invalid receipt carried nonfinite scalar");
        Console.WriteLine("structural experiment checks: " + checks); return 0;
    }
}
