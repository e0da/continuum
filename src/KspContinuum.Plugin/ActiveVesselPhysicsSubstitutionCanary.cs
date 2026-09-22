using System;

namespace KspContinuum
{
    internal sealed class ActiveVesselPhysicsSubstitutionCanary : IDisposable, IPlayerLoopBracketObserver
    {
        const string Scope = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        readonly ActiveVesselWriterCensus source;
        readonly PhysicsSubstitutionCanary state;
        readonly WriterCensus census;
        PhysicsBoundarySubstitution substitution;
        WriterCensusSnapshot before;
        bool disposed, finished;
        public PhysicsSubstitutionCanaryReport Report { get; private set; }

        public ActiveVesselPhysicsSubstitutionCanary(ActiveVesselWriterCensus source)
        {
            this.source = source ?? throw new ArgumentNullException("source");
            Report = new PhysicsSubstitutionCanaryReport(); state = new PhysicsSubstitutionCanary(Report);
            census = new WriterCensus(source.Snapshot, 1);
        }

        public static bool RequestedAndQualified(string[] arguments, out string reason)
        {
            reason = null;
            if (Array.IndexOf(arguments, "--continuum-live-substitution-canary") < 0) return false;
            if (Array.IndexOf(arguments, "--continuum-survey") < 0 || Array.IndexOf(arguments, "--continuum-scale-profile") < 0 ||
                Array.IndexOf(arguments, "--continuum-writer-census") < 0 || !HasValue(arguments, "--continuum-checkpoint-save") ||
                !HasValue(arguments, "--continuum-checkpoint") || !HasValue(arguments, "--continuum-checkpoint-sha256"))
                reason = "Canary requires survey, checkpoint, scale-profile, and writer-census qualification flags.";
            return true;
        }

        public void Start()
        {
            try
            {
                if (!Eligible()) throw new InvalidOperationException("Active vessel left the qualified lifecycle state before admission.");
                state.Admit(source.Snapshot());
                substitution = new PhysicsBoundarySubstitution(CandidateTick);
                substitution.Start(); state.Installed();
            }
            catch (Exception error)
            {
                state.Fail("installation-failed:" + error.GetType().Name);
                if (substitution != null)
                {
                    PhysicsBoundarySubstitution attempted = substitution; substitution = null;
                    state.Restored(attempted.CleanupStatus);
                }
            }
        }

        void CandidateTick()
        {
            try
            {
                if (!Eligible()) { state.Fail("qualified-lifecycle-changed-before-callback"); return; }
                state.CandidateCallback();
            }
            catch (Exception error) { state.Fail("candidate-callback-failed:" + error.GetType().Name); }
            finally { Restore(); }
        }

        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Scope || finished) return;
            try
            {
                if (!Eligible()) { state.Fail("qualified-lifecycle-changed-before-bracket"); return; }
                before = source.Snapshot();
                if (state.Enter(before)) census.Before(scope, frame, fixedTimeSeconds);
            }
            catch (Exception error) { state.Fail("pre-bracket-capture-failed:" + error.GetType().Name); }
        }

        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (scope != Scope || finished) return;
            try
            {
                census.After(scope, frame, fixedTimeSeconds); census.Finish();
                state.Observe(before, source.Snapshot(), census.Report);
            }
            catch (Exception error) { state.Fail("post-bracket-capture-failed:" + error.GetType().Name); }
            finally { finished = true; }
        }

        public void Fault(string scope, Exception error)
        { if (scope == Scope && !finished) state.Fail("playerloop-bracket-failed:" + error.GetType().Name); }

        void Restore()
        {
            if (substitution == null) return;
            PhysicsBoundarySubstitution owned = substitution; substitution = null;
            try { owned.Dispose(); state.Restored(owned.CleanupStatus); }
            catch (Exception error) { state.Fail("restoration-threw:" + error.GetType().Name); }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (substitution != null) { state.Fail("disposed-before-candidate-callback"); Restore(); }
            census.Finish();
        }

        static bool HasValue(string[] arguments, string name)
        {
            for (int i = 0; i < arguments.Length; i++)
                if (arguments[i] == name && i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal)) return true;
            return false;
        }

        static bool Eligible()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            return vessel != null && vessel.loaded && !vessel.packed && !FlightDriver.Pause && TimeWarp.CurrentRate == 1 &&
                vessel.situation == Vessel.Situations.ORBITING && vessel.ctrlState != null && vessel.ctrlState.mainThrottle < 0.01;
        }
    }
}
