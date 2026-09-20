using System;

namespace KspContinuum
{
    [Serializable] public sealed class BenchReport
    {
        public string schema = "ksp-continuum-bench/v1";
        public string status = "synthetic-engine-benchmark-only";
        public string utc = DateTime.UtcNow.ToString("o");
        public string unity;
        public string ksp;
        public string plugin;
        public string platform;
        public float stepSeconds = 0.02f;
        public int warmupSteps = 50, measuredSteps = 200, solverIterations = 6, solverVelocityIterations = 1;
        public Sample[] samples;
        public float splitInitialPoseError, splitLinearMomentumError, splitAngularMomentumError;
        public bool splitInitialPosePassed, splitPassed, collisionPassed;
        public float collisionFinalY;
        public bool Passed()
        {
            if (!splitPassed || !collisionPassed || samples == null || samples.Length != 36) return false;
            foreach (var sample in samples)
                if (sample.colliderRayHits != sample.boxes || double.IsNaN(sample.millisecondsPerStep) ||
                    double.IsInfinity(sample.millisecondsPerStep) || sample.millisecondsPerStep <= 0 ||
                    float.IsNaN(sample.maxSpacingError) || float.IsInfinity(sample.maxSpacingError) ||
                    (sample.compound && sample.maxSpacingError > 1e-4f)) return false;
            return true;
        }
    }
    [Serializable] public sealed class Sample
    {
        public int boxes, bodies, joints, pair, colliderRayHits;
        public bool compound;
        public double millisecondsPerStep;
        public float maxSpacingError;
    }

    [Serializable] public sealed class VesselReport
    {
        public string schema = "ksp-continuum-vessel/v1";
        public string status = "read-only-candidate-inventory-not-merge-approval";
        public string utc = DateTime.UtcNow.ToString("o");
        public int parts, rigidbodies, joints, colliders, candidateParts;
        public bool packed;
        public PartReport[] inventory;
    }
    [Serializable] public sealed class PartReport
    {
        public string partType, exclusion;
        public int index, parentIndex;
        public float dryMass;
    }
    [Serializable] public sealed class MarkerReport
    {
        public string name, status;
        public long[] nanoseconds;
        public int[] blocks;
    }
    [Serializable] public sealed class ProbeReport
    {
        public string schema = "ksp-continuum-markers/v1";
        public string status = "marker-timings-not-whole-frame-attribution";
        public string utc = DateTime.UtcNow.ToString("o");
        public MarkerReport[] markers;
    }
}
