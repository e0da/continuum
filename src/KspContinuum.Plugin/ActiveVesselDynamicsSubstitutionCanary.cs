using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ActiveVesselDynamicsSubstitutionCanary : IDisposable, IPlayerLoopBracketObserver
    {
        const string Scope = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        const float PositionTolerance = 1e-4f;
        const float VelocityTolerance = 1e-5f;
        const float RotationToleranceDegrees = 1e-4f;
        readonly ActiveVesselWriterCensus source;
        readonly PhysicsDynamicsCanary state;
        readonly WriterCensus census;
        PhysicsBoundarySubstitution substitution;
        WriterCensusSnapshot before;
        bool disposed, finished;

        sealed class BodyState
        {
            public Rigidbody Body;
            public Vector3 Position, Velocity, AngularVelocity, CenterOfMass;
            public Quaternion Rotation, InertiaRotation;
        }

        public PhysicsDynamicsCanaryReport Report { get; private set; }

        public ActiveVesselDynamicsSubstitutionCanary(ActiveVesselWriterCensus source)
        {
            this.source = source ?? throw new ArgumentNullException("source");
            Report = new PhysicsDynamicsCanaryReport(); state = new PhysicsDynamicsCanary(Report);
            census = new WriterCensus(source.Snapshot, 1);
        }

        public static bool RequestedAndQualified(string[] arguments, out string reason)
        {
            reason = null;
            if (Array.IndexOf(arguments, "--continuum-live-dynamics-canary") < 0) return false;
            if (Array.IndexOf(arguments, "--continuum-live-substitution-canary") >= 0)
                reason = "Empty and dynamics substitution canaries are mutually exclusive.";
            else if (Array.IndexOf(arguments, "--continuum-survey") < 0 ||
                Array.IndexOf(arguments, "--continuum-scale-profile") < 0 ||
                Array.IndexOf(arguments, "--continuum-writer-census") < 0 ||
                !HasValue(arguments, "--continuum-checkpoint-save") ||
                !HasValue(arguments, "--continuum-checkpoint") ||
                !HasValue(arguments, "--continuum-checkpoint-sha256"))
                reason = "Dynamics canary requires survey, checkpoint, scale-profile, and writer-census qualification flags.";
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
                    attempted.Dispose(); state.Restored(attempted.CleanupStatus);
                }
            }
        }

        void CandidateTick()
        {
            long callbackStart = Stopwatch.GetTimestamp();
            try
            {
                if (!Eligible()) { state.Fail("qualified-lifecycle-changed-before-callback"); return; }
                if (!state.CandidateCallback(Time.frameCount) || !state.CandidateContext(source.Snapshot())) return;
                Advance(callbackStart);
            }
            catch (Exception error) { state.Fail("candidate-callback-failed:" + error.GetType().Name); }
            finally { Restore(); }
        }

        void Advance(long callbackStart)
        {
            List<BodyState> bodies = CaptureBodies();
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null) throw new InvalidOperationException("Central body unavailable.");
            double step = Time.fixedDeltaTime;
            var input = new RigidBody6Dof[bodies.Count];
            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody body = bodies[i].Body;
                Quaternion principal = body.rotation * body.inertiaTensorRotation;
                input[i] = new RigidBody6Dof(i, body.mass, V(body.worldCenterOfMass), R(principal),
                    V(body.velocity), V(body.angularVelocity), V(body.inertiaTensor));
            }

            long computeStart = Stopwatch.GetTimestamp();
            RigidCluster6Dof cluster = RigidCluster6Dof.Capture(input);
            Vector3d gravity = FlightGlobals.getGeeForceAtPosition(D3(cluster.CenterOfMass), vessel.mainBody);
            Vec acceleration = new Vec(gravity.x, gravity.y, gravity.z);
            IReadOnlyList<RigidPose6Dof> poses = cluster.AdvanceFrozenAcceleration(acceleration, step).Reconstruct();
            double computeMilliseconds = Milliseconds(computeStart);

            long publicationStart = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    BodyState saved = bodies[i]; RigidPose6Dof pose = poses[i];
                    Quaternion principal = Q(pose.Orientation);
                    Quaternion rotation = principal * Quaternion.Inverse(saved.InertiaRotation);
                    Vector3 center = V3(pose.Position);
                    saved.Body.position = center - rotation * saved.CenterOfMass;
                    saved.Body.rotation = rotation;
                    saved.Body.velocity = V3(pose.LinearVelocity);
                    saved.Body.angularVelocity = V3(pose.AngularVelocity);
                }
                Physics.SyncTransforms();
                double positionError = 0, velocityError = 0, rotationError = 0, angularError = 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    Rigidbody body = bodies[i].Body; RigidPose6Dof pose = poses[i];
                    Quaternion expectedRotation = Q(pose.Orientation) * Quaternion.Inverse(bodies[i].InertiaRotation);
                    positionError = Math.Max(positionError, Vector3.Distance(body.worldCenterOfMass, V3(pose.Position)));
                    velocityError = Math.Max(velocityError, Vector3.Distance(body.velocity, V3(pose.LinearVelocity)));
                    rotationError = Math.Max(rotationError, Quaternion.Angle(body.rotation, expectedRotation));
                    angularError = Math.Max(angularError, Vector3.Distance(body.angularVelocity, V3(pose.AngularVelocity)));
                }
                double publicationMilliseconds = Milliseconds(publicationStart);
                if (positionError > PositionTolerance || velocityError > VelocityTolerance ||
                    rotationError > RotationToleranceDegrees || angularError > VelocityTolerance)
                {
                    bool restored = RestoreBodies(bodies);
                    state.PublicationFailed(restored ? "readback-mismatch-restored" : "indeterminate",
                        restored ? "dynamics-readback-mismatch" : "dynamics-readback-indeterminate");
                    return;
                }
                state.Published(bodies.Count, step, acceleration, computeMilliseconds, publicationMilliseconds,
                    Milliseconds(callbackStart), positionError, velocityError, rotationError, angularError);
            }
            catch
            {
                bool restored = RestoreBodies(bodies);
                state.PublicationFailed(restored ? "aborted-restored" : "indeterminate",
                    restored ? "dynamics-publication-aborted" : "dynamics-publication-indeterminate");
                throw;
            }
        }

        List<BodyState> CaptureBodies()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.parts == null) throw new InvalidOperationException("Active vessel bodies unavailable.");
            var representatives = new Dictionary<Rigidbody, Part>();
            foreach (Part part in vessel.parts)
            {
                Rigidbody body = Body(part);
                if (body == null) throw new InvalidOperationException("Part physical owner unavailable.");
                Part current;
                if (!representatives.TryGetValue(body, out current) || part.flightID < current.flightID) representatives[body] = part;
            }
            var parts = new List<Part>(representatives.Values);
            parts.Sort((a, b) => a.flightID.CompareTo(b.flightID));
            var result = new List<BodyState>(parts.Count);
            foreach (Part part in parts)
            {
                Rigidbody body = Body(part);
                if (!(body.mass > 0) || body.isKinematic) throw new InvalidOperationException("Dynamics body is not a positive-mass dynamic Rigidbody.");
                result.Add(new BodyState { Body = body, Position = body.position, Rotation = body.rotation,
                    Velocity = body.velocity, AngularVelocity = body.angularVelocity,
                    CenterOfMass = body.centerOfMass, InertiaRotation = body.inertiaTensorRotation });
            }
            if (result.Count == 0) throw new InvalidOperationException("No dynamics bodies available.");
            return result;
        }

        static bool RestoreBodies(List<BodyState> bodies)
        {
            try
            {
                foreach (BodyState saved in bodies)
                {
                    saved.Body.position = saved.Position; saved.Body.rotation = saved.Rotation;
                    saved.Body.velocity = saved.Velocity; saved.Body.angularVelocity = saved.AngularVelocity;
                }
                Physics.SyncTransforms();
                foreach (BodyState saved in bodies)
                    if (!Same(saved.Body.position, saved.Position) || !Same(saved.Body.rotation, saved.Rotation) ||
                        !Same(saved.Body.velocity, saved.Velocity) || !Same(saved.Body.angularVelocity, saved.AngularVelocity)) return false;
                return true;
            }
            catch { return false; }
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
                if (arguments[i] == name && i + 1 < arguments.Length &&
                    !arguments[i + 1].StartsWith("--", StringComparison.Ordinal)) return true;
            return false;
        }

        static bool Eligible()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            return vessel != null && vessel.loaded && !vessel.packed && !vessel.HoldPhysics && !FlightDriver.Pause &&
                TimeWarp.CurrentRate == 1 && Time.timeScale == 1 && vessel.situation == Vessel.Situations.ORBITING &&
                vessel.ctrlState != null && vessel.ctrlState.mainThrottle < 0.01;
        }

        static Rigidbody Body(Part part)
        { return part == null ? null : part.rb != null ? part.rb : part.RigidBodyPart == null ? null : part.RigidBodyPart.rb; }
        static Vec V(Vector3 value) { return new Vec(value.x, value.y, value.z); }
        static Vector3 V3(Vec value) { return new Vector3((float)value.X, (float)value.Y, (float)value.Z); }
        static Vector3d D3(Vec value) { return new Vector3d(value.X, value.Y, value.Z); }
        static Rotation R(Quaternion value) { return new Rotation(value.x, value.y, value.z, value.w); }
        static Quaternion Q(Rotation value) { return new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W); }
        static bool Same(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        static bool Same(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }
        static double Milliseconds(long start) { return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency; }
    }
}
