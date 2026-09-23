using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;

namespace KspContinuum
{
    public sealed class Probe : IDisposable
    {
        sealed class Slot
        {
            public Recorder Recorder;
            public bool EnabledBefore;
            public MarkerReport Report;
        }
        static Probe owner;
        static readonly string[] Names = { "Physics.Simulate", "Physics.Processing", "BehaviourFixedUpdate", "BehaviourUpdate", "GC.Collect" };
        readonly List<Slot> slots = new List<Slot>();
        readonly Stopwatch clock = new Stopwatch();
        ProbeReport report;
        PlayerLoopTiming playerLoop;
        ActiveVesselWriterCensus writerCensus;
        ActiveVesselPhysicsSubstitutionCanary substitutionCanary;
        PartForceObservation partForces;
        FixedCallbackAttribution callbackAttribution;
        DryBuoyancyRuntime dryBuoyancy;
        Action<ProbeReport> completion;
        bool started, finished;
        int completed;
        Vessel lastVessel;
        string lastVesselId;
        int structuralRigidbodies = -1, structuralJoints = -1, structuralColliders = -1;
        const int FrameCount = 300;

        public IEnumerator Run(Action<ProbeReport> complete)
        {
            if (complete == null) throw new ArgumentNullException("complete");
            if (started || finished) throw new InvalidOperationException("Probe instances support one capture.");
            if (owner != null) throw new InvalidOperationException("Another Continuum timing capture is already running.");
            started = true; owner = this; completion = complete;
            try
            {
                report = new ProbeReport {
                    requestedFrames = FrameCount, frames = new ProfileFrame[FrameCount], markers = new MarkerReport[Names.Length],
                    unity = Application.unityVersion, ksp = Versioning.GetVersionString(),
                    plugin = typeof(Probe).Assembly.GetName().Version.ToString(), platform = Application.platform.ToString(),
                    processor = SystemInfo.processorType, processorCount = SystemInfo.processorCount,
                    graphicsDevice = SystemInfo.graphicsDeviceName, targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount
                };
                for (int i = 0; i < Names.Length; i++)
                {
                    var row = new MarkerReport { name = Names[i], status = "unavailable", availabilityDetail = "Recorder unavailable in this player or marker has not been created.",
                        nanoseconds = new long[FrameCount], blocks = new int[FrameCount], available = new bool[FrameCount] };
                    report.markers[i] = row;
                    Acquire(row);
                }
                string[] arguments = Environment.GetCommandLineArgs();
                string canaryReason;
                bool canaryRequested = ActiveVesselPhysicsSubstitutionCanary.RequestedAndQualified(arguments, out canaryReason);
                if (canaryRequested && canaryReason != null)
                {
                    report.substitutionCanary = new PhysicsSubstitutionCanaryReport { status = "invalid", reason = canaryReason };
                    throw new InvalidOperationException(canaryReason);
                }
                if (Array.IndexOf(arguments, "--continuum-writer-census") >= 0)
                {
                    writerCensus = new ActiveVesselWriterCensus(); report.writerCensus = writerCensus.Census.Report;
                }
                if (canaryRequested)
                {
                    substitutionCanary = new ActiveVesselPhysicsSubstitutionCanary(writerCensus);
                    report.substitutionCanary = substitutionCanary.Report;
                }
                if (Array.IndexOf(arguments, "--continuum-callback-attribution") >= 0)
                {
                    callbackAttribution = new FixedCallbackAttribution(); callbackAttribution.Start();
                    report.callbackAttribution = callbackAttribution.Report;
                }
                if (Array.IndexOf(arguments, "--continuum-dry-buoyancy") >= 0)
                {
                    dryBuoyancy = new DryBuoyancyRuntime(); dryBuoyancy.Start(); report.dryBuoyancy = dryBuoyancy.Report;
                    if (dryBuoyancy.Report.status != "installed") throw new InvalidOperationException("Dry buoyancy admission did not install: " + dryBuoyancy.Report.status);
                }
                if (Array.IndexOf(arguments, "--continuum-playerloop") >= 0 || writerCensus != null || callbackAttribution != null || dryBuoyancy != null)
                {
                    IPlayerLoopBracketObserver observer = writerCensus == null ? null : writerCensus.Census;
                    if (substitutionCanary != null) observer = new CompositePlayerLoopObserver(observer, substitutionCanary);
                    if (callbackAttribution != null) observer = new CompositePlayerLoopObserver(observer, callbackAttribution);
                    if (dryBuoyancy != null) observer = new CompositePlayerLoopObserver(observer, dryBuoyancy);
                    playerLoop = new PlayerLoopTiming(observer); playerLoop.Start(); report.playerLoop = playerLoop.Report;
                    if (substitutionCanary != null) substitutionCanary.Start();
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-part-forces") >= 0)
                {
                    partForces = new PartForceObservation();
                    partForces.Start();
                    report.partForces = partForces.Report;
                }
                clock.Start();
                // Recorder counters describe the previous frame; discard the partly enabled initial frame.
                yield return null;
                if (finished) yield break;
                ProfileFrame previous = Context();
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    yield return null;
                    if (finished) yield break;
                    if (playerLoop != null)
                    {
                        playerLoop.Audit();
                        if (dryBuoyancy != null && playerLoop.Report.integrityStatus == "invalidated")
                        {
                            dryBuoyancy.Dispose();
                            dryBuoyancy = null;
                        }
                    }
                    if (partForces != null) partForces.Tick();
                    double now = clock.Elapsed.TotalSeconds;
                    previous.observedFrame = Time.frameCount;
                    previous.markerFrame = Time.frameCount - 1;
                    previous.contextAligned = previous.contextFrame == previous.markerFrame;
                    previous.wallMilliseconds = (now - previous.boundaryWallSeconds) * 1000;
                    foreach (Slot slot in slots) Read(slot, frame);
                    report.frames[frame] = previous;
                    completed++;
                    if (completed < FrameCount) previous = Context();
                }
                report.status = "complete";
                Finish(false);
            }
            finally { Dispose(); }
        }

        void Acquire(MarkerReport row)
        {
            try
            {
                Recorder recorder = Recorder.Get(row.name);
                if (recorder == null || !recorder.isValid) return;
                var slot = new Slot { Recorder = recorder, EnabledBefore = recorder.enabled, Report = row };
                slots.Add(slot);
                if (!slot.EnabledBefore) recorder.enabled = true;
                row.recorderAvailableAtStart = recorder.enabled;
                row.availabilityDetail = recorder.enabled ? "Recorder valid and enabled; per-frame availability is recorded separately." : "Recorder could not be enabled.";
            }
            catch (Exception error) { row.availabilityDetail = "Recorder setup failed: " + error.GetType().Name; }
        }

        static void Read(Slot slot, int index)
        {
            try
            {
                if (!slot.Recorder.isValid || !slot.Recorder.enabled)
                {
                    slot.Report.availabilityDetail = "Recorder became invalid or disabled during capture.";
                    return;
                }
                long nanoseconds = slot.Recorder.elapsedNanoseconds;
                int blocks = slot.Recorder.sampleBlockCount;
                slot.Report.nanoseconds[index] = nanoseconds;
                slot.Report.blocks[index] = blocks;
                slot.Report.available[index] = nanoseconds >= 0 && blocks >= 0 && (blocks != 0 || nanoseconds == 0);
                if (!slot.Report.available[index]) slot.Report.availabilityDetail = "Native counters contained an invalid or inconsistent reading; its availability is false.";
            }
            catch (Exception error) { slot.Report.availabilityDetail = "Recorder read failed: " + error.GetType().Name; }
        }

        ProfileFrame Context()
        {
            var frame = new ProfileFrame {
                contextFrame = Time.frameCount, renderedFrame = Time.renderedFrameCount, boundaryWallSeconds = clock.Elapsed.TotalSeconds,
                fixedDeltaSeconds = Time.fixedDeltaTime, timeScale = Time.timeScale, scene = HighLogic.LoadedScene.ToString(),
                screenWidth = Screen.width, screenHeight = Screen.height, parts = -1,
                rigidbodies = -1, joints = -1, colliders = -1, loadedVessels = -1,
                managedBytes = GC.GetTotalMemory(false), gcGeneration0 = GC.CollectionCount(0), gcGeneration1 = GC.CollectionCount(1), gcGeneration2 = GC.CollectionCount(2),
                vesselStatus = "unavailable-no-active-vessel"
            };
            if (HighLogic.CurrentGame != null) frame.universalTime = Planetarium.GetUniversalTime();
            if (TimeWarp.fetch != null) frame.warpRate = TimeWarp.CurrentRate;
            if (HighLogic.LoadedSceneIsFlight) frame.paused = FlightDriver.Pause;
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null) return frame;
            if (lastVessel != vessel) { lastVessel = vessel; lastVesselId = vessel.id.ToString("D"); structuralRigidbodies = -1; }
            frame.vesselId = lastVesselId;
            frame.vesselStatus = vessel.loaded ? "loaded" : "unloaded";
            frame.loaded = vessel.loaded; frame.packed = vessel.packed;
            frame.throttleCommand = vessel.ctrlState == null ? (double?)null : vessel.ctrlState.mainThrottle;
            frame.parts = vessel.parts == null ? -1 : vessel.parts.Count;
            if (FlightGlobals.VesselsLoaded != null) frame.loadedVessels = FlightGlobals.VesselsLoaded.Count;
            // Census the whole vessel only at capture boundaries. Per-frame hierarchy walks would
            // perturb the same part-count scaling this probe is intended to measure.
            if (structuralRigidbodies < 0 || completed == 0 || completed == FrameCount - 1)
            {
                StructuralCensusResult census = StructuralCensusCapture.Capture(vessel);
                structuralRigidbodies = census.rigidbodies;
                structuralJoints = census.joints;
                structuralColliders = census.colliders;
            }
            frame.rigidbodies = structuralRigidbodies; frame.joints = structuralJoints; frame.colliders = structuralColliders;
            frame.body = vessel.mainBody == null ? null : vessel.mainBody.bodyName;
            frame.situation = vessel.situation.ToString();
            return frame;
        }

        void Finish(bool suppressExportFailure)
        {
            if (finished) return;
            finished = true;
            var errors = new List<string>();
            if (substitutionCanary != null)
            {
                try { substitutionCanary.Dispose(); }
                catch (Exception error) { errors.Add("SubstitutionCanary: " + error.GetType().Name); }
                substitutionCanary = null;
            }
            if (playerLoop != null)
            {
                try { playerLoop.Dispose(); }
                catch (Exception error) { errors.Add("PlayerLoop: " + error.GetType().Name); }
                if (playerLoop.Report.cleanupStatus == "cleanup-error") errors.Add("PlayerLoop: cleanup-error");
                playerLoop = null;
            }
            if (writerCensus != null)
            {
                try { writerCensus.Dispose(); }
                catch (Exception error) { errors.Add("WriterCensus: " + error.GetType().Name); }
                writerCensus = null;
            }
            if (callbackAttribution != null)
            {
                try { callbackAttribution.Dispose(); }
                catch (Exception error) { errors.Add("CallbackAttribution: " + error.GetType().Name); }
                if (callbackAttribution.Report.cleanupStatus == "cleanup-error") errors.Add("CallbackAttribution: cleanup-error");
                callbackAttribution = null;
            }
            if (dryBuoyancy != null)
            {
                try { dryBuoyancy.Dispose(); }
                catch (Exception error) { errors.Add("DryBuoyancy: " + error.GetType().Name); }
                if (dryBuoyancy.Report.cleanupStatus == "cleanup-error") errors.Add("DryBuoyancy: cleanup-error");
                if (dryBuoyancy.Report.status != "complete") errors.Add("DryBuoyancy: " + dryBuoyancy.Report.status);
                dryBuoyancy = null;
            }
            if (partForces != null)
            {
                try { partForces.Dispose(); }
                catch (Exception error) { errors.Add("PartForces: " + error.GetType().Name); }
                if (partForces.Report.cleanupStatus == "cleanup-error") errors.Add("PartForces: cleanup-error");
                partForces = null;
            }
            foreach (Slot slot in slots)
            {
                try { if (!slot.EnabledBefore && slot.Recorder.enabled) slot.Recorder.enabled = false; }
                catch (Exception error) { errors.Add(slot.Report.name + ": " + error.GetType().Name); }
            }
            slots.Clear();
            if (ReferenceEquals(owner, this)) owner = null;
            clock.Stop();
            Action<ProbeReport> callback = completion;
            completion = null;
            if (report == null || callback == null) return;
            try
            {
                report.cleanupErrors = errors.ToArray();
                report.recorderCleanupStatus = errors.Count == 0 ? "restored-owned-enables" : "restoration-errors";
                if (errors.Count != 0) report.status = "cleanup-error";
                ProfilingSummary.Finish(report, completed);
                callback(report);
            }
            catch (Exception error)
            {
                if (!suppressExportFailure) throw;
                // Teardown must continue even if the destination became unwritable.
                UnityEngine.Debug.LogError("[Continuum] Partial profiling report export failed: " + error.GetType().Name);
                UnityEngine.Debug.LogException(error);
            }
        }

        public void Dispose() { Finish(true); }
    }
}
