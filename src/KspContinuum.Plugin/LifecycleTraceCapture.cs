using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    public sealed class LifecycleTraceCapture : IDisposable
    {
        sealed class BoundExceeded : Exception
        {
            public BoundExceeded(string message)
                : base(message) { }
        }

        static LifecycleTraceCapture owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly List<LifecycleTraceEvent> events = new List<LifecycleTraceEvent>();
        readonly Func<double> clock;
        readonly TimingManager.UpdateAction fashion,
            integrator,
            late;
        Timing3 stage3;
        TimingFI stageFI;
        Timing5 stage5;
        bool started,
            finished,
            originRegistered,
            registrationsAttempted;
        string topologyKey,
            frameKey;
        double startTime,
            previousElapsed;
        public LifecycleTraceReport Report { get; private set; }
        public bool IsRunning
        {
            get { return started && !finished; }
        }

        public LifecycleTraceCapture(Func<double> clock = null)
        {
            this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            fashion = () => Observe("TimingManager.FashionablyLate");
            integrator = () => Observe("TimingManager.FlightIntegrator");
            late = () => Observe("TimingManager.BetterLateThanNever");
            Report = new LifecycleTraceReport
            {
                sessionId = Guid.NewGuid().ToString("D"),
                nativeAssemblyMvid = typeof(Part).Assembly.ManifestModule.ModuleVersionId.ToString(
                    "D"
                ),
                unityAssemblyMvid =
                    typeof(Rigidbody).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                pluginAssemblyMvid =
                    typeof(LifecycleTraceCapture).Assembly.ManifestModule.ModuleVersionId.ToString(
                        "D"
                    ),
            };
        }

        public void Start()
        {
            RequireThread();
            if (started || finished)
                throw new InvalidOperationException("One trace per instance.");
            if (owner != null)
                throw new InvalidOperationException("Another lifecycle trace owns registration.");
            started = true;
            owner = this;
            try
            {
                startTime = clock();
                Finite(startTime);
                stage3 = Single<Timing3>();
                stageFI = Single<TimingFI>();
                stage5 = Single<Timing5>();
                var vessel = Active();
                if (Eligible(vessel))
                {
                    topologyKey = Topology(vessel);
                    frameKey = Frame(vessel);
                    Report.topologyGeneration = Report.frameGeneration = 1;
                }
                originRegistered = true;
                GameEvents.onFloatingOriginShift.Add(OnOriginShift);
                registrationsAttempted = true;
                TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FashionablyLate, fashion);
                TimingManager.FixedUpdateAdd(
                    TimingManager.TimingStage.FlightIntegrator,
                    integrator
                );
                TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, late);
                Audit();
                Report.registrationStatus = "all-three-exact-callbacks-readback-confirmed";
                Report.status = topologyKey == null ? "arming" : "running";
                Report.cleanupStatus = "registered-readback-confirmed";
            }
            catch (BoundExceeded error)
            {
                Finish("bounded", error.Message);
            }
            catch (Exception error)
            {
                Finish("unavailable", "Registration/start failed: " + error.GetType().Name);
            }
        }

        public void ObserveUpdate()
        {
            Observe("Host.Update");
        }

        public void ObserveFixedUpdate()
        {
            Observe("Host.FixedUpdate");
        }

        public void ObserveAfterFixedUpdate()
        {
            Observe("Host.WaitForFixedUpdate");
        }

        void OnOriginShift(Vector3d offset, Vector3d other)
        {
            RequireThread();
            if (!finished)
                Report.originEvents++;
        }

        void Observe(string name)
        {
            RequireThread();
            if (!IsRunning)
                return;
            try
            {
                double elapsed = clock() - startTime;
                Finite(elapsed);
                if (elapsed < previousElapsed)
                    throw new InvalidOperationException("Monotonic clock moved backwards.");
                previousElapsed = elapsed;
                Report.elapsedWallSeconds = elapsed;
                if (elapsed >= LifecycleTraceReport.MaximumWallSeconds)
                {
                    Finish("timeout", "Wall-time bound reached; final cycle may be partial.");
                    return;
                }
                Audit();
                var vessel = Active();
                bool eligible = Eligible(vessel);
                if (topologyKey == null)
                {
                    if (!eligible)
                    {
                        Report.skippedArmingCallbacks++;
                        return;
                    }
                    topologyKey = Topology(vessel);
                    frameKey = Frame(vessel);
                    Report.topologyGeneration = Report.frameGeneration = 1;
                    Report.status = "running";
                }
                if (name == "Host.FixedUpdate")
                    Report.hostFixedObservations++;
                string topology = Topology(vessel),
                    frame = Frame(vessel);
                bool changed = topology != topologyKey || frame != frameKey || !eligible;
                if (topology != topologyKey)
                {
                    topologyKey = topology;
                    Report.topologyGeneration++;
                }
                if (frame != frameKey || !eligible)
                {
                    frameKey = frame;
                    Report.frameGeneration++;
                }
                var context = new LifecycleTraceContext(
                    Report.sessionId,
                    name,
                    vessel == null ? null : vessel.id.ToString("D"),
                    HighLogic.LoadedScene.ToString(),
                    frameKey,
                    events.Count + 1,
                    Report.topologyGeneration,
                    Report.frameGeneration,
                    Report.originEvents,
                    thread,
                    Time.frameCount,
                    Report.hostFixedObservations,
                    vessel == null || vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID(),
                    elapsed,
                    Planetarium.GetUniversalTime(),
                    Time.fixedTime,
                    Time.fixedDeltaTime,
                    TimeWarp.CurrentRate,
                    Time.timeScale,
                    vessel != null && vessel.loaded,
                    vessel != null && vessel.packed,
                    vessel != null && vessel.HoldPhysics,
                    FlightDriver.Pause,
                    eligible,
                    vessel == null ? new Vec() : Vector(Krakensbane.GetFrameVelocity())
                );
                var parts = changed ? new LifecyclePartSample[0] : Capture(vessel);
                if (
                    Active() != vessel
                    || Topology(vessel) != topologyKey
                    || Frame(vessel) != frameKey
                    || context.originEvents != Report.originEvents
                )
                    throw new InvalidOperationException("Context changed during sample.");
                var entry = new LifecycleTraceEvent(
                    context,
                    changed ? "invalidated-no-census" : "captured-component-only",
                    parts
                );
                int holders = 0;
                foreach (var p in parts)
                    holders += p.census.forces.Count;
                int bytes = Encoding.UTF8.GetByteCount(ReportJson.Encode(entry));
                if (
                    events.Count == LifecycleTraceReport.MaximumEvents
                    || Report.retainedParts + parts.Length
                        > LifecycleTraceReport.MaximumRetainedParts
                    || Report.retainedHolders + holders
                        > LifecycleTraceReport.MaximumRetainedHolders
                    || Report.encodedEventBytes + bytes
                        > LifecycleTraceReport.MaximumEncodedEventBytes
                )
                    throw new BoundExceeded(
                        "Whole event refused: retained event/part/holder/encoded-byte budget."
                    );
                events.Add(entry);
                Report.completedEvents = events.Count;
                Report.retainedParts += parts.Length;
                Report.retainedHolders += holders;
                Report.encodedEventBytes += bytes;
                if (changed)
                    Finish(
                        "invalidated",
                        "Scene/vessel/topology/frame/eligibility changed; trace stopped."
                    );
                else if (
                    Report.hostFixedObservations
                    >= LifecycleTraceReport.MaximumHostFixedObservations
                )
                    Finish(
                        "bounded",
                        "Host FixedUpdate observation bound reached; terminal cycle may be partial."
                    );
            }
            catch (BoundExceeded error)
            {
                Finish("bounded", error.Message);
            }
            catch (Exception error)
            {
                Finish("invalid", "Observation failed: " + error.GetType().Name);
            }
        }

        static Vessel Active()
        {
            return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready
                ? FlightGlobals.ActiveVessel
                : null;
        }

        static bool Eligible(Vessel vessel)
        {
            return vessel != null
                && vessel.loaded
                && !vessel.packed
                && !vessel.HoldPhysics
                && !FlightDriver.Pause
                && TimeWarp.CurrentRate == 1
                && Time.timeScale == 1;
        }

        static void Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("Nonfinite sample.");
        }

        static Vec Vector(Vector3d value)
        {
            return new Vec(value.x, value.y, value.z);
        }

        static string Number(double value)
        {
            Finite(value);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        string Frame(Vessel vessel)
        {
            var velocity = vessel == null ? new Vector3d() : Krakensbane.GetFrameVelocity();
            return HighLogic.LoadedScene
                + ":"
                + Report.originEvents
                + ":"
                + (vessel == null || vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID())
                + ":"
                + Number(velocity.x)
                + ":"
                + Number(velocity.y)
                + ":"
                + Number(velocity.z)
                + ":"
                + Number(TimeWarp.CurrentRate)
                + ":"
                + Number(Time.timeScale);
        }

        static string Topology(Vessel vessel)
        {
            if (vessel == null)
                return "no-vessel";
            if (vessel.parts == null)
                throw new InvalidOperationException("Missing part inventory.");
            if (vessel.parts.Count > LifecycleTraceReport.MaximumPartsPerEvent)
                throw new BoundExceeded("Inventory exceeds event part bound.");
            var text = new StringBuilder(vessel.id.ToString("D"));
            text.Append(':').Append(vessel.GetInstanceID());
            foreach (var p in vessel.parts)
            {
                if (p == null)
                    throw new InvalidOperationException("Null part.");
                text.Append(':')
                    .Append(p.flightID)
                    .Append('/')
                    .Append(p.GetInstanceID())
                    .Append('/')
                    .Append(p.parent == null ? 0 : p.parent.flightID)
                    .Append('/')
                    .Append(p.rb == null ? 0 : p.rb.GetInstanceID())
                    .Append('/')
                    .Append(p.rb != null && p.rb.isKinematic)
                    .Append('/')
                    .Append(p.RigidBodyPart == null ? 0 : p.RigidBodyPart.flightID);
            }
            return text.ToString();
        }

        LifecyclePartSample[] Capture(Vessel vessel)
        {
            int count = vessel.parts.Count;
            if (count == 0)
                throw new InvalidOperationException("Empty eligible vessel.");
            if (Report.retainedParts + count > LifecycleTraceReport.MaximumRetainedParts)
                throw new BoundExceeded("Whole event refused: retained part budget.");
            var samples = new LifecyclePartSample[count];
            var ids = new HashSet<uint>();
            var nativeIds = new HashSet<int>();
            int totalHolders = 0;
            foreach (var p in vessel.parts)
            {
                if (!ids.Add(p.flightID) || !nativeIds.Add(p.GetInstanceID()))
                    throw new InvalidOperationException("Duplicate part identity.");
                if (p.forces == null)
                    throw new InvalidOperationException("Missing force channel.");
                if (p.forces.Count > ForceObservationReport.MaximumHoldersPerPart)
                    throw new BoundExceeded("Part holder bound exceeded.");
                totalHolders += p.forces.Count;
            }
            if (Report.retainedHolders + totalHolders > LifecycleTraceReport.MaximumRetainedHolders)
                throw new BoundExceeded("Whole event refused: retained holder budget.");
            for (int i = 0; i < count; i++)
            {
                var p = vessel.parts[i];
                var b = p.rb;
                if (
                    (p.parent != null && !ids.Contains(p.parent.flightID))
                    || (p.RigidBodyPart != null && !ids.Contains(p.RigidBodyPart.flightID))
                )
                    throw new InvalidOperationException("Foreign topology reference.");
                Vec? center = b == null ? (Vec?)null : Vector(b.worldCenterOfMass);
                var holders = new ForceAtPositionObservation[p.forces.Count];
                for (int j = 0; j < holders.Length; j++)
                {
                    var f = p.forces[j];
                    var position = Vector(f.pos);
                    Vec? arm = center.HasValue ? (Vec?)(position + center.Value * -1) : null;
                    holders[j] = new ForceAtPositionObservation(Vector(f.force), position, arm);
                }
                var census = new ForcePartObservation(
                    p.flightID,
                    p.parent == null ? 0 : p.parent.flightID,
                    p.GetInstanceID(),
                    b == null ? (int?)null : b.GetInstanceID(),
                    Vector(p.force),
                    Vector(p.torque),
                    center,
                    holders,
                    p.RigidBodyPart == null ? (long?)null : p.RigidBodyPart.flightID
                );
                samples[i] =
                    b == null
                        ? new LifecyclePartSample(census, null, null, null, null, null)
                        : new LifecyclePartSample(
                            census,
                            Vector(b.position),
                            Vector(b.velocity),
                            Vector(b.angularVelocity),
                            new Vec(b.rotation.x, b.rotation.y, b.rotation.z),
                            b.rotation.w
                        );
            }
            return samples;
        }

        static T Single<T>()
            where T : UnityEngine.Object
        {
            var found = UnityEngine.Object.FindObjectsOfType<T>();
            if (found.Length != 1 || found[0].GetType() != typeof(T))
                throw new InvalidOperationException("Native stage unavailable/ambiguous.");
            return found[0];
        }

        static int Count(TimingManager.UpdateAction slot, TimingManager.UpdateAction callback)
        {
            int count = 0;
            if (slot != null)
                foreach (Delegate d in slot.GetInvocationList())
                    if (d.Equals(callback))
                        count++;
            return count;
        }

        void Audit()
        {
            if (
                stage3 == null
                || stageFI == null
                || stage5 == null
                || !ReferenceEquals(Single<Timing3>(), stage3)
                || !ReferenceEquals(Single<TimingFI>(), stageFI)
                || !ReferenceEquals(Single<Timing5>(), stage5)
                || Count(stage3.onFixedUpdate, fashion) != 1
                || Count(stageFI.onFixedUpdate, integrator) != 1
                || Count(stage5.onFixedUpdate, late) != 1
            )
                throw new InvalidOperationException("Stage owner/callback changed.");
        }

        void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Main-thread observer only.");
        }

        void Finish(string status, string detail)
        {
            if (finished)
                return;
            finished = true;
            Report.status = status;
            Report.detail = detail;
            bool error = false,
                destroyed = false;
            if (registrationsAttempted)
            {
                if (status != "unavailable")
                {
                    try
                    {
                        Audit();
                    }
                    catch (Exception)
                    {
                        Report.status = "invalid";
                        Report.detail = "Stage owner/callback lost or duplicated before removal.";
                    }
                }
                try
                {
                    if (stage3 == null)
                        destroyed = true;
                    else
                        while (Count(stage3.onFixedUpdate, fashion) > 0)
                            stage3.onFixedUpdate -= fashion;
                }
                catch (Exception)
                {
                    error = true;
                }
                try
                {
                    if (stageFI == null)
                        destroyed = true;
                    else
                        while (Count(stageFI.onFixedUpdate, integrator) > 0)
                            stageFI.onFixedUpdate -= integrator;
                }
                catch (Exception)
                {
                    error = true;
                }
                try
                {
                    if (stage5 == null)
                        destroyed = true;
                    else
                        while (Count(stage5.onFixedUpdate, late) > 0)
                            stage5.onFixedUpdate -= late;
                }
                catch (Exception)
                {
                    error = true;
                }
                Report.cleanupStatus =
                    error ? "cleanup-error"
                    : destroyed ? "owner-destroyed"
                    : "removed-owned-callbacks";
            }
            if (originRegistered)
            {
                try
                {
                    GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
                }
                catch (Exception)
                {
                    Report.cleanupStatus = "cleanup-error";
                }
            }
            Report.events = events.ToArray();
            if (ReferenceEquals(owner, this))
                owner = null;
        }

        public void Dispose()
        {
            RequireThread();
            Finish(
                started ? "interrupted" : "not-started",
                "Capture disposed; terminal cycle may be partial."
            );
        }
    }
}
