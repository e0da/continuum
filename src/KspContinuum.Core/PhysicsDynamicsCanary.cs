using System;

namespace KspContinuum
{
    public sealed class PhysicsDynamicsCanary
    {
        readonly PhysicsDynamicsCanaryReport report;
        WriterCensusSnapshot admitted, enteredSnapshot;
        bool installed, entered, callback, observed, restored;

        public PhysicsDynamicsCanary(PhysicsDynamicsCanaryReport report)
        { this.report = report ?? throw new ArgumentNullException("report"); }

        public void Admit(WriterCensusSnapshot snapshot)
        {
            if (admitted != null) throw new InvalidOperationException("Dynamics admission is single-use.");
            RequireSnapshot(snapshot); admitted = snapshot;
            report.vesselId = snapshot.vesselId; report.topologyKey = snapshot.topologyKey;
            report.originGeneration = snapshot.originGeneration; report.frameVelocity = snapshot.frameVelocity;
            report.status = "admitted";
        }

        public void Installed()
        {
            if (admitted == null || installed) throw new InvalidOperationException("Dynamics canary was not admitted or was already installed.");
            installed = true; report.installationStatus = "candidate-installed"; report.status = "installed";
        }

        public bool Enter(WriterCensusSnapshot snapshot)
        {
            if (!installed || entered || restored) return Reject("unexpected-physics-bracket-entry");
            entered = true;
            if (!SameMembership(admitted, snapshot)) return Reject("admitted-membership-changed-before-bracket");
            enteredSnapshot = snapshot;
            report.status = "entered-substituted-physics-bracket"; return true;
        }

        public bool CandidateCallback(int frame)
        {
            if (!entered || frame < 0) return Reject("unexpected-candidate-callback");
            if (callback)
            {
                if (!restored || frame != report.candidateFrame || report.candidateCallbacks >= 4)
                    return Reject("candidate-callback-outside-bounded-frame");
                report.candidateCallbacks++; return true;
            }
            if (observed) return Reject("unexpected-candidate-callback");
            callback = true; report.candidateFrame = frame; report.candidateCallbacks = 1;
            report.status = "running-bounded-dynamics"; return true;
        }

        public bool CandidateContext(WriterCensusSnapshot snapshot)
        {
            if (!callback || !SameContext(enteredSnapshot, snapshot)) return Reject("candidate-context-changed-before-publication");
            return true;
        }

        public void Published(int bodies, double step, Vec acceleration, double computeMilliseconds,
            double publicationMilliseconds, double callbackMilliseconds, double positionError, double velocityError,
            double rotationErrorDegrees = 0, double angularVelocityError = 0)
        {
            if (!callback || bodies <= 0 || step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
                throw new InvalidOperationException("Invalid dynamics publication receipt.");
            report.bodiesWritten = Math.Max(report.bodiesWritten, bodies);
            report.stepSeconds = step; report.acceleration = acceleration;
            report.computeMilliseconds += computeMilliseconds;
            report.publicationMilliseconds += publicationMilliseconds;
            report.callbackMilliseconds += callbackMilliseconds;
            report.maxPositionReadbackErrorMeters = Math.Max(report.maxPositionReadbackErrorMeters, positionError);
            report.maxVelocityReadbackErrorMetersPerSecond = Math.Max(report.maxVelocityReadbackErrorMetersPerSecond, velocityError);
            report.maxRotationReadbackErrorDegrees = Math.Max(report.maxRotationReadbackErrorDegrees, rotationErrorDegrees);
            report.maxAngularVelocityReadbackErrorRadiansPerSecond = Math.Max(
                report.maxAngularVelocityReadbackErrorRadiansPerSecond, angularVelocityError);
            report.publicationStatus = "verified-readback";
        }

        public bool Observe(WriterCensusSnapshot before, WriterCensusSnapshot after, WriterCensusReport interval)
        {
            report.before = before; report.after = after; report.substitutedInterval = interval;
            if (!entered || !callback || observed) return Reject("unexpected-post-bracket-observation");
            if (!SameContext(before, after)) return Reject("context-changed-across-physics-bracket");
            if (report.publicationStatus != "verified-readback") return Reject("dynamics-publication-unverified");
            if (interval == null || interval.status != "observed" || interval.invalidIntervals != 0 ||
                interval.intervals == null || interval.intervals.Length != 1 || interval.intervals[0].status != "observed")
                return Reject("substituted-interval-census-invalid");
            WriterCensusInterval value = interval.intervals[0];
            if (value.changedPositions == 0 && value.changedOrientations == 0 && value.changedVelocities == 0 &&
                value.changedAngularVelocities == 0 && value.changedInternalPositions == 0 &&
                value.changedInternalOrientations == 0 && value.changedInternalVelocities == 0 &&
                value.changedInternalAngularVelocities == 0)
                return Reject("candidate-produced-no-observable-dynamics");
            observed = true; report.status = "observed-pending-restoration"; CompleteIfReady();
            return report.status != "invalid";
        }

        public void Restored(string cleanupStatus)
        {
            if (restored) throw new InvalidOperationException("Dynamics restoration was already recorded.");
            restored = true; report.restorationStatus = cleanupStatus;
            if (cleanupStatus != "native-node-restored") Reject("native-node-restoration-failed");
            else CompleteIfReady();
        }

        public void PublicationFailed(string status, string reason)
        {
            report.publicationStatus = status; Reject(reason);
        }

        public void Fail(string reason) { Reject(reason); }

        void CompleteIfReady()
        {
            if (report.status == "invalid") return;
            if (observed && restored) report.status = "observed-bounded-dynamics";
        }

        bool Reject(string reason) { report.status = "invalid"; if (report.reason == null) report.reason = reason; return false; }

        static void RequireSnapshot(WriterCensusSnapshot value)
        {
            if (value == null || string.IsNullOrEmpty(value.vesselId) || string.IsNullOrEmpty(value.topologyKey) ||
                value.bodies == null || value.bodies.Length == 0) throw new ArgumentException("Dynamics snapshot is incomplete.");
        }

        static bool SameContext(WriterCensusSnapshot a, WriterCensusSnapshot b)
        {
            return SameMembership(a, b) && a.originGeneration == b.originGeneration && Same(a.frameVelocity, b.frameVelocity);
        }

        static bool SameMembership(WriterCensusSnapshot a, WriterCensusSnapshot b)
        {
            if (a == null || b == null || a.vesselId != b.vesselId || a.topologyKey != b.topologyKey ||
                a.bodies == null || b.bodies == null || a.bodies.Length != b.bodies.Length) return false;
            for (int i = 0; i < a.bodies.Length; i++) if (a.bodies[i].id != b.bodies[i].id) return false;
            return true;
        }

        static bool Same(Vec a, Vec b) { return a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
    }
}
