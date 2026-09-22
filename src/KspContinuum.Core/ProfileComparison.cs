using System;
using System.Collections.Generic;

namespace KspContinuum
{
    [Serializable] public sealed class ProfileComparison
    {
        public string schema = "ksp-continuum-profile-comparison/v1";
        public string status = "comparable";
        public string substitutionId, substitutionStatus;
        public int stockFrames, candidateFrames;
        public ProfileDelta wallIntervals;
        public ProfileDelta fixedUpdate, physicsFixedUpdate, behaviourFixedUpdate;
    }
    [Serializable] public sealed class ProfileDelta
    {
        public double stockMeanMilliseconds, candidateMeanMilliseconds, meanDeltaMilliseconds, meanSpeedupPercent;
        public double stockP95Milliseconds, candidateP95Milliseconds, p95DeltaMilliseconds, p95SpeedupPercent;
    }
    [Serializable] public sealed class ProfileComparisonExpectation
    {
        public string substitutionId, substitutionStatus;
        public int stockRigidbodies, stockJoints, candidateRigidbodies, candidateJoints;
    }
    public static class ProfileComparisonSummary
    {
        static readonly string Fixed = "UnityEngine.PlayerLoop.FixedUpdate";
        static readonly string Physics = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        static readonly string Behaviours = "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate";

        public static ProfileComparison Compare(ProbeReport stock, ProbeReport candidate, ProfileComparisonExpectation expectation)
        {
            if (expectation == null || string.IsNullOrEmpty(expectation.substitutionId) || expectation.substitutionStatus != "verified" ||
                expectation.stockRigidbodies < 0 || expectation.stockJoints < 0 || expectation.candidateRigidbodies < 0 || expectation.candidateJoints < 0)
                throw new ArgumentException("A verified structural substitution contract is required.");
            Validate(stock, "stock"); Validate(candidate, "candidate");
            if (stock.unity != candidate.unity || stock.ksp != candidate.ksp || stock.platform != candidate.platform ||
                stock.processor != candidate.processor || stock.processorCount != candidate.processorCount ||
                stock.graphicsDevice != candidate.graphicsDevice || stock.targetFrameRate != candidate.targetFrameRate || stock.vSyncCount != candidate.vSyncCount)
                throw new ArgumentException("Profile environments differ.");
            if (stock.completedFrames != candidate.completedFrames) throw new ArgumentException("Profile frame counts differ.");
            for (int i = 0; i < stock.completedFrames; i++) RequireSameContext(stock.frames[i], candidate.frames[i], expectation, i);
            return new ProfileComparison {
                substitutionId = expectation.substitutionId, substitutionStatus = expectation.substitutionStatus,
                stockFrames = stock.completedFrames, candidateFrames = candidate.completedFrames,
                wallIntervals = Delta(stock.wallIntervals, candidate.wallIntervals),
                fixedUpdate = Delta(Scope(stock, Fixed), Scope(candidate, Fixed)),
                physicsFixedUpdate = Delta(Scope(stock, Physics), Scope(candidate, Physics)),
                behaviourFixedUpdate = Delta(Scope(stock, Behaviours), Scope(candidate, Behaviours))
            };
        }

        static void Validate(ProbeReport report, string label)
        {
            if (report == null || report.status != "complete" || report.frames == null || report.completedFrames < 1 ||
                report.frames.Length != report.completedFrames || report.wallIntervals == null || report.playerLoop == null ||
                report.playerLoop.status != "observed" || report.playerLoop.integrityStatus != "verified-at-boundaries" ||
                report.playerLoop.cleanupStatus != "removed-owned-hooks" || report.playerLoop.scopes == null)
                throw new ArgumentException("Incomplete or invalid " + label + " profile.");
            if (report.contextMisalignedFrames != 0) throw new ArgumentException("Misaligned " + label + " profile context.");
        }

        static ProfileDistribution Scope(ProbeReport report, string name)
        {
            LoopTimingScope found = null;
            foreach (LoopTimingScope scope in report.playerLoop.scopes)
                if (scope != null && scope.name == name) { if (found != null) throw new ArgumentException("Duplicate timing scope: " + name); found = scope; }
            if (found == null || found.status != "observed" || found.milliseconds == null || found.sequenceErrors != 0 || found.droppedSamples != 0)
                throw new ArgumentException("Unqualified timing scope: " + name);
            return found.milliseconds;
        }

        static void RequireSameContext(ProfileFrame stock, ProfileFrame candidate, ProfileComparisonExpectation expectation, int index)
        {
            if (stock == null || candidate == null || stock.scene != candidate.scene || stock.body != candidate.body ||
                stock.situation != candidate.situation || stock.vesselId != candidate.vesselId || stock.parts != candidate.parts ||
                stock.rigidbodies != expectation.stockRigidbodies || stock.joints != expectation.stockJoints ||
                candidate.rigidbodies != expectation.candidateRigidbodies || candidate.joints != expectation.candidateJoints || stock.colliders != candidate.colliders ||
                stock.loadedVessels != candidate.loadedVessels || stock.screenWidth != candidate.screenWidth || stock.screenHeight != candidate.screenHeight ||
                stock.fixedDeltaSeconds != candidate.fixedDeltaSeconds || stock.timeScale != candidate.timeScale || stock.warpRate != candidate.warpRate ||
                stock.throttleCommand != candidate.throttleCommand || stock.packed != candidate.packed || stock.loaded != candidate.loaded || stock.paused != candidate.paused)
                throw new ArgumentException("Profile context differs at frame " + index + ".");
        }

        static ProfileDelta Delta(ProfileDistribution stock, ProfileDistribution candidate)
        {
            if (stock == null || candidate == null || stock.count < 1 || candidate.count < 1 || stock.mean <= 0 || stock.p95 <= 0)
                throw new ArgumentException("Invalid comparison distribution.");
            return new ProfileDelta {
                stockMeanMilliseconds = stock.mean, candidateMeanMilliseconds = candidate.mean,
                meanDeltaMilliseconds = candidate.mean - stock.mean, meanSpeedupPercent = (stock.mean - candidate.mean) * 100 / stock.mean,
                stockP95Milliseconds = stock.p95, candidateP95Milliseconds = candidate.p95,
                p95DeltaMilliseconds = candidate.p95 - stock.p95, p95SpeedupPercent = (stock.p95 - candidate.p95) * 100 / stock.p95
            };
        }
    }
}
