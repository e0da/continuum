using System;

namespace KspContinuum
{
    [Serializable]
    public sealed class AeroForceSubstitutionReport
    {
        public string schema = "ksp-continuum-aero-force-substitution/v1";
        public string status = "created";
        public string reason;
        public string strategy = "stock-computed-body-drag-publication/v1";
        public int maximumPublications;
        public int attemptedPublications;
        public int substitutedPublications;
        public int stockFallbacks;
        public bool stockLiftRemainedAuthoritative = true;
        public bool unityIntegrationRemainedAuthoritative = true;
    }

    public sealed class AeroForceSubstitution
    {
        readonly AeroForceSubstitutionReport report;
        bool active;

        public AeroForceSubstitution(AeroForceSubstitutionReport report, int maximumPublications)
        {
            if (report == null) throw new ArgumentNullException("report");
            if (maximumPublications < 1 || maximumPublications > 4096)
                throw new ArgumentOutOfRangeException("maximumPublications");
            this.report = report;
            report.maximumPublications = maximumPublications;
        }

        public void Start()
        {
            if (active || report.status != "created") throw new InvalidOperationException("Substitution is single-use.");
            active = true; report.status = "active";
        }

        public bool TryBeginPublication(bool eligible, bool finiteInputs)
        {
            if (!active) return false;
            report.attemptedPublications++;
            if (!eligible || !finiteInputs)
            {
                report.stockFallbacks++;
                Stop(!eligible ? "ineligible-live-context" : "nonfinite-stock-publication");
                return false;
            }
            return true;
        }

        public void Published()
        {
            if (!active) throw new InvalidOperationException("Substitution is not active.");
            report.substitutedPublications++;
            if (report.substitutedPublications >= report.maximumPublications) Stop("bounded-publication-limit-reached");
        }

        public void PublicationFailed(string failure)
        {
            if (!active) return;
            report.stockFallbacks++;
            Stop("publication-failed:" + (string.IsNullOrEmpty(failure) ? "unknown" : failure));
        }

        public void Stop(string reason)
        {
            if (!active) return;
            active = false; report.reason = reason;
            report.status = reason == "bounded-publication-limit-reached" ? "complete" : "abstained";
        }

        public bool IsActive { get { return active; } }
    }
}
