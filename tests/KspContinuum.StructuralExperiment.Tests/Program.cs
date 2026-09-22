using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message) { checks++; try { action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }

    static StructuralLimit Limit(double value = 1) { return new StructuralLimit { limit = value, bounciness = .1, contactDistance = .01 }; }
    static StructuralSpring Spring() { return new StructuralSpring { spring = 10, damper = 2 }; }
    static StructuralDrive Drive() { return new StructuralDrive { positionSpring = 11, positionDamper = 3,
        maximumForce = 100, maximumForceStatus = "finite" }; }
    static StructuralConfigurableJoint Configurable() { return new StructuralConfigurableJoint {
        autoConfigureConnectedAnchor = false, configuredInWorldSpace = false, swapBodies = false,
        xMotion = "Limited", yMotion = "Locked", zMotion = "Free", angularXMotion = "Limited",
        angularYMotion = "Locked", angularZMotion = "Free", rotationDriveMode = "Slerp",
        projectionMode = "PositionAndRotation", projectionDistance = .1, projectionAngle = 2,
        targetPosition = new[] { .1, .2, .3 }, targetVelocity = new[] { .4, .5, .6 },
        targetRotation = new[] { 0.0, 0, 0, 1 }, targetAngularVelocity = new[] { .7, .8, .9 },
        linearLimit = Limit(), lowAngularXLimit = Limit(-20), highAngularXLimit = Limit(30),
        angularYLimit = Limit(40), angularZLimit = Limit(50), linearLimitSpring = Spring(),
        angularXLimitSpring = Spring(), angularYZLimitSpring = Spring(), xDrive = Drive(), yDrive = Drive(),
        zDrive = Drive(), angularXDrive = Drive(), angularYZDrive = Drive(), slerpDrive = Drive() }; }
    static StructuralLink Joint() { return new StructuralLink { nativeInstanceId = 3, bodyId = 0, connectedBodyId = 1,
        jointType = "UnityEngine.ConfigurableJoint", anchor = new[] { 0.0, 0, 0 }, connectedAnchor = new[] { 0.0, 0, 0 },
        axis = new[] { 1.0, 0, 0 }, secondaryAxis = new[] { 0.0, 1, 0 }, breakForceStatus = "unbreakable",
        breakTorqueStatus = "unbreakable", massScale = 1, connectedMassScale = 1, configurable = Configurable() }; }

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
                physicsCycle = i + 1, bodyAInstanceId = 1, bodyBInstanceId = 2,
                unityFrame = 100 + i, fixedTimeSeconds = 20 + i * step,
                bodyAWorldCenterOfMass = new[] { 0.0, 0, 0 }, bodyARotation = new[] { 0.0, 0, 0, 1 },
                bodyAVelocity = new[] { 0.0, 0, 0 }, bodyAAngularVelocity = new[] { 0.0, 0, 0 },
                bodyBWorldCenterOfMass = new[] { 1.0 + displacement, 0, 0 }, bodyBRotation = new[] { 0.0, 0, 0, 1 },
                bodyBVelocity = new[] { velocity, 0, 0 }, bodyBAngularVelocity = new[] { 0.0, 0, 0 },
            };
        }
        var lifecycle = new LifecycleOrderQualificationReport {
            evidence = "portable-helper-fixture", status = "qualified", integrityStatus = "verified-at-every-boundary",
            cleanupStatus = "removed-owned-hooks-probe-destroy-requested", installedBeforeCallbacks = 1, installedAfterCallbacks = 1,
            retainedTrials = 3, unity = "fixture-unity", ksp = "fixture-ksp", plugin = "fixture-plugin",
            trials = new LifecycleOrderTrial[3]
        };
        for (int i = 0; i < lifecycle.trials.Length; i++) lifecycle.trials[i] = new LifecycleOrderTrial {
            trial = i + 1, unityFrameBefore = 9, unityFrameAfter = 9, fixedTimeBefore = i, fixedTimeAfter = i,
            fixedDeltaSeconds = step, initialPosition = new[] { 0.0, 0, 0 }, requestedVelocity = new[] { 1.0, 0, 0 },
            positionBeforeTarget = new[] { 0.0, 0, 0 }, positionAfterTarget = new[] { step, 0, 0 },
            velocityAfterTarget = new[] { 1.0, 0, 0 }
        };
        lifecycle.qualificationId = LifecycleOrderQualification.ComputeId(lifecycle);
        var baseline = new StructuralBodySample {
            physicsEpoch = 10, physicsCycle = 1, topologyGeneration = 4, frameGeneration = 7,
            originEventCount = 2, unityFrame = 100, fixedTimeSeconds = 20,
            bodyAInstanceId = 1, bodyBInstanceId = 2,
            bodyAWorldCenterOfMass = new[] { 0.0, 0, 0 }, bodyARotation = new[] { 0.0, 0, 0, 1 },
            bodyAVelocity = new[] { 0.0, 0, 0 }, bodyAAngularVelocity = new[] { 0.0, 0, 0 },
            bodyBWorldCenterOfMass = new[] { 1.0, 0, 0 }, bodyBRotation = new[] { 0.0, 0, 0, 1 },
            bodyBVelocity = new[] { 0.0, 0, 0 }, bodyBAngularVelocity = new[] { 0.0, 0, 0 },
        };
        var report = new StructuralExperimentReport {
            evidence = "portable-helper-fixture", status = "complete", receiptValidity = "valid",
            runEligibility = "eligible", experimentQualified = "not-evaluated", cleanupStatus = "complete",
            contactObservationStatus = "observed-none", vesselId = "fixture-vessel", bodyAInstanceId = 1,
            unity = lifecycle.unity, ksp = lifecycle.ksp, plugin = lifecycle.plugin,
            startedUtc = "2026-09-21T12:00:00Z", sessionId = "fixture-session", runId = "fixture-run",
            injectionCallback = lifecycle.injectionCallback, observationCallback = lifecycle.observationCallback,
            lifecycleQualificationId = lifecycle.qualificationId, lifecycleQualification = lifecycle,
            bodyBInstanceId = 2, jointInstanceId = 3, retainedSamples = trace.Length, stepSeconds = step,
            impulseMagnitude = .01, worldAxis = new[] { 1.0, 0, 0 },
            baselineBodyAWorldCenterOfMass = new[] { 0.0, 0, 0 },
            baselineBodyBWorldCenterOfMass = new[] { 1.0, 0, 0 },
            referenceRelativeCenterOfMass = new[] { 1.0, 0, 0 }, requestedBodyAImpulse = new[] { .01, 0, 0 },
            requestedBodyBImpulse = new[] { -.01, 0, 0 }, requestedNetImpulse = new[] { 0.0, 0, 0 },
            baseline = baseline,
            injection = new StructuralInjectionWitness { status = "completed", callback = lifecycle.injectionCallback,
                physicsEpoch = 10, physicsCycle = 1, totalInjectionCallbacks = 1,
                bodyAInstanceId = 1, bodyBInstanceId = 2, totalBodyACommands = 1, totalBodyBCommands = 1,
                bodyACommandReturned = true, bodyBCommandReturned = true,
                bodyAImpulse = new[] { .01, 0, 0 }, bodyBImpulse = new[] { -.01, 0, 0 } },
            admission = new StructuralAdmissionEvidence { status = "verified", topologyGeneration = 4,
                dynamicBodyCount = 2, mappedJointCount = 1, unmappedJointCount = 0, jointEnabled = true,
                bodyAInstanceId = 1, bodyBInstanceId = 2, jointInstanceId = 3,
                jointType = "UnityEngine.ConfigurableJoint", jointHostBodyInstanceId = 1,
                jointConnectedBodyInstanceId = 2, joint = Joint(), jointTransformRotation = new[] { 0.0, 0, 0, 1 },
                installedContactSentinels = 2, removedContactSentinels = 2,
                contactWindowFirstEpoch = 10, contactWindowLastEpoch = 129,
                contactObservationCount = trace.Length, detectedContactCount = 0, jointBreakCount = 0,
                bodyASentinelTargetInstanceId = 1, bodyBSentinelTargetInstanceId = 2,
                hookCleanupStatus = "removed-owned-hooks" },
            trace = new StructuralTrace { evidence = "portable-helper-fixture",
                stepSeconds = step, samples = trace }, bodySamples = bodies,
        };
        report.topology = StructuralExperiment.ComputeTopology(report);
        report.admission.topology = report.trace.topology = report.topology;
        return report;
    }

    static int Main()
    {
        var valid = Fixture(); StructuralExperiment.Validate(valid);
        Check(ReportJson.Encode(valid).Contains("\"receiptValidity\":\"valid\""), "receipt did not serialize");
        Check(StructuralResponse.FitAndPredict(valid.trace, 40).heldOutSamples == 80, "40/80 analysis split changed");
        var sham = Fixture(); sham.mode = "sham"; sham.impulseMagnitude = 0;
        sham.requestedBodyAImpulse = sham.requestedBodyBImpulse = sham.requestedNetImpulse = new[] { 0.0, 0, 0 };
        sham.injection.bodyAImpulse = sham.injection.bodyBImpulse = new[] { 0.0, 0, 0 };
        sham.injection.totalBodyACommands = sham.injection.totalBodyBCommands = 0;
        sham.injection.bodyACommandReturned = sham.injection.bodyBCommandReturned = false;
        StructuralExperiment.Validate(sham); Check(true, "valid sham rejected");
        var provisional = Fixture(); provisional.runEligibility = "provisional-contact-unobserved";
        provisional.contactObservationStatus = "unavailable";
        provisional.admission.installedContactSentinels = provisional.admission.removedContactSentinels = 0;
        provisional.admission.contactWindowFirstEpoch = provisional.admission.contactWindowLastEpoch = -1;
        provisional.admission.contactObservationCount = 0;
        StructuralExperiment.Validate(provisional); Check(true, "valid provisional capture rejected");
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
        changed = Fixture(); changed.lifecycleQualification.evidence = "native-isolated-rigidbody-observation";
        changed.lifecycleQualification.qualificationId = LifecycleOrderQualification.ComputeId(changed.lifecycleQualification);
        changed.lifecycleQualificationId = changed.lifecycleQualification.qualificationId;
        Reject(() => StructuralExperiment.Validate(changed), "native qualification accepted for a portable receipt");
        changed = Fixture(); changed.referenceRelativeCenterOfMass[0] += .1;
        for (int i = 0; i < changed.trace.samples.Length; i++) changed.trace.samples[i].relativeDisplacement -= .1;
        Reject(() => StructuralExperiment.Validate(changed), "shifted displacement reference accepted");
        changed = Fixture(); changed.injection.totalInjectionCallbacks = 2;
        Reject(() => StructuralExperiment.Validate(changed), "duplicate injection callback accepted");
        changed = Fixture(); changed.injection = null;
        Reject(() => StructuralExperiment.Validate(changed), "missing injection witness accepted");
        changed = Fixture(); changed.injection.physicsEpoch++;
        Reject(() => StructuralExperiment.Validate(changed), "injection epoch detached from baseline");
        changed = Fixture(); changed.injection.bodyBCommandReturned = false;
        Reject(() => StructuralExperiment.Validate(changed), "half-completed injection accepted");
        changed = Fixture(); changed.injection.totalBodyACommands = 2;
        Reject(() => StructuralExperiment.Validate(changed), "repeated injection command accepted");
        changed = Fixture(); changed.baseline.bodyAWorldCenterOfMass[0] = 1000;
        Reject(() => StructuralExperiment.Validate(changed), "unbound baseline accepted");
        changed = Fixture(); changed.baseline.unityFrame = 999;
        Reject(() => StructuralExperiment.Validate(changed), "cross-frame baseline accepted");
        changed = Fixture(); changed.bodySamples[60].bodyAInstanceId = 2;
        Reject(() => StructuralExperiment.Validate(changed), "mid-run body swap accepted");
        changed = Fixture(); changed.bodySamples[0] = null;
        Reject(() => StructuralExperiment.Validate(changed), "missing first body sample accepted");
        changed = Fixture(); changed.bodySamples[changed.bodySamples.Length - 1] = null;
        Reject(() => StructuralExperiment.Validate(changed), "missing last body sample accepted");
        changed = Fixture(); changed.admission.dynamicBodyCount = 3;
        Reject(() => StructuralExperiment.Validate(changed), "extra dynamic body accepted");
        changed = Fixture(); changed.admission.jointType = "UnityEngine.FixedJoint";
        Reject(() => StructuralExperiment.Validate(changed), "wrong joint type accepted");
        changed = Fixture(); changed.admission.jointConnectedBodyInstanceId = 1;
        Reject(() => StructuralExperiment.Validate(changed), "wrong joint endpoint accepted");
        changed = Fixture(); changed.topology = changed.admission.topology = changed.trace.topology = "arbitrary";
        Reject(() => StructuralExperiment.Validate(changed), "free-form topology identity accepted");
        changed = Fixture(); changed.admission.joint.configurable.xDrive.positionSpring++;
        Reject(() => StructuralExperiment.Validate(changed), "joint configuration mutation accepted under stale topology");
        changed = Fixture(); changed.admission.joint.axis = new[] { 0.0, 1, 0 };
        changed.topology = StructuralExperiment.ComputeTopology(changed);
        changed.admission.topology = changed.trace.topology = changed.topology;
        Reject(() => StructuralExperiment.Validate(changed), "world axis detached from joint axis");
        changed = Fixture(); changed.admission.detectedContactCount = 1;
        Reject(() => StructuralExperiment.Validate(changed), "observed contact accepted");
        changed = Fixture(); changed.admission.bodyASentinelTargetInstanceId = 2;
        Reject(() => StructuralExperiment.Validate(changed), "misbound contact sentinel accepted");
        changed = Fixture(); changed.admission.hookCleanupStatus = "pending";
        Reject(() => StructuralExperiment.Validate(changed), "unclean physics hooks accepted");
        changed = Fixture(); changed.lifecycleQualification.trials[1].fixedDeltaSeconds = .03;
        changed.lifecycleQualification.qualificationId = LifecycleOrderQualification.ComputeId(changed.lifecycleQualification);
        changed.lifecycleQualificationId = changed.lifecycleQualification.qualificationId;
        Reject(() => StructuralExperiment.Validate(changed), "varying qualified timestep accepted");
        changed = Fixture(); changed.stepSeconds = .01; changed.trace.stepSeconds = .01;
        for (int i = 0; i < changed.bodySamples.Length; i++) changed.bodySamples[i].fixedTimeSeconds = 20 + i * .01;
        Reject(() => StructuralExperiment.Validate(changed), "structural timestep detached from qualification");
        changed = Fixture(); changed.unity = changed.ksp = changed.plugin = "";
        changed.lifecycleQualification.unity = changed.lifecycleQualification.ksp = changed.lifecycleQualification.plugin = "";
        changed.lifecycleQualification.qualificationId = LifecycleOrderQualification.ComputeId(changed.lifecycleQualification);
        changed.lifecycleQualificationId = changed.lifecycleQualification.qualificationId;
        Reject(() => StructuralExperiment.Validate(changed), "empty build identity accepted");
        changed = Fixture(); changed.bodySamples[8].unityFrame = 0;
        Reject(() => StructuralExperiment.Validate(changed), "rendered frame reversal accepted");
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
