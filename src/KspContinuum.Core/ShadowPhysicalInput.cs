using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public static class ShadowPhysicalInput
    {
        public const string PhysicalInputSchema = "ksp-continuum-rigidbody-input/v1";
        public const string ReferenceFrameSchema = "ksp-continuum-unity-frame-context/v1";
        public const string UnityWorldReferenceFrame = "unity-world-at-capture";
        public const string SyntheticZeroForce = "synthetic-zero-not-native-measurement";
        public const string AggregateForceUnavailable = "unavailable-not-captured";
        const int MaximumCapturedBodies = 512;
        const int MaximumCapturedLinks = 2048;
        const double QuaternionNormTolerance = .001;

        public static void Validate(ShadowReport report)
        {
            if (report == null)
                throw new ArgumentNullException("report");
            Require(
                report.schema == "ksp-continuum-flight-shadow/v2",
                "Unexpected shadow report schema."
            );
            Require(
                report.evidence == "native-adapter-observation"
                    || report.evidence == "portable-helper-fixture",
                "Unexpected shadow evidence provenance."
            );
            Require(
                report.physicalInputSchema == PhysicalInputSchema,
                "Unexpected physical input schema."
            );
            Require(
                report.referenceFrameSchema == ReferenceFrameSchema,
                "Unexpected reference-frame schema."
            );
            Require(
                report.aggregateForceStatus == AggregateForceUnavailable,
                "Aggregate force availability is ambiguous."
            );
            Require(
                report.samples != null && report.firstAcceptedBatch != null && report.firstAcceptedLinks != null,
                "Shadow arrays are missing."
            );
            Require(report.firstAcceptedUnmappedJoints >= 0, "Unmapped joint count is negative.");
            Require(report.firstAcceptedLinks.Length <= MaximumCapturedLinks, "Structural link count exceeds its bound.");
            Require(
                report.maxBodies > 0 && report.maxBodies <= MaximumCapturedBodies,
                "Shadow body bound is invalid."
            );
            Require(
                report.firstAcceptedBatch.Length <= report.maxBodies,
                "Physical snapshot exceeds its declared body bound."
            );
            Require(
                report.compared >= 0
                    && report.comparisonSkipped >= 0
                    && report.compared + report.comparisonSkipped <= report.accepted,
                "Shadow comparison counts contradict accepted work."
            );

            bool linkedAcceptedSample = false;
            int compared = 0,
                skipped = 0;
            for (int i = 0; i < report.samples.Length; i++)
            {
                ShadowSample sample = report.samples[i];
                Require(sample != null, "Shadow sample is missing.");
                Require(
                    sample.referenceFrame == UnityWorldReferenceFrame,
                    "Shadow sample reference frame is unsupported."
                );
                Vector(sample.rawKrakensbaneFrameVelocity, 3, "Krakensbane frame velocity");
                Require(
                    sample.physicsEpoch >= 0 && sample.floatingOriginEventCount >= 0,
                    "Frame counters must be nonnegative."
                );
                Require(
                    Finite(sample.captureFixedTimeSeconds),
                    "Capture fixed time must be finite."
                );
                if (sample.observedComparisonAvailable)
                {
                    Vector(
                        sample.comparisonRawKrakensbaneFrameVelocity,
                        3,
                        "comparison Krakensbane frame velocity"
                    );
                    Require(
                        sample.status == "accepted" && sample.comparisonStatus == "compared",
                        "Observed comparison is not linked to accepted work."
                    );
                    Require(
                        sample.comparedBodies == sample.bodies && sample.comparedBodies > 0,
                        "Observed comparison body count is incomplete."
                    );
                    Require(
                        sample.comparisonPhysicsEpoch == sample.physicsEpoch + 1,
                        "Observed comparison is not from the next physics boundary."
                    );
                    Require(
                        sample.comparisonFloatingOriginEventCount
                            >= sample.floatingOriginEventCount,
                        "Observed comparison origin counter moved backward."
                    );
                    Require(
                        Finite(sample.comparisonFixedTimeSeconds)
                            && Finite(sample.observedDeltaSeconds)
                            && sample.observedDeltaSeconds > 0,
                        "Observed comparison time is invalid."
                    );
                    Require(
                        Finite(sample.observedPositionMaxMeters)
                            && sample.observedPositionMaxMeters >= 0
                            && Finite(sample.observedPositionRmsMeters)
                            && sample.observedPositionRmsMeters >= 0
                            && sample.observedPositionRmsMeters <= sample.observedPositionMaxMeters,
                        "Observed position residual is invalid."
                    );
                    Require(
                        Finite(sample.observedVelocityMaxMetersPerSecond)
                            && sample.observedVelocityMaxMetersPerSecond >= 0
                            && Finite(sample.observedVelocityRmsMetersPerSecond)
                            && sample.observedVelocityRmsMetersPerSecond >= 0
                            && sample.observedVelocityRmsMetersPerSecond
                                <= sample.observedVelocityMaxMetersPerSecond,
                        "Observed velocity residual is invalid."
                    );
                    compared++;
                }
                else
                {
                    Require(
                        sample.comparedBodies == 0
                            && sample.observedPositionMaxMeters == 0
                            && sample.observedPositionRmsMeters == 0
                            && sample.observedVelocityMaxMetersPerSecond == 0
                            && sample.observedVelocityRmsMetersPerSecond == 0,
                        "Unavailable comparison contains residual data."
                    );
                    if (
                        sample.comparisonStatus != null
                        && sample.comparisonStatus.StartsWith("skipped-", StringComparison.Ordinal)
                    )
                        skipped++;
                }
                if (sample.tick == report.firstAcceptedTick && sample.status == "accepted")
                    linkedAcceptedSample = true;
            }
            Require(
                compared == report.compared && skipped == report.comparisonSkipped,
                "Shadow comparison counters do not match samples."
            );

            if (report.accepted == 0)
            {
                Require(
                    report.firstAcceptedBatch.Length == 0,
                    "A physical snapshot has no accepted sample."
                );
                return;
            }
            Require(
                report.accepted > 0 && report.firstAcceptedBatch.Length != 0,
                "An accepted report is missing its physical snapshot."
            );
            Require(
                linkedAcceptedSample,
                "The physical snapshot is not linked to an accepted sample."
            );

            var ids = new HashSet<int>();
            for (int i = 0; i < report.firstAcceptedBatch.Length; i++)
            {
                ShadowBody body = report.firstAcceptedBatch[i];
                Require(body != null, "Physical body is missing.");
                Require(
                    body.id >= 0 && ids.Add(body.id),
                    "Physical body IDs must be unique and nonnegative."
                );
                Require(
                    Finite(body.mass) && body.mass > 0,
                    "Physical body mass must be positive and finite."
                );
                Require(
                    body.constraints >= 0,
                    "Rigidbody constraints bitmask must be nonnegative."
                );
                Vector(body.position, 3, "position");
                Quaternion(body.rotation, "rotation");
                Vector(body.velocity, 3, "velocity");
                Vector(body.angularVelocity, 3, "angular velocity");
                Vector(body.centerOfMass, 3, "center of mass");
                Vector(body.worldCenterOfMass, 3, "world center of mass");
                Vector(body.inertiaTensor, 3, "inertia tensor");
                for (int component = 0; component < 3; component++)
                    Require(
                        body.inertiaTensor[component] >= 0,
                        "Inertia tensor components must be nonnegative."
                    );
                Quaternion(body.inertiaTensorRotation, "inertia tensor rotation");
                Vector(body.force, 3, "synthetic force");
                Require(body.forceSource == SyntheticZeroForce, "Force provenance is unsupported.");
                Require(
                    body.force[0] == 0 && body.force[1] == 0 && body.force[2] == 0,
                    "Synthetic force must be exactly zero."
                );
                Vector(body.predictedPosition, 3, "predicted position");
                Vector(body.predictedVelocity, 3, "predicted velocity");
            }
            var jointIds = new HashSet<int>();
            for (int i = 0; i < report.firstAcceptedLinks.Length; i++)
            {
                StructuralLink link = report.firstAcceptedLinks[i];
                Require(link != null, "Structural link is missing.");
                Require(jointIds.Add(link.nativeInstanceId), "Structural joint IDs must be unique.");
                Require(
                    ids.Contains(link.bodyId) && ids.Contains(link.connectedBodyId) && link.bodyId != link.connectedBodyId,
                    "Structural link endpoints are invalid."
                );
                Require(!String.IsNullOrEmpty(link.jointType), "Structural joint type is missing.");
                Vector(link.anchor, 3, "joint anchor");
                Vector(link.connectedAnchor, 3, "connected joint anchor");
                Vector(link.axis, 3, "joint axis");
                Vector(link.secondaryAxis, 3, "joint secondary axis");
                Require(Threshold(link.breakForce, link.breakForceStatus), "Joint break force is invalid.");
                Require(Threshold(link.breakTorque, link.breakTorqueStatus), "Joint break torque is invalid.");
                Require(Finite(link.massScale) && link.massScale >= 0 && Finite(link.connectedMassScale) && link.connectedMassScale >= 0,
                    "Joint mass scales are invalid.");
            }
        }

        static void Quaternion(double[] value, string name)
        {
            Vector(value, 4, name);
            double norm =
                value[0] * value[0]
                + value[1] * value[1]
                + value[2] * value[2]
                + value[3] * value[3];
            Require(
                Finite(norm) && Math.Abs(norm - 1) <= QuaternionNormTolerance,
                name + " must be a unit quaternion."
            );
        }

        static void Vector(double[] value, int length, string name)
        {
            Require(value != null && value.Length == length, name + " has the wrong shape.");
            for (int i = 0; i < value.Length; i++)
                Require(Finite(value[i]), name + " contains a nonfinite value.");
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static bool Threshold(double value, string status)
        {
            return status == "finite" && Finite(value) && value >= 0
                || status == "unbreakable" && value == 0;
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
