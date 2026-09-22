using System;

namespace KspContinuum
{
    public sealed class StructuralExperimentReport
    {
        public const int RequiredSamples = 120;
        public string schema = "ksp-continuum-structural-experiment/v1";
        public string evidence = "native-adapter-observation";
        public string status = "waiting";
        public string reason;
        public string receiptValidity = "pending";
        public string runEligibility = "pending";
        public string experimentQualified = "not-evaluated";
        public string cleanupStatus = "pending";
        public string mode = "impulse";
        public string impulseApi = "Rigidbody.AddForce(worldVector,ForceMode.Impulse)";
        public string injectionCallback;
        public string observationCallback;
        public string lifecycleQualificationId;
        public string contactObservationStatus = "unavailable";
        public string coordinateSchema = "ksp-continuum-structural-relative-coordinate/v1";
        public string startedUtc;
        public string unity;
        public string ksp;
        public string plugin;
        public string vesselId;
        public int bodyAInstanceId;
        public int bodyBInstanceId;
        public int jointInstanceId;
        public int requestedSamples = RequiredSamples;
        public int retainedSamples;
        public double stepSeconds;
        public double impulseMagnitude = .01;
        public double[] worldAxis = new double[0];
        public double[] referenceRelativeCenterOfMass = new double[0];
        public double[] requestedBodyAImpulse = new double[0];
        public double[] requestedBodyBImpulse = new double[0];
        public double[] requestedNetImpulse = new double[0];
        public StructuralTrace trace;
        public StructuralBodySample[] bodySamples = new StructuralBodySample[0];
    }

    public sealed class StructuralBodySample
    {
        public long physicsEpoch;
        public long topologyGeneration;
        public long frameGeneration;
        public long originEventCount;
        public int unityFrame;
        public double fixedTimeSeconds;
        public double[] bodyAWorldCenterOfMass;
        public double[] bodyARotation;
        public double[] bodyAVelocity;
        public double[] bodyAAngularVelocity;
        public double[] bodyBWorldCenterOfMass;
        public double[] bodyBRotation;
        public double[] bodyBVelocity;
        public double[] bodyBAngularVelocity;
    }

    public static class StructuralExperiment
    {
        public static void Validate(StructuralExperimentReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            Require(report.schema == "ksp-continuum-structural-experiment/v1", "Unexpected structural experiment schema.");
            Require(report.evidence == "native-adapter-observation" || report.evidence == "portable-helper-fixture",
                "Unexpected structural experiment provenance.");
            Require(report.mode == "impulse" || report.mode == "sham", "Structural experiment mode is invalid.");
            Require(report.requestedSamples == StructuralExperimentReport.RequiredSamples,
                "Structural experiment sample request changed.");
            if (report.status != "complete")
            {
                Require(report.status == "invalid" || report.status == "unavailable" || report.status == "waiting",
                    "Structural experiment status is invalid.");
                Require(!String.IsNullOrEmpty(report.reason) || report.status == "waiting",
                    "Terminal structural experiment has no reason.");
                return;
            }
            Require(String.IsNullOrEmpty(report.reason), "Complete structural experiment has a failure reason.");
            Require(report.receiptValidity == "valid", "Complete structural experiment receipt is invalid.");
            Require(report.runEligibility == "eligible" || report.runEligibility == "provisional-contact-unobserved",
                "Complete structural experiment eligibility is invalid.");
            Require(report.experimentQualified == "not-evaluated", "Capture receipt cannot claim scientific qualification.");
            Require(report.cleanupStatus == "complete", "Structural experiment cleanup is incomplete.");
            Require(report.contactObservationStatus == "observed-none" || report.contactObservationStatus == "unavailable",
                "Unexpected contact observation status.");
            Require(report.runEligibility == "eligible" && report.contactObservationStatus == "observed-none"
                || report.runEligibility == "provisional-contact-unobserved" && report.contactObservationStatus == "unavailable",
                "Contact evidence contradicts run eligibility.");
            Require(report.coordinateSchema == "ksp-continuum-structural-relative-coordinate/v1",
                "Structural coordinate schema is unsupported.");
            Require(!String.IsNullOrEmpty(report.vesselId), "Structural vessel identity is missing.");
            Require(report.bodyAInstanceId != report.bodyBInstanceId && report.bodyAInstanceId != 0
                && report.bodyBInstanceId != 0 && report.jointInstanceId != 0, "Structural native identities are invalid.");
            Require(Finite(report.stepSeconds) && report.stepSeconds > 0, "Structural step is invalid.");
            Require(Finite(report.impulseMagnitude) && report.impulseMagnitude >= 0, "Impulse magnitude is invalid.");
            Require(report.mode == "sham" && report.impulseMagnitude == 0
                || report.mode == "impulse" && report.impulseMagnitude > 0,
                "Impulse magnitude contradicts experiment mode.");
            Require(report.impulseApi == "Rigidbody.AddForce(worldVector,ForceMode.Impulse)",
                "Structural impulse API is unsupported.");
            Require(!String.IsNullOrEmpty(report.injectionCallback) && !String.IsNullOrEmpty(report.observationCallback)
                && !String.IsNullOrEmpty(report.lifecycleQualificationId),
                "Installed callback qualification is missing.");
            Vector(report.worldAxis, 3, "world axis");
            Vector(report.referenceRelativeCenterOfMass, 3, "reference relative center of mass");
            Vector(report.requestedBodyAImpulse, 3, "requested body A impulse");
            Vector(report.requestedBodyBImpulse, 3, "requested body B impulse");
            Vector(report.requestedNetImpulse, 3, "requested net impulse");
            for (int i = 0; i < 3; i++)
            {
                Require(report.requestedBodyAImpulse[i] == -report.requestedBodyBImpulse[i], "Requested impulses are not exactly equal and opposite.");
                Require(report.requestedNetImpulse[i] == 0, "Requested net impulse is not exactly zero.");
                if (report.mode == "sham") Require(report.requestedBodyAImpulse[i] == 0, "Sham experiment requests an impulse.");
                double expected = report.mode == "sham" ? 0 : report.worldAxis[i] * report.impulseMagnitude;
                Require(Math.Abs(report.requestedBodyAImpulse[i] - expected) <= 1e-12,
                    "Requested impulse does not match the frozen axis and magnitude.");
            }
            double axisNorm = report.worldAxis[0] * report.worldAxis[0] + report.worldAxis[1] * report.worldAxis[1]
                + report.worldAxis[2] * report.worldAxis[2];
            Require(Math.Abs(axisNorm - 1) <= 1e-9, "Structural world axis is not normalized.");
            Require(report.retainedSamples == StructuralExperimentReport.RequiredSamples,
                "Structural experiment is incomplete.");
            Require(report.bodySamples != null && report.bodySamples.Length == report.retainedSamples,
                "Structural body sample count is inconsistent.");
            StructuralResponse.Validate(report.trace);
            Require(report.trace.samples.Length == report.retainedSamples && report.trace.stepSeconds == report.stepSeconds,
                "Structural trace does not match the experiment.");
            long topologyGeneration = -1, frameGeneration = -1, originEventCount = -1;
            double previousTime = 0;
            for (int i = 0; i < report.bodySamples.Length; i++)
            {
                StructuralBodySample sample = report.bodySamples[i];
                Require(sample != null && sample.physicsEpoch == report.trace.samples[i].physicsEpoch,
                    "Structural body sample epoch is inconsistent.");
                Require(sample.physicsEpoch >= 0 && sample.physicsEpoch != Int64.MaxValue && sample.topologyGeneration >= 0
                    && sample.frameGeneration >= 0 && sample.originEventCount >= 0 && sample.unityFrame >= 0
                    && Finite(sample.fixedTimeSeconds), "Structural sample context is invalid.");
                if (i == 0) { topologyGeneration = sample.topologyGeneration; frameGeneration = sample.frameGeneration;
                    originEventCount = sample.originEventCount; }
                Require(sample.topologyGeneration == topologyGeneration && sample.frameGeneration == frameGeneration
                    && sample.originEventCount == originEventCount, "Structural sample context changed.");
                if (i != 0) Require(Math.Abs((sample.fixedTimeSeconds - previousTime) - report.stepSeconds) <= 1e-6,
                    "Structural fixed-time sequence is inconsistent.");
                previousTime = sample.fixedTimeSeconds;
                Vector(sample.bodyAWorldCenterOfMass, 3, "body A center of mass"); Quaternion(sample.bodyARotation, "body A rotation");
                Vector(sample.bodyAVelocity, 3, "body A velocity"); Vector(sample.bodyAAngularVelocity, 3, "body A angular velocity");
                Vector(sample.bodyBWorldCenterOfMass, 3, "body B center of mass"); Quaternion(sample.bodyBRotation, "body B rotation");
                Vector(sample.bodyBVelocity, 3, "body B velocity"); Vector(sample.bodyBAngularVelocity, 3, "body B angular velocity");
                double displacement = 0, velocity = 0;
                for (int component = 0; component < 3; component++)
                {
                    displacement += (sample.bodyBWorldCenterOfMass[component] - sample.bodyAWorldCenterOfMass[component]
                        - report.referenceRelativeCenterOfMass[component]) * report.worldAxis[component];
                    velocity += (sample.bodyBVelocity[component] - sample.bodyAVelocity[component]) * report.worldAxis[component];
                }
                Require(Math.Abs(displacement - report.trace.samples[i].relativeDisplacement) <= 1e-9
                    && Math.Abs(velocity - report.trace.samples[i].relativeVelocity) <= 1e-9,
                    "Structural derived trace does not match raw body states.");
            }
        }

        static void Vector(double[] value, int length, string name)
        {
            Require(value != null && value.Length == length, name + " has the wrong shape.");
            for (int i = 0; i < value.Length; i++) Require(Finite(value[i]), name + " contains a nonfinite value.");
        }

        static void Quaternion(double[] value, string name)
        {
            Vector(value, 4, name);
            double norm = value[0] * value[0] + value[1] * value[1] + value[2] * value[2] + value[3] * value[3];
            Require(Finite(norm) && Math.Abs(norm - 1) <= .001, name + " must be a unit quaternion.");
        }

        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
