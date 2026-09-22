using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KspContinuum
{
    public sealed class StructuralExperimentReport
    {
        public const int RequiredSamples = 120;
        public string schema = "ksp-continuum-structural-experiment/v2";
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
        public LifecycleOrderQualificationReport lifecycleQualification;
        public string contactObservationStatus = "unavailable";
        public string coordinateSchema = "ksp-continuum-structural-relative-coordinate/v1";
        public string startedUtc;
        public string sessionId;
        public string runId;
        public string unity;
        public string ksp;
        public string plugin;
        public string vesselId;
        public string topology;
        public int bodyAInstanceId;
        public int bodyBInstanceId;
        public int jointInstanceId;
        public int requestedSamples = RequiredSamples;
        public int retainedSamples;
        public double stepSeconds;
        public double impulseMagnitude = .01;
        public double[] worldAxis = new double[0];
        public double[] baselineBodyAWorldCenterOfMass = new double[0];
        public double[] baselineBodyBWorldCenterOfMass = new double[0];
        public double[] referenceRelativeCenterOfMass = new double[0];
        public double[] requestedBodyAImpulse = new double[0];
        public double[] requestedBodyBImpulse = new double[0];
        public double[] requestedNetImpulse = new double[0];
        public StructuralAdmissionEvidence admission;
        public StructuralBodySample baseline;
        public StructuralInjectionWitness injection;
        public StructuralTrace trace;
        public StructuralBodySample[] bodySamples = new StructuralBodySample[0];
    }

    public sealed class StructuralBodySample
    {
        public long physicsEpoch;
        public long topologyGeneration;
        public long frameGeneration;
        public long originEventCount;
        public long physicsCycle;
        public int bodyAInstanceId;
        public int bodyBInstanceId;
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

    public sealed class StructuralAdmissionEvidence
    {
        public string status = "pending";
        public string topology;
        public int dynamicBodyCount;
        public int mappedJointCount;
        public int unmappedJointCount;
        public int bodyAInstanceId;
        public int bodyBInstanceId;
        public int jointInstanceId;
        public string jointType;
        public int jointHostBodyInstanceId;
        public int jointConnectedBodyInstanceId;
        public bool jointEnabled;
        public long topologyGeneration;
        public StructuralLink joint;
        public double[] jointTransformRotation = new double[0];
        public int installedContactSentinels;
        public int removedContactSentinels;
        public long contactWindowFirstEpoch;
        public long contactWindowLastEpoch;
        public int contactObservationCount;
        public int detectedContactCount;
        public int jointBreakCount;
        public int bodyASentinelTargetInstanceId;
        public int bodyBSentinelTargetInstanceId;
        public string hookCleanupStatus;
    }

    public sealed class StructuralInjectionWitness
    {
        public string status = "pending";
        public string callback;
        public long physicsEpoch;
        public long physicsCycle;
        public int totalInjectionCallbacks;
        public int bodyAInstanceId;
        public int bodyBInstanceId;
        public int totalBodyACommands;
        public int totalBodyBCommands;
        public bool bodyACommandReturned;
        public bool bodyBCommandReturned;
        public double[] bodyAImpulse = new double[0];
        public double[] bodyBImpulse = new double[0];
    }

    public static class StructuralExperiment
    {
        public static void Validate(StructuralExperimentReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            Require(report.schema == "ksp-continuum-structural-experiment/v2", "Unexpected structural experiment schema.");
            Require(report.evidence == "native-adapter-observation" || report.evidence == "portable-helper-fixture",
                "Unexpected structural experiment provenance.");
            Require(report.mode == "impulse" || report.mode == "sham", "Structural experiment mode is invalid.");
            Require(report.requestedSamples == StructuralExperimentReport.RequiredSamples,
                "Structural experiment sample request changed.");
            if (report.status != "complete")
            {
                Require(report.status == "invalid" || report.status == "unavailable" || report.status == "waiting",
                    "Structural experiment status is invalid.");
                Require(report.status == "waiting" && report.receiptValidity == "pending"
                    && report.runEligibility == "pending" && report.cleanupStatus == "pending"
                    && String.IsNullOrEmpty(report.reason)
                    || report.status != "waiting" && report.receiptValidity == "valid-invalidated-run"
                    && report.runEligibility == "ineligible" && report.cleanupStatus == "complete"
                    && !String.IsNullOrEmpty(report.reason),
                    "Non-complete structural experiment state is contradictory.");
                Require(report.experimentQualified == "not-evaluated", "Incomplete capture claimed qualification.");
                Require(report.trace == null && report.bodySamples != null && report.bodySamples.Length == 0
                    && report.retainedSamples == 0, "V2 invalid receipt must not publish partial samples.");
                Require(report.worldAxis != null && report.worldAxis.Length == 0
                    && report.baselineBodyAWorldCenterOfMass != null && report.baselineBodyAWorldCenterOfMass.Length == 0
                    && report.baselineBodyBWorldCenterOfMass != null && report.baselineBodyBWorldCenterOfMass.Length == 0
                    && report.referenceRelativeCenterOfMass != null && report.referenceRelativeCenterOfMass.Length == 0
                    && report.requestedBodyAImpulse != null && report.requestedBodyAImpulse.Length == 0
                    && report.requestedBodyBImpulse != null && report.requestedBodyBImpulse.Length == 0
                    && report.requestedNetImpulse != null && report.requestedNetImpulse.Length == 0,
                    "V2 invalid receipt contains unvalidated numeric payloads.");
                Require(report.admission == null && report.baseline == null && report.injection == null,
                    "V2 invalid receipt retained qualifying evidence.");
                Require(Finite(report.stepSeconds) && Finite(report.impulseMagnitude),
                    "Non-complete structural experiment contains nonfinite scalars.");
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
            DateTime started;
            Require(!String.IsNullOrEmpty(report.startedUtc) && DateTime.TryParse(report.startedUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out started), "Structural start time is missing or invalid.");
            Require(!String.IsNullOrEmpty(report.sessionId) && !String.IsNullOrEmpty(report.runId)
                && !String.IsNullOrEmpty(report.unity) && !String.IsNullOrEmpty(report.ksp)
                && !String.IsNullOrEmpty(report.plugin), "Structural run or build identity is missing.");
            Require(!String.IsNullOrEmpty(report.vesselId) && !String.IsNullOrEmpty(report.topology),
                "Structural vessel or topology identity is missing.");
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
            Require(report.lifecycleQualification != null, "Installed lifecycle qualification is missing.");
            LifecycleOrderQualification.Validate(report.lifecycleQualification);
            Require(report.lifecycleQualification.status == "qualified"
                && (report.evidence == "portable-helper-fixture" && report.lifecycleQualification.evidence == "portable-helper-fixture"
                    || report.evidence == "native-adapter-observation"
                    && report.lifecycleQualification.evidence == "native-isolated-rigidbody-observation")
                && report.lifecycleQualification.qualificationId == report.lifecycleQualificationId
                && report.lifecycleQualification.injectionCallback == report.injectionCallback
                && report.lifecycleQualification.observationCallback == report.observationCallback
                && report.lifecycleQualification.unity == report.unity
                && report.lifecycleQualification.ksp == report.ksp
                && report.lifecycleQualification.plugin == report.plugin,
                "Installed callback qualification does not bind this structural experiment.");
            double qualifiedStep = LifecycleOrderQualification.QualifiedStepSeconds(report.lifecycleQualification);
            Require(Math.Abs(report.stepSeconds - qualifiedStep) <= 1e-9,
                "Lifecycle qualification timestep does not bind this structural experiment.");
            Vector(report.worldAxis, 3, "world axis");
            Vector(report.baselineBodyAWorldCenterOfMass, 3, "baseline body A center of mass");
            Vector(report.baselineBodyBWorldCenterOfMass, 3, "baseline body B center of mass");
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
                Require(Math.Abs(report.requestedBodyAImpulse[i] - expected) <= 1e-6 * Math.Max(1, Math.Abs(expected)),
                    "Requested impulse does not match the frozen axis and magnitude.");
                Require(Math.Abs(report.baselineBodyBWorldCenterOfMass[i] - report.baselineBodyAWorldCenterOfMass[i]
                    - report.referenceRelativeCenterOfMass[i]) <= 1e-12,
                    "Structural displacement reference does not match the raw baseline.");
            }
            double axisNorm = report.worldAxis[0] * report.worldAxis[0] + report.worldAxis[1] * report.worldAxis[1]
                + report.worldAxis[2] * report.worldAxis[2];
            Require(Math.Abs(axisNorm - 1) <= 1e-9, "Structural world axis is not normalized.");
            Require(report.retainedSamples == StructuralExperimentReport.RequiredSamples,
                "Structural experiment is incomplete.");
            Require(report.bodySamples != null && report.bodySamples.Length == report.retainedSamples,
                "Structural body sample count is inconsistent.");
            ValidateBaselineAndInjection(report);
            ValidateAdmission(report);
            Require(report.trace != null, "Structural trace is missing.");
            StructuralResponse.Validate(report.trace);
            Require(report.trace.evidence == report.evidence, "Structural trace provenance does not match its receipt.");
            Require(report.trace.topology == report.topology, "Structural trace topology does not match its receipt.");
            Require(report.trace.samples.Length == report.retainedSamples && report.trace.stepSeconds == report.stepSeconds,
                "Structural trace does not match the experiment.");
            long topologyGeneration = -1, frameGeneration = -1, originEventCount = -1;
            int previousUnityFrame = -1;
            double previousTime = 0;
            for (int i = 0; i < report.bodySamples.Length; i++)
            {
                StructuralBodySample sample = report.bodySamples[i];
                Require(sample != null && sample.physicsEpoch == report.trace.samples[i].physicsEpoch,
                    "Structural body sample epoch is inconsistent.");
                Require(sample.physicsEpoch >= 0 && sample.physicsEpoch != Int64.MaxValue && sample.topologyGeneration > 0
                    && sample.frameGeneration > 0 && sample.originEventCount >= 0 && sample.unityFrame >= 0
                    && Finite(sample.fixedTimeSeconds), "Structural sample context is invalid.");
                Require(sample.bodyAInstanceId == report.bodyAInstanceId && sample.bodyBInstanceId == report.bodyBInstanceId,
                    "Structural sample body identity changed.");
                Require(sample.physicsCycle == i + 1, "Structural physics-cycle sequence is not exact.");
                Require(sample.unityFrame >= previousUnityFrame, "Structural rendered frame sequence reversed.");
                previousUnityFrame = sample.unityFrame;
                if (i == 0) { topologyGeneration = sample.topologyGeneration; frameGeneration = sample.frameGeneration;
                    originEventCount = sample.originEventCount; }
                Require(sample.topologyGeneration == topologyGeneration && sample.frameGeneration == frameGeneration
                    && sample.originEventCount == originEventCount, "Structural sample context changed.");
                if (i != 0) Require(Math.Abs((sample.fixedTimeSeconds - previousTime) - report.stepSeconds) <= 1e-6,
                    "Structural fixed-time sequence is inconsistent.");
                Require(sample.physicsEpoch == report.baseline.physicsEpoch + i,
                    "Structural samples are not consecutive from the injection epoch.");
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

        static void ValidateAdmission(StructuralExperimentReport report)
        {
            StructuralAdmissionEvidence value = report.admission;
            Require(value != null && value.status == "verified", "Structural admission evidence is missing.");
            Require(value.dynamicBodyCount == 2 && value.mappedJointCount == 1 && value.unmappedJointCount == 0
                && value.jointEnabled, "Structural topology was not admitted exactly.");
            Require(value.bodyAInstanceId == report.bodyAInstanceId && value.bodyBInstanceId == report.bodyBInstanceId
                && value.jointInstanceId == report.jointInstanceId
                && value.jointType == "UnityEngine.ConfigurableJoint"
                && value.jointHostBodyInstanceId == report.bodyAInstanceId
                && value.jointConnectedBodyInstanceId == report.bodyBInstanceId,
                "Structural admission identities do not bind the report.");
            Require(value.topologyGeneration > 0 && value.topologyGeneration == report.baseline.topologyGeneration,
                "Structural admission generation does not bind the run.");
            ShadowPhysicalInput.ValidateStructuralLink(value.joint, 0, 1);
            Require(value.joint.nativeInstanceId == report.jointInstanceId
                && value.joint.jointType == "UnityEngine.ConfigurableJoint",
                "Structural admission joint payload does not bind the native joint.");
            Quaternion(value.jointTransformRotation, "admitted joint transform rotation");
            Require(value.topology == report.topology && report.topology == ComputeTopology(report),
                "Structural admission topology does not bind its full payload.");
            double[] transformedAxis = Rotate(value.jointTransformRotation, value.joint.axis);
            double transformedNorm = Math.Sqrt(transformedAxis[0] * transformedAxis[0]
                + transformedAxis[1] * transformedAxis[1] + transformedAxis[2] * transformedAxis[2]);
            Require(transformedNorm > 0, "Admitted joint axis is degenerate.");
            for (int i = 0; i < 3; i++) Require(Math.Abs(report.worldAxis[i] - transformedAxis[i] / transformedNorm) <= 1e-6,
                "Structural world axis is not derived from the admitted joint axis.");
            if (report.runEligibility == "eligible")
            {
                StructuralBodySample last = report.bodySamples[report.bodySamples.Length - 1];
                Require(report.contactObservationStatus == "observed-none"
                    && last != null
                    && value.installedContactSentinels == 2 && value.removedContactSentinels == 2
                    && value.contactWindowFirstEpoch == report.baseline.physicsEpoch
                    && value.contactWindowLastEpoch == last.physicsEpoch
                    && value.contactObservationCount == report.bodySamples.Length
                    && value.detectedContactCount == 0 && value.jointBreakCount == 0,
                    "Structural contact sentinel evidence is incomplete or observed interference.");
                Require(value.bodyASentinelTargetInstanceId == report.bodyAInstanceId
                    && value.bodyBSentinelTargetInstanceId == report.bodyBInstanceId,
                    "Structural contact sentinels do not bind both bodies.");
            }
            else
            {
                Require(report.runEligibility == "provisional-contact-unobserved"
                    && report.contactObservationStatus == "unavailable"
                    && value.installedContactSentinels == 0 && value.removedContactSentinels == 0
                    && value.contactWindowFirstEpoch == -1 && value.contactWindowLastEpoch == -1
                    && value.contactObservationCount == 0 && value.detectedContactCount == 0
                    && value.jointBreakCount == 0 && value.bodyASentinelTargetInstanceId == 0
                    && value.bodyBSentinelTargetInstanceId == 0,
                    "Provisional structural capture contains contradictory contact evidence.");
            }
            Require(value.hookCleanupStatus == "removed-owned-hooks",
                "Structural physics-boundary hook cleanup is incomplete.");
        }

        static void ValidateBaselineAndInjection(StructuralExperimentReport report)
        {
            StructuralBodySample baseline = report.baseline;
            StructuralInjectionWitness injection = report.injection;
            Require(baseline != null && injection != null && injection.status == "completed",
                "Structural baseline or injection witness is missing.");
            Require(baseline.physicsEpoch >= 0 && baseline.physicsEpoch != Int64.MaxValue
                && baseline.physicsCycle == 1 && baseline.topologyGeneration > 0 && baseline.frameGeneration > 0
                && baseline.originEventCount >= 0 && baseline.unityFrame >= 0 && Finite(baseline.fixedTimeSeconds),
                "Structural baseline context is invalid.");
            Require(baseline.bodyAInstanceId == report.bodyAInstanceId && baseline.bodyBInstanceId == report.bodyBInstanceId,
                "Structural baseline body identity changed.");
            Require(injection.callback == report.injectionCallback && injection.physicsEpoch == baseline.physicsEpoch
                && injection.physicsCycle == baseline.physicsCycle && injection.totalInjectionCallbacks == 1
                && injection.bodyAInstanceId == report.bodyAInstanceId && injection.bodyBInstanceId == report.bodyBInstanceId,
                "Structural injection did not execute once in the baseline callback.");
            int expectedCalls = report.mode == "impulse" ? 1 : 0;
            bool expectedReturned = report.mode == "impulse";
            Require(injection.totalBodyACommands == expectedCalls && injection.totalBodyBCommands == expectedCalls
                && injection.bodyACommandReturned == expectedReturned && injection.bodyBCommandReturned == expectedReturned,
                "Structural injection command completion is not exact.");
            Vector(baseline.bodyAWorldCenterOfMass, 3, "baseline event body A center of mass");
            Quaternion(baseline.bodyARotation, "baseline event body A rotation");
            Vector(baseline.bodyAVelocity, 3, "baseline event body A velocity");
            Vector(baseline.bodyAAngularVelocity, 3, "baseline event body A angular velocity");
            Vector(baseline.bodyBWorldCenterOfMass, 3, "baseline event body B center of mass");
            Quaternion(baseline.bodyBRotation, "baseline event body B rotation");
            Vector(baseline.bodyBVelocity, 3, "baseline event body B velocity");
            Vector(baseline.bodyBAngularVelocity, 3, "baseline event body B angular velocity");
            Vector(injection.bodyAImpulse, 3, "witness body A impulse");
            Vector(injection.bodyBImpulse, 3, "witness body B impulse");
            StructuralBodySample first = report.bodySamples[0];
            Require(first != null && first.physicsEpoch == baseline.physicsEpoch && first.fixedTimeSeconds == baseline.fixedTimeSeconds
                && first.physicsCycle == baseline.physicsCycle && first.unityFrame == baseline.unityFrame
                && first.topologyGeneration == baseline.topologyGeneration && first.frameGeneration == baseline.frameGeneration
                && first.originEventCount == baseline.originEventCount,
                "Structural first response does not bind the pre-physics baseline.");
            for (int i = 0; i < 3; i++)
            {
                Require(baseline.bodyAWorldCenterOfMass[i] == report.baselineBodyAWorldCenterOfMass[i]
                    && baseline.bodyBWorldCenterOfMass[i] == report.baselineBodyBWorldCenterOfMass[i]
                    && report.referenceRelativeCenterOfMass[i] == baseline.bodyBWorldCenterOfMass[i]
                        - baseline.bodyAWorldCenterOfMass[i],
                    "Structural baseline arrays do not bind the injection event.");
                Require(injection.bodyAImpulse[i] == report.requestedBodyAImpulse[i]
                    && injection.bodyBImpulse[i] == report.requestedBodyBImpulse[i],
                    "Structural injection witness does not match the request.");
            }
        }

        public static string ComputeTopology(StructuralExperimentReport report)
        {
            if (report == null || report.admission == null || report.admission.joint == null)
                throw new ArgumentException("Missing structural topology evidence.");
            string canonical = report.bodyAInstanceId.ToString(CultureInfo.InvariantCulture) + "|"
                + report.bodyBInstanceId.ToString(CultureInfo.InvariantCulture) + "|"
                + report.jointInstanceId.ToString(CultureInfo.InvariantCulture) + "|"
                + (report.admission.jointEnabled ? "1" : "0") + "|"
                + ReportJson.Encode(report.admission.jointTransformRotation) + "|"
                + ReportJson.Encode(report.admission.joint);
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var result = new StringBuilder("sha256:");
                foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        static double[] Rotate(double[] quaternion, double[] vector)
        {
            Vector(vector, 3, "joint local axis");
            double x = quaternion[0], y = quaternion[1], z = quaternion[2], w = quaternion[3];
            double tx = 2 * (y * vector[2] - z * vector[1]);
            double ty = 2 * (z * vector[0] - x * vector[2]);
            double tz = 2 * (x * vector[1] - y * vector[0]);
            return new[] { vector[0] + w * tx + (y * tz - z * ty),
                vector[1] + w * ty + (z * tx - x * tz),
                vector[2] + w * tz + (x * ty - y * tx) };
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
