using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    public sealed class PartForceObservation : IDisposable
    {
        sealed class BoundExceeded : Exception { public BoundExceeded(string reason) : base(reason) { } }
        static PartForceObservation owner;
        readonly List<ForceObservationBatch> batches = new List<ForceObservationBatch>();
        readonly TimingManager.UpdateAction callback;
        readonly string sessionId = Guid.NewGuid().ToString("D");
        int retainedParts, retainedHolders;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        Timing3 stage;
        bool started, finished, registered, originRegistered, lastEligible;
        string topologyKey, frameKey;
        long epoch, topologyGeneration, frameGeneration, originEvents;
        public ForceObservationReport Report { get; private set; }

        public PartForceObservation()
        {
            callback = OnFixed;
            Report = new ForceObservationReport { sessionId = sessionId,
                nativeAssemblyMvid = typeof(Part).Assembly.ManifestModule.ModuleVersionId.ToString("D") };
        }

        public void Start()
        {
            RequireThread();
            if (started || finished) throw new InvalidOperationException("Force provider supports one capture.");
            if (owner != null) throw new InvalidOperationException("Another force observation provider is active.");
            started = true; owner = this;
            try
            {
                var stages = UnityEngine.Object.FindObjectsOfType<Timing3>();
                if (stages.Length != 1 || stages[0].GetType() != typeof(Timing3))
                    throw new InvalidOperationException("Expected one native FashionablyLate stage.");
                stage = stages[0];
                originRegistered = true;
                GameEvents.onFloatingOriginShift.Add(OnOriginShift);
                // Native Add returns void and can silently do nothing. Verify the exact live slot afterwards.
                registered = true;
                TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FashionablyLate, callback);
                if (OwnCount() != 1) throw new InvalidOperationException("Named timing registration was not established.");
                Report.cleanupStatus = "registered";
                Report.status = "running";
            }
            catch (Exception error) { Finish("unavailable", "Registration failed: " + error.GetType().Name); }
        }

        public void Tick()
        {
            RequireThread();
            if (!started || finished) return;
            try
            {
                AuditOwner();
                Vessel vessel = Active();
                Observe(vessel, Eligible(vessel));
            }
            catch (BoundExceeded error) { Finish("bounded", error.Message); }
            catch (Exception error) { Finish("invalid", "Lifecycle audit failed: " + error.GetType().Name); }
        }

        void OnOriginShift(Vector3d ignoredOffset, Vector3d ignoredNonFrame)
        {
            if (!finished) originEvents++;
        }

        void OnFixed()
        {
            if (finished) return;
            try
            {
                RequireThread();
                AuditOwner();
                epoch++;
                Vessel vessel = Active();
                bool eligible = Eligible(vessel);
                Observe(vessel, eligible);
                if (!eligible) { Report.skippedCallbacks++; return; }
                Capture(vessel);
                if (batches.Count >= ForceObservationReport.MaximumBatches) Finish("complete", "Requested batch bound reached.");
            }
            catch (BoundExceeded error) { Finish("bounded", error.Message); }
            catch (Exception error) { Finish("invalid", "Observation failed: " + error.GetType().Name); }
        }

        static Vessel Active() { return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null; }
        static bool Eligible(Vessel vessel)
        {
            return vessel != null && vessel.loaded && !vessel.packed && !vessel.HoldPhysics && !FlightDriver.Pause &&
                TimeWarp.CurrentRate == 1 && Time.timeScale == 1;
        }
        static Vec Vector(Vector3d value) { return new Vec(value.x, value.y, value.z); }
        static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        void Observe(Vessel vessel, bool eligible)
        {
            string topology = Topology(vessel);
            string frame = Frame(vessel);
            if (topology != topologyKey) { topologyKey = topology; topologyGeneration++; }
            if (frame != frameKey || eligible != lastEligible) { frameKey = frame; frameGeneration++; }
            lastEligible = eligible;
        }
        string Frame(Vessel vessel)
        {
            Vector3d velocity = vessel == null ? new Vector3d() : Krakensbane.GetFrameVelocity();
            return HighLogic.LoadedScene + ":" + originEvents + ":" + (vessel == null || vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID()) +
                ":" + Number(velocity.x) + ":" + Number(velocity.y) + ":" + Number(velocity.z) + ":" + Number(TimeWarp.CurrentRate) + ":" + Number(Time.timeScale);
        }
        static string Topology(Vessel vessel)
        {
            if (vessel == null) return "no-vessel";
            if (vessel.parts == null) throw new InvalidOperationException("Missing part inventory.");
            if (vessel.parts.Count > ForceObservationReport.MaximumPartsPerBatch) throw new BoundExceeded("Part inventory exceeds batch bound.");
            var text = new StringBuilder(vessel.id.ToString("D")); text.Append(':').Append(vessel.GetInstanceID());
            foreach (Part part in vessel.parts)
            {
                if (part == null) throw new InvalidOperationException("Missing part.");
                Part physical = part.RigidBodyPart;
                text.Append(':').Append(part.flightID).Append('/').Append(part.GetInstanceID()).Append('/')
                    .Append(part.parent == null ? 0 : part.parent.flightID).Append('/')
                    .Append(part.rb == null ? 0 : part.rb.GetInstanceID()).Append('/')
                    .Append(physical == null ? 0 : physical.flightID);
            }
            return text.ToString();
        }

        void Capture(Vessel vessel)
        {
            int count = vessel.parts.Count, totalHolders = 0;
            if (count == 0) throw new InvalidOperationException("Empty eligible part inventory.");
            if (retainedParts + count > ForceObservationReport.MaximumRetainedParts)
                throw new BoundExceeded("Retained part budget reached; next batch rejected whole.");
            foreach (Part part in vessel.parts)
            {
                if (part.forces == null) throw new InvalidOperationException("Missing force-holder channel.");
                if (part.forces.Count > ForceObservationReport.MaximumHoldersPerPart) throw new BoundExceeded("Part force-holder bound exceeded.");
                totalHolders += part.forces.Count;
            }
            if (retainedHolders + totalHolders > ForceObservationReport.MaximumRetainedHolders)
                throw new BoundExceeded("Retained force-holder budget reached; next batch rejected whole.");
            var context = new ForceObservationContext(sessionId, vessel.id.ToString("D"), HighLogic.LoadedScene.ToString(),
                frameKey, Time.frameCount, vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID(), epoch,
                topologyGeneration, frameGeneration, originEvents, Planetarium.GetUniversalTime(), Time.fixedTime,
                Time.fixedDeltaTime, Vector(Krakensbane.GetFrameVelocity()));
            var parts = new ForcePartObservation[count];
            for (int i = 0; i < count; i++)
            {
                Part part = vessel.parts[i]; Rigidbody body = part.rb;
                Vec? center = body == null ? (Vec?)null : Vector(body.worldCenterOfMass);
                var holders = new ForceAtPositionObservation[part.forces.Count];
                for (int j = 0; j < holders.Length; j++)
                {
                    Part.ForceHolder force = part.forces[j];
                    Vec position = Vector(force.pos);
                    Vec? arm = center.HasValue ? new Vec(position.X - center.Value.X, position.Y - center.Value.Y, position.Z - center.Value.Z) : (Vec?)null;
                    holders[j] = new ForceAtPositionObservation(Vector(force.force), position, arm);
                }
                Part physical = part.RigidBodyPart;
                parts[i] = new ForcePartObservation(part.flightID, part.parent == null ? 0 : part.parent.flightID, part.GetInstanceID(),
                    body == null ? (int?)null : body.GetInstanceID(), Vector(part.force), Vector(part.torque), center, holders,
                    physical == null ? (long?)null : physical.flightID);
            }
            if (Active() != vessel || !Eligible(vessel) || Topology(vessel) != topologyKey || Frame(vessel) != frameKey ||
                context.floatingOriginEvents != originEvents)
                throw new InvalidOperationException("Observation context changed while copying.");
            var batch = new ForceObservationBatch(context, parts);
            batches.Add(batch);
            Report.completedBatches = batches.Count;
            retainedParts += count; retainedHolders += totalHolders;
            Report.retainedParts = retainedParts; Report.retainedHolders = retainedHolders;
            Report.partCensusStatus = "captured-component-only";
        }

        int OwnCount()
        {
            if (stage == null || stage.onFixedUpdate == null) return 0;
            int count = 0;
            foreach (Delegate entry in stage.onFixedUpdate.GetInvocationList()) if (entry.Equals(callback)) count++;
            return count;
        }
        void AuditOwner()
        {
            var stages = UnityEngine.Object.FindObjectsOfType<Timing3>();
            if (stage == null || stages.Length != 1 || !ReferenceEquals(stages[0], stage) || OwnCount() != 1)
                throw new InvalidOperationException("FashionablyLate owner changed or callback lost/duplicated.");
        }
        void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Force provider is main-thread owned.");
        }

        void Finish(string status, string detail)
        {
            if (finished) return;
            finished = true;
            Report.status = status; Report.detail = detail;
            bool cleanupError = false;
            if (registered)
            {
                try
                {
                    if (stage == null) Report.cleanupStatus = "owner-destroyed";
                    else
                    {
                        if (status != "unavailable" && OwnCount() != 1)
                        { Report.status = "invalid"; Report.detail = "Owned timing callback was lost or duplicated before removal."; }
                        // Remove from the retained owner, even when a different manager became current.
                        while (OwnCount() > 0) stage.onFixedUpdate -= callback;
                        Report.cleanupStatus = "removed-owned-callbacks";
                    }
                }
                catch (Exception) { cleanupError = true; }
            }
            if (originRegistered)
            {
                try { GameEvents.onFloatingOriginShift.Remove(OnOriginShift); }
                catch (Exception) { cleanupError = true; }
            }
            registered = false; originRegistered = false;
            if (cleanupError) Report.cleanupStatus = "cleanup-error";
            if (ReferenceEquals(owner, this)) owner = null;
            stage = null;
            Report.physicsEpochs = epoch; Report.originEvents = originEvents;
            Report.batches = batches.ToArray();
        }
        public void Dispose()
        {
            RequireThread();
            Finish(started ? "interrupted" : "not-started", "Capture disposed.");
        }
    }
}
