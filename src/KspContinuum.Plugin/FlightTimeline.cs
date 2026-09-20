using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KspContinuum
{
    public sealed class FlightTimeline : IDisposable
    {
        static readonly string[] AnalogNames = { "mainThrottle", "pitch", "yaw", "roll", "pitchTrim", "yawTrim", "rollTrim",
            "wheelSteer", "wheelSteerTrim", "wheelThrottle", "wheelThrottleTrim", "X", "Y", "Z" };
        public static string[] AnalogChannels { get { return (string[])AnalogNames.Clone(); } }
        static readonly string[] Flags = { "killRot", "gearUp", "gearDown", "headlight" };
        static readonly FieldInfo[] AnalogFields = AnalogNames.Select(n => typeof(FlightCtrlState).GetField(n)).ToArray();
        static readonly FieldInfo[] FlagFields = Flags.Select(n => typeof(FlightCtrlState).GetField(n)).ToArray();
        static readonly Guid SupportedModule = new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
        readonly string lockId = "KspContinuum.Timeline." + Guid.NewGuid().ToString("N");
        Vessel vessel;
        FlightInputCallback callback;
        bool replaying;
        InputTimeline playback;
        TimelinePlaybackCursor cursor;
        double startUt = double.NaN, lastTime, lastSampleTime = -1, lastObservedUt = double.NaN;
        string segmentTopology, segmentGroups;
        uint segmentReference;
        string topology, groups;
        Transform reference;
        List<TimelineKey>[] keys;
        List<TimelineEvent> events = new List<TimelineEvent>();
        string[] channels;
        int sampleCount, segment;
        StreamWriter observations, eventLog;
        public string Status { get; private set; } = "Timeline idle.";
        public string OutputDirectory { get; private set; }
        public bool IsRecording { get; private set; }
        public bool IsReplaying { get { return replaying; } }

        public static void RequireOfflineControl()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name == "DarkMultiPlayer" || name == "LmpClient")
                    throw new InvalidOperationException("Active control is not qualified with loaded multiplayer client " + name + ". Use an offline instance without the client.");
            }
        }

        static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        static string Topology(Vessel v)
        {
            var text = new StringBuilder();
            foreach (var p in v.parts)
                text.Append(p.flightID).Append(':').Append(p.parent == null ? 0 : p.parent.flightID).Append(':').Append(p.inverseStage).Append(';');
            return text.ToString();
        }
        static string Groups(Vessel v)
        {
            var text = new StringBuilder();
            foreach (KSPActionGroup group in Enum.GetValues(typeof(KSPActionGroup))) text.Append(v.ActionGroups[group] ? '1' : '0');
            return text.ToString();
        }
        static bool Ready(Vessel v)
        {
            return v != null && v == FlightGlobals.ActiveVessel && v.loaded && !v.packed && v.IsControllable && !v.HoldPhysics;
        }
        void Attach(Vessel target)
        {
            if (!Ready(target)) throw new InvalidOperationException("An active, controllable, unpacked vessel is required.");
            vessel = target; callback = ControlTick;
            vessel.OnFlyByWire += callback;
            GameEvents.onStageActivate.Add(Stage);
            reference = vessel.ReferenceTransform; topology = Topology(vessel); groups = Groups(vessel);
        }
        bool IsTail()
        {
            var callbacks = vessel.OnFlyByWire == null ? null : vessel.OnFlyByWire.GetInvocationList();
            return callbacks != null && callbacks.Length > 0 && callbacks[callbacks.Length - 1].Equals(callback);
        }
        bool StockCallbacks(FlightInputCallback chain, int noopToken)
        {
            if (chain == null) return true;
            foreach (var item in chain.GetInvocationList())
            {
                if (item.Equals(callback)) continue;
                if (item.Method.Module.ModuleVersionId != SupportedModule || item.Method.MetadataToken != noopToken) return false;
            }
            return true;
        }
        void CheckReplayOwner()
        {
            RequireOfflineControl();
            if (typeof(Vessel).Module.ModuleVersionId != SupportedModule)
                throw new InvalidOperationException("Unqualified game assembly.");
            if (!Ready(vessel) || FlightDriver.Pause || TimeWarp.CurrentRate != 1 || TimeWarp.CurrentRateIndex != 0)
                throw new InvalidOperationException("Replay requires unpaused normal-rate physics on the same active vessel.");
            if (vessel.GroupOverride != 0 || vessel.ActionGroups[KSPActionGroup.SAS] || vessel.Autopilot.Enabled)
                throw new InvalidOperationException("Disable SAS and control overrides before replay.");
            if (!IsTail() || !StockCallbacks(vessel.OnPreAutopilotUpdate, 0x0600aa76) ||
                !StockCallbacks(vessel.OnAutopilotUpdate, 0x0600aa77) || !StockCallbacks(vessel.OnPostAutopilotUpdate, 0x0600aa78) ||
                !StockCallbacks(vessel.OnFlyByWire, 0x0600aa79))
                throw new InvalidOperationException("Replay refuses foreign control callbacks or an unqualified game assembly.");
            if (reference != vessel.ReferenceTransform || topology != Topology(vessel) || groups != Groups(vessel))
                throw new InvalidOperationException("Control reference, topology or action groups changed.");
        }
        public void BeginRecording(Vessel target)
        {
            Stop();
            OutputDirectory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                "inputs-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OutputDirectory);
            try
            {
                Attach(target);
                eventLog = new StreamWriter(Path.Combine(OutputDirectory, "events.csv"));
                eventLog.WriteLine("ut,event,value");
                observations = new StreamWriter(Path.Combine(OutputDirectory, "observations.csv"));
                observations.WriteLine("ut,body,situation,latitude,longitude,altitude,surfaceSpeed,verticalSpeed,mass,parts,stage");
                File.WriteAllText(Path.Combine(OutputDirectory, "session.txt"),
                    "schema=ksp-continuum-input-session/v1\nboundary=vessel-control-vector-before-part-dispatch\n" +
                    "gameMvid=" + typeof(Vessel).Module.ModuleVersionId + "\nvessel=" + vessel.id + "\ninitialUt=" + Number(Planetarium.GetUniversalTime()) +
                    "\ninitialTopology=" + topology + "\nactionGroups=" + groups + "\n" +
                    "Replay does not restore this initial state. Later part consumers may modify controls.\n");
                IsRecording = true; segment = 0; lastObservedUt = double.NaN; ResetSegment(); Status = "Recording control vectors and observations.";
            }
            catch { Stop(); throw; }
        }
        void ResetSegment()
        {
            keys = null; channels = null; events.Clear(); sampleCount = 0; startUt = double.NaN; lastTime = 0; lastSampleTime = -1;
        }
        static void ValidatePlayback(InputTimeline timeline)
        {
            foreach (var name in AnalogNames)
            {
                var track = timeline.GetTrack(name);
                if (track.Min != (name == "mainThrottle" ? 0 : -1) || track.Max != 1)
                    throw new ArgumentException("Incorrect control range: " + name);
            }
            foreach (var track in timeline.Tracks)
            {
                if (Array.IndexOf(AnalogNames, track.Name) >= 0) continue;
                bool flag = Array.IndexOf(Flags, track.Name) >= 0;
                int axis;
                bool custom = track.Name.StartsWith("custom", StringComparison.Ordinal) && int.TryParse(track.Name.Substring(6), out axis);
                if (!flag && !custom) throw new ArgumentException("Unknown control channel: " + track.Name);
                foreach (var key in track.Keys)
                    if (key.Value != 0 || key.Control1 != 0 || key.Control2 != 0)
                        throw new ArgumentException("Nonzero auxiliary controls are not qualified for replay: " + track.Name);
            }
        }
        public void BeginReplay(Vessel target, InputTimeline timeline)
        {
            if (timeline == null) throw new ArgumentNullException("timeline");
            ValidatePlayback(timeline); Stop();
            try
            {
                Attach(target); CheckReplayOwner();
                playback = timeline; cursor = timeline.CreateCursor(); startUt = double.NaN;
                InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.TIMEWARP, lockId);
                replaying = true; Status = "Replaying from CURRENT vessel state; no state restore.";
            }
            catch { Stop(); throw; }
        }
        public void LoadAndReplay(Vessel target, string path)
        {
            using (var stream = File.OpenRead(path)) BeginReplay(target, TimelineCsv.Parse(stream));
        }
        void Stage(int stage)
        {
            if (vessel != FlightGlobals.ActiveVessel) return;
            if (replaying) { StopWithReason("Staging interrupted analog replay."); return; }
            RecordEvent("stage-activation-began", stage);
        }
        void RecordEvent(string name, int value)
        {
            if (!IsRecording) return;
            if (eventLog != null) eventLog.WriteLine(Number(Planetarium.GetUniversalTime()) + "," + name + "," + value.ToString(CultureInfo.InvariantCulture));
            if (!double.IsNaN(startUt))
            {
                double time = Planetarium.GetUniversalTime() - startUt;
                if (time >= 0) { events.Add(new TimelineEvent(time, name, value)); lastTime = Math.Max(time, lastTime); }
            }
        }
        void ControlTick(FlightCtrlState state)
        {
            try
            {
                if (!IsTail()) throw new InvalidOperationException("Control observer is no longer last in the vessel callback chain.");
                if (replaying)
                {
                    CheckReplayOwner();
                    double now = Planetarium.GetUniversalTime();
                    if (double.IsNaN(startUt)) startUt = now;
                    double time = now - startUt;
                    if (time > playback.Duration) { Clear(state); StopWithReason("Replay complete."); return; }
                    if (cursor.Advance(time).Count != 0) { Clear(state); StopWithReason("Replay stopped before a recorded discrete boundary."); return; }
                    for (int i = 0; i < AnalogFields.Length; i++) AnalogFields[i].SetValue(state, (float)playback.GetTrack(AnalogNames[i]).Evaluate(time));
                    foreach (var field in FlagFields) field.SetValue(state, false);
                    if (state.custom_axes != null) Array.Clear(state.custom_axes, 0, state.custom_axes.Length);
                }
                else if (IsRecording && Ready(vessel) && !FlightDriver.Pause)
                {
                    CheckRecordedContext();
                    if (keys == null) InitializeTracks(state);
                    double time = Planetarium.GetUniversalTime() - startUt;
                    if (time < lastSampleTime) throw new InvalidOperationException("Simulation clock moved backward.");
                    if (sampleCount > 0 && time == lastSampleTime) return;
                    var values = Values(state);
                    if (values.Length != keys.Length) throw new InvalidOperationException("Custom control schema changed.");
                    for (int i = 0; i < values.Length; i++)
                    {
                        AssemblyModel.Finite(values[i]);
                        double min = i == 0 || (i >= 14 && i < 18) ? 0 : -1;
                        if (values[i] < min || values[i] > 1) throw new InvalidOperationException("Out-of-range control vector.");
                        if (keys[i].Count == 0 || keys[i][keys[i].Count - 1].Value != values[i])
                            keys[i].Add(new TimelineKey(time, values[i], TimelineMode.Step));
                    }
                    sampleCount++; lastTime = Math.Max(time, lastTime); lastSampleTime = time;
                    Observe();
                    if (sampleCount >= 1000) { FlushSegment(); ResetSegment(); }
                }
            }
            catch (Exception ex)
            {
                if (replaying) Clear(state);
                StopWithReason("Timeline stopped: " + ex.Message);
            }
        }
        void InitializeTracks(FlightCtrlState state)
        {
            int count = state.custom_axes == null ? 0 : state.custom_axes.Length;
            if (count > 32) throw new InvalidOperationException("Too many custom control axes.");
            channels = AnalogNames.Concat(Flags).Concat(Enumerable.Range(0, count).Select(i => "custom" + i)).ToArray();
            keys = channels.Select(n => new List<TimelineKey>()).ToArray(); startUt = Planetarium.GetUniversalTime(); lastTime = 0; lastSampleTime = -1;
            segmentTopology = Topology(vessel); segmentGroups = Groups(vessel);
            var part = vessel.GetReferenceTransformPart(); segmentReference = part == null ? 0 : part.flightID; lastSampleTime = -1;
        }
        static double[] Values(FlightCtrlState state)
        {
            var values = new List<double>();
            foreach (var field in AnalogFields) values.Add((float)field.GetValue(state));
            foreach (var field in FlagFields) values.Add((bool)field.GetValue(state) ? 1 : 0);
            if (state.custom_axes != null) foreach (var value in state.custom_axes) values.Add(value);
            return values.ToArray();
        }
        void Observe()
        {
            double ut = Planetarium.GetUniversalTime();
            if (ut == lastObservedUt) return;
            lastObservedUt = ut;
            observations.WriteLine(string.Join(",", new[] { Number(ut), vessel.mainBody.bodyName, vessel.situation.ToString(),
                Number(vessel.latitude), Number(vessel.longitude), Number(vessel.altitude), Number(vessel.srfSpeed),
                Number(vessel.verticalSpeed), Number(vessel.GetTotalMass()), vessel.parts.Count.ToString(CultureInfo.InvariantCulture),
                vessel.currentStage.ToString(CultureInfo.InvariantCulture) }));
        }
        void FlushSegment()
        {
            if (keys == null || sampleCount == 0 || lastTime <= 0) return;
            var tracks = new List<ScalarTrack>();
            for (int i = 0; i < channels.Length; i++)
                tracks.Add(new ScalarTrack(channels[i], i == 0 || (i >= 14 && i < 18) ? 0 : -1, 1, lastTime, keys[i]));
            string stem = "segment-" + (segment++).ToString("D5", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(OutputDirectory, stem + ".csv"), TimelineCsv.Serialize(new InputTimeline(lastTime, tracks, events)));
            File.WriteAllText(Path.Combine(OutputDirectory, stem + ".txt"), "startUt=" + Number(startUt) + "\nsamples=" + sampleCount +
                "\ninitialTopology=" + segmentTopology + "\ninitialActionGroups=" + segmentGroups + "\ninitialReferencePart=" + segmentReference + "\n");
            observations.Flush();
            if (eventLog != null) eventLog.Flush();
        }
        void CheckRecordedContext()
        {
            string nextTopology = Topology(vessel), nextGroups = Groups(vessel);
            if (nextTopology != topology) { RecordEvent("topology-changed", vessel.parts.Count); topology = nextTopology; }
            if (nextGroups != groups) { RecordEvent("action-groups-changed", 0); groups = nextGroups; }
            if (reference != vessel.ReferenceTransform)
            {
                RecordEvent("control-reference-changed", 0); reference = vessel.ReferenceTransform;
            }
        }
        public void Tick()
        {
            if (!IsRecording && !replaying) return;
            try
            {
                if (vessel == null || vessel != FlightGlobals.ActiveVessel || !vessel.loaded)
                    throw new InvalidOperationException("Active vessel changed or unloaded.");
                if (!IsTail()) throw new InvalidOperationException("Control observer was removed or reordered.");
                if (replaying) CheckReplayOwner();
                else
                {
                    CheckRecordedContext();
                    if (vessel.packed && keys != null) { RecordEvent("packed", 0); FlushSegment(); ResetSegment(); }
                    if (vessel.packed) Observe();
                }
            }
            catch (Exception ex) { StopWithReason("Timeline stopped: " + ex.Message); }
        }
        static void Clear(FlightCtrlState state)
        {
            if (state == null) return;
            foreach (var field in AnalogFields) field.SetValue(state, 0f);
            foreach (var field in FlagFields) field.SetValue(state, false);
            if (state.custom_axes != null) Array.Clear(state.custom_axes, 0, state.custom_axes.Length);
        }
        void StopWithReason(string reason) { StopCore(reason); }
        public void Stop()
        {
            StopCore(IsRecording ? "Recording stopped; files saved in PluginData." :
                replaying ? "Replay aborted; controls cleared." : Status);
        }
        void StopCore(string reason)
        {
            bool wasRecording = IsRecording;
            Status = reason;
            try { if (IsRecording) FlushSegment(); }
            catch (Exception ex) { Debug.LogException(ex); Status = "Recording export failed: " + ex.Message; }
            finally
            {
                if (vessel != null && callback != null) vessel.OnFlyByWire -= callback;
                GameEvents.onStageActivate.Remove(Stage);
                InputLockManager.RemoveControlLock(lockId);
                if (replaying) { if (vessel != null) Clear(vessel.ctrlState); Clear(FlightInputHandler.state); }
                var writer = observations; observations = null;
                var log = eventLog; eventLog = null;
                IsRecording = false; replaying = false; vessel = null; callback = null; playback = null; keys = null;
                try { if (writer != null) writer.Dispose(); }
                finally
                {
                    if (log != null) log.Dispose();
                    if (wasRecording && OutputDirectory != null)
                        File.WriteAllText(Path.Combine(OutputDirectory, "session-end.txt"), "status=" + Status + "\nsegments=" + segment + "\n");
                }
            }
        }
        public void Dispose() { Stop(); }
    }
}
