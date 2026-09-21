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
        const double QuaternionNormTolerance = .001;

        public static void Validate(ShadowReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            Require(report.schema == "ksp-continuum-flight-shadow/v1", "Unexpected shadow report schema.");
            Require(report.physicalInputSchema == PhysicalInputSchema, "Unexpected physical input schema.");
            Require(report.referenceFrameSchema == ReferenceFrameSchema, "Unexpected reference-frame schema.");
            Require(report.aggregateForceStatus == AggregateForceUnavailable, "Aggregate force availability is ambiguous.");
            Require(report.samples != null && report.firstAcceptedBatch != null, "Shadow arrays are missing.");
            Require(report.maxBodies > 0 && report.maxBodies <= MaximumCapturedBodies, "Shadow body bound is invalid.");
            Require(report.firstAcceptedBatch.Length <= report.maxBodies, "Physical snapshot exceeds its declared body bound.");

            bool linkedAcceptedSample = false;
            for (int i = 0; i < report.samples.Length; i++)
            {
                ShadowSample sample = report.samples[i];
                Require(sample != null, "Shadow sample is missing.");
                Require(sample.referenceFrame == UnityWorldReferenceFrame, "Shadow sample reference frame is unsupported.");
                Vector(sample.rawKrakensbaneFrameVelocity, 3, "Krakensbane frame velocity");
                Require(sample.physicsEpoch >= 0 && sample.floatingOriginEventCount >= 0, "Frame counters must be nonnegative.");
                if (sample.tick == report.firstAcceptedTick && sample.status == "accepted") linkedAcceptedSample = true;
            }

            if (report.accepted == 0)
            {
                Require(report.firstAcceptedBatch.Length == 0, "A physical snapshot has no accepted sample.");
                return;
            }
            Require(report.accepted > 0 && report.firstAcceptedBatch.Length != 0, "An accepted report is missing its physical snapshot.");
            Require(linkedAcceptedSample, "The physical snapshot is not linked to an accepted sample.");

            var ids = new HashSet<int>();
            for (int i = 0; i < report.firstAcceptedBatch.Length; i++)
            {
                ShadowBody body = report.firstAcceptedBatch[i];
                Require(body != null, "Physical body is missing.");
                Require(body.id >= 0 && ids.Add(body.id), "Physical body IDs must be unique and nonnegative.");
                Require(Finite(body.mass) && body.mass > 0, "Physical body mass must be positive and finite.");
                Require(body.constraints >= 0, "Rigidbody constraints bitmask must be nonnegative.");
                Vector(body.position, 3, "position");
                Quaternion(body.rotation, "rotation");
                Vector(body.velocity, 3, "velocity");
                Vector(body.angularVelocity, 3, "angular velocity");
                Vector(body.centerOfMass, 3, "center of mass");
                Vector(body.worldCenterOfMass, 3, "world center of mass");
                Vector(body.inertiaTensor, 3, "inertia tensor");
                for (int component = 0; component < 3; component++)
                    Require(body.inertiaTensor[component] >= 0, "Inertia tensor components must be nonnegative.");
                Quaternion(body.inertiaTensorRotation, "inertia tensor rotation");
                Vector(body.force, 3, "synthetic force");
                Require(body.forceSource == SyntheticZeroForce, "Force provenance is unsupported.");
                Require(body.force[0] == 0 && body.force[1] == 0 && body.force[2] == 0, "Synthetic force must be exactly zero.");
                Vector(body.predictedPosition, 3, "predicted position");
                Vector(body.predictedVelocity, 3, "predicted velocity");
            }
        }

        static void Quaternion(double[] value, string name)
        {
            Vector(value, 4, name);
            double norm = value[0] * value[0] + value[1] * value[1] + value[2] * value[2] + value[3] * value[3];
            Require(Finite(norm) && Math.Abs(norm - 1) <= QuaternionNormTolerance, name + " must be a unit quaternion.");
        }

        static void Vector(double[] value, int length, string name)
        {
            Require(value != null && value.Length == length, name + " has the wrong shape.");
            for (int i = 0; i < value.Length; i++) Require(Finite(value[i]), name + " contains a nonfinite value.");
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
