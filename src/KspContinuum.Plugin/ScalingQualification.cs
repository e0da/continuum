using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ScalingQualification : MonoBehaviour
    {
        static ScalingQualification instance;
        Probe probe;
        string directory, sweepState = "idle", sweepWindow;
        int sweepWindowIndex = -1;
        bool active, capturing, finished, strategySweep, quitAfterQualification;
        float eligibleSince;
        int expectedParts = -1;

        public void Awake() { instance = this; }

        public static bool TryStartDryBuoyancySweep(out string reason)
        {
            if (instance == null) { reason = "flight-sweep-component-unavailable"; return false; }
            return instance.StartExternalSweep(out reason);
        }

        public static string GetDryBuoyancySweepState() { return instance == null ? "unavailable" : instance.sweepState; }
        public static string GetDryBuoyancySweepDirectory() { return instance == null ? null : instance.directory; }
        public static string GetDryBuoyancySweepWindow() { return instance == null ? null : instance.sweepWindow; }
        public static int GetDryBuoyancySweepWindowIndex() { return instance == null ? -1 : instance.sweepWindowIndex; }

        bool StartExternalSweep(out string reason)
        {
            if (active || capturing || finished) { reason = "qualification-already-active"; return false; }
            Vessel current = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (current == null || current.parts == null) { reason = "active-vessel-unavailable"; return false; }
            try
            {
                PrepareDirectory(); strategySweep = true; quitAfterQualification = false;
                expectedParts = current.parts.Count; eligibleSince = 0; sweepWindow = null; sweepWindowIndex = -1;
                WriteScope(); active = true; sweepState = "waiting-for-orbit";
                reason = "accepted"; return true;
            }
            catch (Exception error) { sweepState = "error"; reason = error.GetType().Name + ": " + error.Message; return false; }
        }

        void PrepareDirectory()
        {
            directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                "scale-profile-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        void WriteScope()
        {
            File.WriteAllText(Path.Combine(directory, "scope.txt"),
                (strategySweep ? "Repeated in-process stock, full-publication batch and resident dry-domain orbital windows.\n" :
                "Single settled stock-vessel orbital window.\n") +
                "PlayerLoop scopes overlap and must not be summed. The active fixed parent owns strategy comparison.\n" +
                "Experimental strategies are opt-in and do not establish complete stock semantics.\n");
        }

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "--continuum-experiment-host") >= 0) { sweepState = "idle"; return; }
            if (Array.IndexOf(arguments, "--continuum-scale-profile") < 0) return;
            if (Array.IndexOf(arguments, "--continuum-live-setdrag-provider") >= 0 &&
                Array.IndexOf(arguments, "--continuum-setdrag-quit-after-qualification") >= 0) return;
            try
            {
                PrepareDirectory(); quitAfterQualification = true;
                if (Array.IndexOf(arguments, "--continuum-playerloop") < 0)
                { Finish("missing-playerloop-flag", 2); return; }
                strategySweep = Array.IndexOf(arguments, "--continuum-dry-buoyancy-sweep") >= 0;
                foreach (string argument in arguments) if (argument.StartsWith("--continuum-scale-parts=", StringComparison.Ordinal))
                {
                    int parsed;
                    if (!int.TryParse(argument.Substring("--continuum-scale-parts=".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed) || parsed < 1) { Finish("invalid-part-count", 2); return; }
                    expectedParts = parsed;
                }
                WriteScope();
                active = true;
                sweepState = strategySweep ? "waiting-for-orbit" : "single-window";
            }
            catch (Exception error) { Fail(error); }
        }

        public void Update()
        {
            if (!active || capturing) return;
            if (ScaleCheckpointLoadState.Requested && !ScaleCheckpointLoadState.Ready) return;
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            bool eligible = vessel != null && vessel.loaded && !vessel.packed && !FlightDriver.Pause &&
                TimeWarp.CurrentRate == 1 && vessel.situation == Vessel.Situations.ORBITING &&
                vessel.ctrlState != null && vessel.ctrlState.mainThrottle < 0.01 &&
                (!ScaleCheckpointLoadState.Requested || vessel.id == ScaleCheckpointLoadState.VesselId) &&
                (expectedParts < 1 || vessel.parts.Count == expectedParts);
            if (!eligible) { eligibleSince = 0; return; }
            if (eligibleSince == 0) { eligibleSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - eligibleSince < 10) return;
            capturing = true;
            if (strategySweep) { sweepState = "running"; StartCoroutine(CaptureSweep()); }
            else { probe = new Probe(); StartCoroutine(Capture()); }
        }

        IEnumerator Capture()
        {
            IEnumerator work = probe.Run(Complete);
            try { while (active && work.MoveNext()) yield return work.Current; }
            finally { var disposable = work as IDisposable; if (disposable != null) disposable.Dispose(); capturing = false; }
        }

        IEnumerator CaptureSweep()
        {
            string[] strategies = { DryBuoyancyRuntime.Stock, DryBuoyancyRuntime.FullPublication, DryBuoyancyRuntime.ResidentDomain,
                DryBuoyancyRuntime.Stock, DryBuoyancyRuntime.ResidentDomain, DryBuoyancyRuntime.FullPublication };
            string[] labels = { "stock-01", "full-publication-01", "resident-domain-01",
                "stock-02", "resident-domain-02", "full-publication-02" };
            bool valid = true; string reason = "verified-in-process-strategy-sweep";
            try
            {
                for (int i = 0; active && i < strategies.Length; i++)
                {
                    sweepWindowIndex = i; sweepWindow = labels[i];
                    ProbeReport captured = null;
                    probe = new Probe(strategies[i], true);
                    IEnumerator work = probe.Run(report => captured = report);
                    try
                    {
                        while (active)
                        {
                            bool moved;
                            try { moved = work.MoveNext(); }
                            catch (Exception error) { Fail(error); yield break; }
                            if (!moved) break;
                            yield return work.Current;
                        }
                    }
                    finally { var disposable = work as IDisposable; if (disposable != null) disposable.Dispose(); }
                    probe = null;
                    if (!active) yield break;
                    if (captured == null) { valid = false; reason = "missing-sweep-report"; break; }
                    try { File.WriteAllText(Path.Combine(directory, labels[i] + "-markers.json"), ReportJson.Encode(captured)); }
                    catch (Exception error) { Fail(error); yield break; }
                    string windowReason;
                    if (!Stable(captured, out windowReason)) { valid = false; reason = labels[i] + ":" + windowReason; break; }
                    yield return null;
                }
                bool sourceUnchanged = !ScaleCheckpointLoadState.Requested || ScaleCheckpointLoadState.SourceUnchanged();
                if (!sourceUnchanged) { valid = false; reason = "source-checkpoint-changed"; }
                try
                {
                    File.WriteAllText(Path.Combine(directory, "status.txt"), (valid ? "complete" : "invalid") + "\nreason=" + reason + "\n" +
                        "sequence=stock-01,full-publication-01,resident-domain-01,stock-02,resident-domain-02,full-publication-02\n" +
                        (ScaleCheckpointLoadState.Requested ? "save=" + ScaleCheckpointLoadState.Save + "\ncheckpoint=" +
                        ScaleCheckpointLoadState.Checkpoint + "\nsourceSha256=" + ScaleCheckpointLoadState.SourceSha256 +
                        "\nsourceUnchanged=" + sourceUnchanged + "\n" : ""));
                }
                catch (Exception error) { Fail(error); yield break; }
                sweepState = valid ? "complete" : "invalid";
                Finish(valid ? "complete" : "invalid", valid ? 0 : 2);
            }
            finally { capturing = false; }
        }

        void Complete(ProbeReport report)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "markers.json"), ReportJson.Encode(report));
                string reason; bool valid = Stable(report, out reason);
                bool sourceUnchanged = !ScaleCheckpointLoadState.Requested || ScaleCheckpointLoadState.SourceUnchanged();
                if (!sourceUnchanged)
                { valid = false; reason = "source-checkpoint-changed"; }
                File.WriteAllText(Path.Combine(directory, "status.txt"), (valid ? "complete" : "invalid") + "\nreason=" + reason + "\n" +
                    (ScaleCheckpointLoadState.Requested ? "save=" + ScaleCheckpointLoadState.Save + "\ncheckpoint=" +
                    ScaleCheckpointLoadState.Checkpoint + "\nsourceSha256=" + ScaleCheckpointLoadState.SourceSha256 +
                    "\nsourceUnchanged=" + sourceUnchanged + "\n" : ""));
                Finish(valid ? "complete" : "invalid", valid ? 0 : 2);
            }
            catch (Exception error) { Fail(error); }
        }

        static bool Stable(ProbeReport report, out string reason)
        {
            reason = "verified-fixed-orbital-context";
            if (report == null || report.status != "complete" || report.completedFrames != report.requestedFrames || report.frames == null || report.frames.Length == 0)
            { reason = "incomplete-capture"; return false; }
            ProfileFrame first = report.frames[0];
            foreach (ProfileFrame frame in report.frames)
                if (frame == null || frame.vesselId != first.vesselId || frame.parts != first.parts ||
                    frame.rigidbodies != first.rigidbodies || frame.joints != first.joints || frame.colliders != first.colliders ||
                    frame.loadedVessels != first.loadedVessels || frame.loaded != true || frame.packed != false || frame.paused != false ||
                    frame.warpRate != 1 || !frame.throttleCommand.HasValue || frame.throttleCommand.Value >= 0.01 ||
                    frame.situation != Vessel.Situations.ORBITING.ToString())
                { reason = "context-or-topology-changed"; return false; }
            if (report.playerLoop == null || report.playerLoop.schema != "ksp-continuum-playerloop/v2" ||
                report.playerLoop.status != "observed" || report.playerLoop.integrityStatus != "verified-at-boundaries" ||
                report.playerLoop.cleanupStatus != "removed-owned-hooks")
            { reason = "playerloop-unqualified"; return false; }
            if (report.substitutionCanary != null &&
                (report.substitutionCanary.status != "observed-bounded-native-skip" ||
                 report.substitutionCanary.installationStatus != "candidate-installed" ||
                 report.substitutionCanary.restorationStatus != "native-node-restored" ||
                 report.substitutionCanary.candidateCallbacks < 1 || report.substitutionCanary.candidateCallbacks > 4))
            { reason = "physics-substitution-canary-invalid"; return false; }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-callback-attribution") >= 0 &&
                (report.callbackAttribution == null || report.callbackAttribution.status != "observed" ||
                 report.callbackAttribution.cleanupStatus != "removed-owned-patches" || report.callbackAttribution.patchedMethods < 1 ||
                 report.callbackAttribution.callbacks == null || report.callbackAttribution.callbacks.Length < 1))
            { reason = "callback-attribution-invalid"; return false; }
            if (report.dryBuoyancy != null && (report.dryBuoyancy.status != "complete" ||
                report.dryBuoyancy.cleanupStatus != "restored-owned-enables" || report.dryBuoyancy.ownedComponents < 1 ||
                report.dryBuoyancy.fixedSteps < 1 || report.dryBuoyancy.bypassed < 1 || report.dryBuoyancy.errors != 0))
            { reason = "dry-buoyancy-strategy-invalid"; return false; }
            return true;
        }

        void Fail(Exception error)
        {
            sweepState = "error";
            Debug.LogException(error);
            try { if (directory != null) File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); } catch { }
            Finish("error", 2);
        }
        void Finish(string status, int code)
        {
            if (finished) return;
            if (quitAfterQualification) finished = true;
            active = false;
            try { if (probe != null) probe.Dispose(); } catch (Exception error) { Debug.LogException(error); code = 2; }
            probe = null;
            if (directory != null && !File.Exists(Path.Combine(directory, "status.txt")))
                try { File.WriteAllText(Path.Combine(directory, "status.txt"), status + "\n"); } catch { code = 2; }
            if (quitAfterQualification) Application.Quit(code);
        }
        public void OnDestroy()
        {
            if (ReferenceEquals(instance, this)) instance = null;
            if (!finished && active) { sweepState = "interrupted"; Finish("interrupted", 2); }
        }
    }
}
