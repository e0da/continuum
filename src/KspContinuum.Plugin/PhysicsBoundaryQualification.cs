using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class PhysicsBoundaryQualification : IDisposable
    {
        sealed class BeforePhysicsHook { }
        sealed class AfterPhysicsHook { }
        static PhysicsBoundaryQualification owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly PlayerLoopSystem.UpdateFunction before;
        readonly PlayerLoopSystem.UpdateFunction after;
        readonly List<LifecycleOrderTrial> trials = new List<LifecycleOrderTrial>();
        PlayerLoopSystem original;
        PlayerLoopSystem target;
        GameObject probeObject;
        Rigidbody probe;
        bool installed, active, finished, disposed, valid = true;
        LifecycleOrderTrial current;
        public LifecycleOrderQualificationReport Report { get; private set; }
        public bool IsRunning { get { return active && !finished; } }

        public PhysicsBoundaryQualification()
        {
            before = BeforePhysics;
            after = AfterPhysics;
            Report = new LifecycleOrderQualificationReport {
                unity = Application.unityVersion,
                ksp = Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision,
                plugin = typeof(PhysicsBoundaryQualification).Assembly.ManifestModule.ModuleVersionId.ToString("D")
            };
        }

        public void Start()
        {
            RequireThread();
            if (installed || disposed) throw new InvalidOperationException("Physics-boundary qualification supports one installation.");
            if (owner != null) throw new InvalidOperationException("Another physics-boundary qualification owns the PlayerLoop.");
            owner = this;
            try
            {
                original = Clone(PlayerLoop.GetCurrentPlayerLoop());
                if (Count(original, n => n.type == typeof(FixedLoop.PhysicsFixedUpdate)) != 1)
                    throw new InvalidOperationException("Expected one native physics target.");
                var parent = FindDirectParent(original, typeof(FixedLoop.PhysicsFixedUpdate));
                if (parent.type != typeof(FixedLoop)) throw new InvalidOperationException("Native physics target has an unexpected parent.");
                probeObject = new GameObject("KSP Continuum physics-boundary qualification probe");
                probeObject.hideFlags = HideFlags.HideAndDontSave;
                probe = probeObject.AddComponent<Rigidbody>();
                probe.useGravity = false; probe.drag = 0; probe.angularDrag = 0; probe.detectCollisions = false;
                probe.constraints = RigidbodyConstraints.FreezeRotation;
                var modified = Insert(original); target = Clone(Find(modified, typeof(FixedLoop.PhysicsFixedUpdate)));
                installed = true; PlayerLoop.SetPlayerLoop(modified); active = true;
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
            if (!installed || disposed || !valid) return;
            try
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                if (!PreservesOriginal(loop, original) || CountCallbacks(loop, before) != 1 || CountCallbacks(loop, after) != 1
                    || !Intact(loop)) Invalidate("Native physics target or owned bracket changed, moved, duplicated or removed.");
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
            if (installed && valid) Audit();
            active = false; disposed = true;
            bool clean = true;
            if (installed)
            {
                try
                {
                    int removed = 0; var latest = Strip(PlayerLoop.GetCurrentPlayerLoop(), ref removed);
                    if (removed > 0) PlayerLoop.SetPlayerLoop(latest);
                    var verified = PlayerLoop.GetCurrentPlayerLoop();
                    clean = CountCallbacks(verified, before) == 0 && CountCallbacks(verified, after) == 0;
                }
                catch { clean = false; }
            }
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
        bool Intact(PlayerLoopSystem root)
        {
            var parent = FindDirectParent(root, typeof(FixedLoop.PhysicsFixedUpdate)); var children = parent.subSystemList;
            for (int i = 1; i + 1 < children.Length; i++) if (children[i].type == typeof(FixedLoop.PhysicsFixedUpdate))
                return PreservesOriginal(children[i], target) && Hook(children[i - 1], before, typeof(BeforePhysicsHook))
                    && Hook(children[i + 1], after, typeof(AfterPhysicsHook));
            return false;
        }
        static bool Hook(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback, Type type) { return node.type == type && node.updateDelegate == callback && node.updateFunction == IntPtr.Zero && node.loopConditionFunction == IntPtr.Zero && (node.subSystemList == null || node.subSystemList.Length == 0); }
        PlayerLoopSystem Insert(PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return node; var children = new List<PlayerLoopSystem>();
            foreach (var raw in node.subSystemList) { var child = Insert(raw); if (node.type == typeof(FixedLoop) && raw.type == typeof(FixedLoop.PhysicsFixedUpdate)) children.Add(new PlayerLoopSystem { type = typeof(BeforePhysicsHook), updateDelegate = before }); children.Add(child); if (node.type == typeof(FixedLoop) && raw.type == typeof(FixedLoop.PhysicsFixedUpdate)) children.Add(new PlayerLoopSystem { type = typeof(AfterPhysicsHook), updateDelegate = after }); }
            node.subSystemList = children.ToArray(); return node;
        }
        PlayerLoopSystem Strip(PlayerLoopSystem node, ref int removed)
        {
            if (node.updateDelegate != null)
            {
                foreach (PlayerLoopSystem.UpdateFunction callback in node.updateDelegate.GetInvocationList())
                    if (callback == before || callback == after) { node.updateDelegate -= callback; removed++; }
            }
            if (node.subSystemList != null)
            {
                var children = new List<PlayerLoopSystem>();
                foreach (var child in node.subSystemList)
                {
                    var stripped = Strip(child, ref removed);
                    bool emptyOwnedMarker = (stripped.type == typeof(BeforePhysicsHook) || stripped.type == typeof(AfterPhysicsHook))
                        && stripped.updateDelegate == null && stripped.updateFunction == IntPtr.Zero
                        && stripped.loopConditionFunction == IntPtr.Zero
                        && (stripped.subSystemList == null || stripped.subSystemList.Length == 0);
                    if (!emptyOwnedMarker) children.Add(stripped);
                }
                node.subSystemList = children.ToArray();
            }
            return node;
        }
        static bool PreservesOriginal(PlayerLoopSystem current, PlayerLoopSystem expected) { if (!SameHeader(current, expected)) return false; var wanted = expected.subSystemList; if (wanted == null || wanted.Length == 0) return true; var actual = current.subSystemList; if (actual == null) return false; int matched = 0; for (int i = 0; i < actual.Length && matched < wanted.Length; i++) if (actual[i].type == wanted[matched].type && PreservesOriginal(actual[i], wanted[matched])) matched++; return matched == wanted.Length; }
        static bool SameHeader(PlayerLoopSystem a, PlayerLoopSystem b) { return a.type == b.type && a.updateDelegate == b.updateDelegate && a.updateFunction == b.updateFunction && a.loopConditionFunction == b.loopConditionFunction; }
        static int CountCallbacks(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback) { int result = 0; if (node.updateDelegate != null) foreach (Delegate entry in node.updateDelegate.GetInvocationList()) if (entry.Equals(callback)) result++; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += CountCallbacks(child, callback); return result; }
        static int Count(PlayerLoopSystem node, Predicate<PlayerLoopSystem> predicate) { int result = predicate(node) ? 1 : 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += Count(child, predicate); return result; }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return Find(child, type); throw new InvalidOperationException("Missing loop node."); }
        static PlayerLoopSystem FindDirectParent(PlayerLoopSystem node, Type type) { if (node.subSystemList != null) { foreach (var child in node.subSystemList) if (child.type == type) return node; foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return FindDirectParent(child, type); } throw new InvalidOperationException("Missing loop parent."); }
        static PlayerLoopSystem Clone(PlayerLoopSystem node) { if (node.subSystemList != null) { var children = new PlayerLoopSystem[node.subSystemList.Length]; for (int i = 0; i < children.Length; i++) children[i] = Clone(node.subSystemList[i]); node.subSystemList = children; } return node; }
        static double[] Vector(Vector3 value) { return new[] { (double)value.x, (double)value.y, (double)value.z }; }
    }
}
