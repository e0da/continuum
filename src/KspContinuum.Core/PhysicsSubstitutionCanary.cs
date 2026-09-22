using System;

namespace KspContinuum
{
    public sealed class PhysicsSubstitutionCanary
    {
        readonly PhysicsSubstitutionCanaryReport report;
        WriterCensusSnapshot admitted;
        bool installed, entered, restored;

        public PhysicsSubstitutionCanary(PhysicsSubstitutionCanaryReport report)
        { this.report = report ?? throw new ArgumentNullException("report"); }

        public void Admit(WriterCensusSnapshot snapshot)
        {
            if (admitted != null) throw new InvalidOperationException("Canary admission is single-use.");
            RequireSnapshot(snapshot); admitted = snapshot;
            report.vesselId = snapshot.vesselId; report.topologyKey = snapshot.topologyKey;
            report.originGeneration = snapshot.originGeneration; report.frameVelocity = snapshot.frameVelocity;
            report.status = "admitted";
        }

        public void Installed()
        {
            if (admitted == null || installed) throw new InvalidOperationException("Canary was not admitted or was already installed.");
            installed = true; report.installationStatus = "candidate-installed"; report.status = "installed";
        }

        public bool Enter(WriterCensusSnapshot snapshot)
        {
            if (!installed || entered || restored) return Reject("unexpected-candidate-callback");
            entered = true; report.candidateCallbacks++;
            if (!SameContext(admitted, snapshot)) return Reject("admitted-context-changed-before-callback");
            report.status = "skipping-one-native-physics-tick"; return true;
        }

        public bool Observe(WriterCensusSnapshot before, WriterCensusSnapshot after, WriterCensusReport interval)
        {
            report.before = before; report.after = after; report.skippedInterval = interval;
            if (!entered || restored) return Reject("unexpected-post-callback-observation");
            if (!SameContext(before, after) || !SameContext(admitted, after)) return Reject("context-changed-during-callback");
            if (interval == null || interval.status != "observed" || interval.invalidIntervals != 0 ||
                interval.intervals == null || interval.intervals.Length != 1 || interval.intervals[0].status != "observed")
                return Reject("skipped-interval-census-invalid");
            WriterCensusInterval value = interval.intervals[0];
            if (value.changedPositions != 0 || value.changedOrientations != 0 || value.changedVelocities != 0 || value.changedAngularVelocities != 0 ||
                value.changedInternalPositions != 0 || value.changedInternalOrientations != 0 || value.changedInternalVelocities != 0 || value.changedInternalAngularVelocities != 0)
                return Reject("state-changed-during-no-dynamics-callback");
            report.status = "observed-pending-restoration"; return true;
        }

        public void Restored(string cleanupStatus)
        {
            if (restored) throw new InvalidOperationException("Canary restoration was already recorded.");
            restored = true; report.restorationStatus = cleanupStatus;
            if (report.status == "observed-pending-restoration" && cleanupStatus == "native-node-restored")
                report.status = "observed-skipped-native-tick";
            else Reject(cleanupStatus == "native-node-restored" ? "canary-invalid-before-restoration" : "native-node-restoration-failed");
        }

        public void Fail(string reason) { Reject(reason); }

        bool Reject(string reason) { report.status = "invalid"; if (report.reason == null) report.reason = reason; return false; }
        static void RequireSnapshot(WriterCensusSnapshot value)
        {
            if (value == null || string.IsNullOrEmpty(value.vesselId) || string.IsNullOrEmpty(value.topologyKey) ||
                value.bodies == null || value.bodies.Length == 0) throw new ArgumentException("Canary snapshot is incomplete.");
        }
        static bool SameContext(WriterCensusSnapshot a, WriterCensusSnapshot b)
        {
            if (a == null || b == null || a.vesselId != b.vesselId || a.topologyKey != b.topologyKey ||
                a.originGeneration != b.originGeneration || a.bodies == null || b.bodies == null || a.bodies.Length != b.bodies.Length) return false;
            return Same(a.frameVelocity, b.frameVelocity);
        }
        static bool Same(Vec a, Vec b) { return a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
    }
}
