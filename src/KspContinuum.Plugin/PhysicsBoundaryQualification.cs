using System;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class PhysicsBoundaryQualification : IDisposable
    {
        static PhysicsBoundaryQualification owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly System.Collections.Generic.List<LifecycleOrderTrial> trials = new System.Collections.Generic.List<LifecycleOrderTrial>();
        readonly PhysicsBoundaryHooks hooks;
        GameObject probeObject;
        Rigidbody probe;
        bool active, finished, disposed, valid = true;
        LifecycleOrderTrial current;
        public LifecycleOrderQualificationReport Report { get; private set; }
        public bool IsRunning { get { return active && !finished; } }

        public PhysicsBoundaryQualification()
        {
            hooks = new PhysicsBoundaryHooks(BeforePhysics, AfterPhysics);
            Report = new LifecycleOrderQualificationReport {
                unity = Application.unityVersion,
                ksp = Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision,
                plugin = typeof(PhysicsBoundaryQualification).Assembly.ManifestModule.ModuleVersionId.ToString("D")
            };
        }

        public void Start()
        {
            RequireThread();
            if (active || disposed) throw new InvalidOperationException("Physics-boundary qualification supports one installation.");
            if (owner != null) throw new InvalidOperationException("Another physics-boundary qualification owns the PlayerLoop.");
            owner = this;
            try
            {
                probeObject = new GameObject("KSP Continuum physics-boundary qualification probe");
                probeObject.hideFlags = HideFlags.HideAndDontSave;
                probe = probeObject.AddComponent<Rigidbody>();
                probe.useGravity = false; probe.drag = 0; probe.angularDrag = 0; probe.detectCollisions = false;
                probe.constraints = RigidbodyConstraints.FreezeRotation;
                hooks.Start(); active = true;
                Report.status = "running"; Report.integrityStatus = "installed-pending-observation";
                Audit();
            }
            catch (Exception error)
            {
                Invalidate("Installation unavailable: " + error.GetType().Name + ". " + error.Message);
                Dispose();
            }
        }

        void BeforePhysics()
        {
            if (!active) return;
            try
            {
                RequireThread(); Audit(); if (!active) return;
                if (current != null) throw new InvalidOperationException("Previous physics trial has no post-target observation.");
                int index = trials.Count;
                if (index >= LifecycleOrderQualificationReport.RequiredTrials) throw new InvalidOperationException("Unexpected extra physics trial.");
                Vector3 velocity = index == 0 ? new Vector3(2, 0, 0) : index == 1 ? new Vector3(0, -2, 0) : new Vector3(0, 0, 2);
                probe.position = Vector3.zero; probe.rotation = Quaternion.identity; probe.velocity = velocity; probe.angularVelocity = Vector3.zero;
                current = new LifecycleOrderTrial { trial = index + 1, unityFrameBefore = Time.frameCount,
                    fixedTimeBefore = Time.fixedTime, fixedDeltaSeconds = Time.fixedDeltaTime,
                    initialPosition = Vector(Vector3.zero), requestedVelocity = Vector(velocity),
                    positionBeforeTarget = Vector(probe.position) };
            }
            catch (Exception error) { Invalidate("Pre-target observation failed: " + error.GetType().Name); }
        }

        void AfterPhysics()
        {
            if (!active) return;
            try
            {
                RequireThread(); Audit(); if (!active) return;
                if (current == null) throw new InvalidOperationException("Post-target callback has no matching pre-target callback.");
                current.unityFrameAfter = Time.frameCount; current.fixedTimeAfter = Time.fixedTime;
                current.positionAfterTarget = Vector(probe.position); current.velocityAfterTarget = Vector(probe.velocity);
                trials.Add(current); current = null;
                if (trials.Count == LifecycleOrderQualificationReport.RequiredTrials) { active = false; finished = true; }
            }
            catch (Exception error) { Invalidate("Post-target observation failed: " + error.GetType().Name); }
        }

        public void Audit()
        {
            RequireThread();
            if (disposed || !valid) return;
            try
            {
                hooks.Audit();
                if (!hooks.IsValid) Invalidate(hooks.Detail ?? "Native physics boundary became invalid.");
                else Report.integrityStatus = "verified-at-every-boundary";
            }
            catch (Exception error) { Invalidate("Loop audit failed: " + error.GetType().Name); }
        }

        void Invalidate(string reason)
        {
            valid = false; active = false; finished = true; Report.status = "invalid";
            Report.reason = reason; Report.integrityStatus = "invalidated";
        }

        public void Dispose()
        {
            if (disposed) return;
            if (!finished && valid) Invalidate("Qualification interrupted before all trials completed.");
            if (valid) Audit();
            active = false; disposed = true;
            bool clean = true;
            try { hooks.Dispose(); clean = hooks.CleanupStatus == "removed-owned-hooks"; }
            catch { clean = false; }
            try { if (probeObject != null) UnityEngine.Object.Destroy(probeObject); }
            catch { clean = false; }
            probe = null; probeObject = null; if (owner == this) owner = null;
            if (!clean)
            {
                valid = false; Report.status = "invalid"; Report.reason = "Owned PlayerLoop hook or probe cleanup failed.";
                Report.integrityStatus = "invalidated"; Report.cleanupStatus = "cleanup-error"; ClearEvidence();
            }
            else if (valid && trials.Count == LifecycleOrderQualificationReport.RequiredTrials)
            {
                Report.installedBeforeCallbacks = 1; Report.installedAfterCallbacks = 1;
                Report.trials = trials.ToArray(); Report.retainedTrials = trials.Count;
                Report.cleanupStatus = "removed-owned-hooks-probe-destroy-requested"; Report.status = "qualified";
                Report.qualificationId = LifecycleOrderQualification.ComputeId(Report);
                try { LifecycleOrderQualification.Validate(Report); }
                catch (Exception error) { Invalidate("Captured qualification failed validation: " + error.GetType().Name); Report.cleanupStatus = "complete"; ClearEvidence(); }
            }
            else
            {
                Report.cleanupStatus = "complete"; ClearEvidence();
            }
        }

        void ClearEvidence() { Report.trials = new LifecycleOrderTrial[0]; Report.retainedTrials = 0; Report.qualificationId = null; }
        void RequireThread() { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Physics-boundary qualification changed threads."); }
        static double[] Vector(Vector3 value) { return new[] { (double)value.x, (double)value.y, (double)value.z }; }
    }
}
