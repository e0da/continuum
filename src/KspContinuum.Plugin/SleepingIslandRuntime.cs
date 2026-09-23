using System;
using System.Collections.Generic;
using UnityEngine;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class SleepingIslandRuntime : IDisposable, IPlayerLoopBracketObserver
    {
        sealed class BodyEntry
        {
            public Rigidbody body;
            public bool wasSleeping;
            public Vector3 anchorLocalCenter;
        }

        sealed class JointEntry
        {
            public Joint joint;
        }

        readonly List<BodyEntry> bodies = new List<BodyEntry>();
        readonly List<JointEntry> joints = new List<JointEntry>();
        Vessel vessel;
        Rigidbody anchor;
        int partCount, colliderCount;
        bool installed, disposed;
        public SleepingIslandReport Report { get; private set; }

        public SleepingIslandRuntime()
        { Report = new SleepingIslandReport(); }

        public void Start()
        {
            if (installed || disposed) throw new InvalidOperationException("Sleeping island supports one lifetime.");
            vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (!EligibleVessel()) { Reject("active-vessel-outside-quiet-vacuum-domain"); return; }
            partCount = vessel.parts.Count;
            var bodyIds = new HashSet<int>(); var jointIds = new HashSet<int>(); var colliderIds = new HashSet<int>();
            foreach (Part part in vessel.parts)
            {
                if (part == null) { Reject("null-part"); return; }
                AddBody(part.rb, bodyIds);
                foreach (Rigidbody body in part.GetComponentsInChildren<Rigidbody>(true)) AddBody(body, bodyIds);
                foreach (Joint joint in part.GetComponentsInChildren<Joint>(true))
                    if (joint != null && jointIds.Add(joint.GetInstanceID())) joints.Add(new JointEntry { joint = joint });
                foreach (Collider collider in part.GetComponentsInChildren<Collider>(true))
                    if (collider != null) colliderIds.Add(collider.GetInstanceID());
            }
            anchor = vessel.rootPart == null ? null : vessel.rootPart.rb;
            if (anchor == null || !bodyIds.Contains(anchor.GetInstanceID()) || bodies.Count < 2 || joints.Count < 1)
            { Reject("candidate-requires-root-body-multiple-bodies-and-joints"); return; }
            colliderCount = colliderIds.Count;
            Quaternion inverse = Quaternion.Inverse(anchor.rotation); Vector3 origin = anchor.worldCenterOfMass;
            int dynamic = 0;
            foreach (BodyEntry entry in bodies)
            {
                entry.wasSleeping = entry.body.IsSleeping();
                entry.anchorLocalCenter = inverse * (entry.body.worldCenterOfMass - origin);
                if (!entry.body.isKinematic) dynamic++;
            }
            if (dynamic < 2) { Reject("candidate-requires-multiple-dynamic-bodies"); return; }
            try
            {
                Report.vesselId = vessel.id.ToString("D"); Report.admittedParts = partCount;
                Report.admittedBodies = bodies.Count; Report.admittedDynamicBodies = dynamic;
                Report.admittedJoints = joints.Count; Report.admittedColliders = colliderCount;
                Report.installationStatus = "captured-graph-sleep-policy"; Report.cleanupStatus = "installed";
                Report.status = "installed"; installed = true;
            }
            catch (Exception error) { Report.errors++; Report.reason = "installation-failed:" + error.GetType().Name; Restore(); }
        }

        void AddBody(Rigidbody body, HashSet<int> ids)
        {
            if (body != null && ids.Add(body.GetInstanceID())) bodies.Add(new BodyEntry { body = body });
        }

        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (!installed || scope != typeof(FixedLoop.PhysicsFixedUpdate).FullName) return;
            try
            {
                if (!EligibleVessel() || vessel.parts.Count != partCount || !ObjectsPresent())
                { Report.fallbacks++; Reject("qualified-domain-changed"); Restore(); return; }
                foreach (BodyEntry entry in bodies)
                    if (!entry.body.isKinematic) { entry.body.Sleep(); Report.forcedSleeps++; }
                Report.fixedSteps++;
            }
            catch (Exception error) { Report.errors++; Report.reason = "sleep-failed:" + error.GetType().Name; Restore(); }
        }

        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (!installed || scope != typeof(FixedLoop.PhysicsFixedUpdate).FullName) return;
            try
            {
                if (Report.fixedSteps == 1 || Report.fixedSteps % 16 == 0) ValidateGeometry();
            }
            catch (Exception error) { Report.errors++; Report.reason = "validation-failed:" + error.GetType().Name; Restore(); }
        }

        public void Fault(string scope, Exception error)
        {
            if (scope != typeof(FixedLoop.PhysicsFixedUpdate).FullName) return;
            Report.errors++; Report.reason = "physics-boundary-fault:" + (error == null ? "unknown" : error.GetType().Name); Restore();
        }

        void ValidateGeometry()
        {
            Quaternion inverse = Quaternion.Inverse(anchor.rotation); Vector3 origin = anchor.worldCenterOfMass;
            foreach (BodyEntry entry in bodies)
            {
                Vector3 current = inverse * (entry.body.worldCenterOfMass - origin);
                Report.maximumInternalPositionDriftMeters = Math.Max(Report.maximumInternalPositionDriftMeters,
                    (current - entry.anchorLocalCenter).magnitude);
            }
            Report.validationPasses++;
            if (Report.maximumInternalPositionDriftMeters > .001)
                throw new InvalidOperationException("Sleeping island internal geometry drifted.");
        }

        bool EligibleVessel()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || vessel == null || vessel.parts == null ||
                !ReferenceEquals(vessel, FlightGlobals.ActiveVessel) || !vessel.loaded || vessel.packed || vessel.HoldPhysics ||
                FlightDriver.Pause || Time.timeScale != 1f || TimeWarp.CurrentRate != 1f ||
                vessel.situation != Vessel.Situations.ORBITING || vessel.atmDensity != 0 || vessel.ctrlState == null ||
                vessel.ctrlState.mainThrottle != 0 || vessel.ActionGroups[KSPActionGroup.SAS] || vessel.ActionGroups[KSPActionGroup.RCS]) return false;
            foreach (Part part in vessel.parts)
                if (part != null) foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine != null && (engine.currentThrottle != 0 || engine.finalThrust != 0)) return false;
            return true;
        }

        bool ObjectsPresent()
        {
            foreach (BodyEntry entry in bodies) if (entry.body == null) return false;
            foreach (JointEntry entry in joints) if (entry.joint == null) return false;
            return anchor != null;
        }

        void Reject(string reason)
        { Report.status = "abstained"; Report.reason = reason; }

        void Restore()
        {
            bool jointsClean = true, bodiesClean = true;
            foreach (JointEntry entry in joints) if (entry.joint == null) jointsClean = false;
            foreach (BodyEntry entry in bodies)
            {
                try
                {
                    if (entry.body == null) { bodiesClean = false; continue; }
                    if (!entry.body.isKinematic && !entry.wasSleeping) entry.body.WakeUp();
                    if (!entry.body.isKinematic && entry.wasSleeping) entry.body.Sleep();
                }
                catch { bodiesClean = false; }
            }
            Report.jointsRestored = jointsClean; Report.bodyActivityRestored = bodiesClean;
            Report.sourceTopologyStable = ObjectsPresent() && vessel != null && vessel.parts != null && vessel.parts.Count == partCount;
            Report.cleanupStatus = jointsClean && bodiesClean ? "restored-joints-and-activity" : "cleanup-error";
            installed = false;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (installed) { try { ValidateGeometry(); } catch (Exception error) { Report.errors++; Report.reason = "final-validation-failed:" + error.GetType().Name; } }
            Restore();
            if (Report.status == "installed")
                Report.status = Report.errors == 0 && Report.fallbacks == 0 && Report.fixedSteps > 0 && Report.validationPasses > 0 &&
                    Report.maximumInternalPositionDriftMeters <= .001 && Report.sourceTopologyStable && Report.jointsRestored &&
                    Report.bodyActivityRestored && Report.cleanupStatus == "restored-joints-and-activity" ? "complete" : "invalid";
        }
    }
}
