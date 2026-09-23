using System;

namespace KspContinuum
{
    [Serializable]
    public sealed class AeroSetDragSubstitutionReport
    {
        public string schema = "ksp-continuum-set-drag-substitution/v2";
        public string status = "created";
        public string reason;
        public string cleanupStatus = "not-attempted";
        public string strategy = "complete-stock-set-drag-reduction/v1";
        public int requiredWarmupMatches;
        public int requiredMeasuredMatches;
        public int maximumSubstitutions;
        public int shadowWarmupComparisons;
        public int shadowMeasuredComparisons;
        public int shadowComparisons;
        public int matchedCompleteOutputs;
        public int suppressedOriginalCalls;
        public int stockFallbacks;
        public long candidateStopwatchTicks;
        public long stockStopwatchTicks;
        public long stopwatchFrequency;
        public int patchGraphInspections;
        public double maximumRelativeError;
        public bool stockUpdateAerodynamicsRemainedAuthoritative = true;
        public bool stockForceApplicationRemainedAuthoritative = true;
        public bool unityIntegrationRemainedAuthoritative = true;

        public bool RecordCleanup(bool registered, bool removed)
        {
            if (cleanupStatus != "not-attempted") return cleanupStatus != "failed";
            if (!registered) { cleanupStatus = "not-registered"; return true; }
            if (removed) { cleanupStatus = "removed-owned-patches"; return true; }
            cleanupStatus = "failed"; status = "abstained"; reason = "cleanup-failed"; return false;
        }
    }
}
