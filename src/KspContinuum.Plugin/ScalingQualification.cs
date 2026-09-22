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
        Probe probe;
        string directory;
        bool active, capturing, finished;
        float eligibleSince;
        int expectedParts = -1;

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "--continuum-scale-profile") < 0) return;
            try
            {
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                    "scale-profile-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                if (Array.IndexOf(arguments, "--continuum-playerloop") < 0)
                { Finish("missing-playerloop-flag", 2); return; }
                foreach (string argument in arguments) if (argument.StartsWith("--continuum-scale-parts=", StringComparison.Ordinal))
                {
                    int parsed;
                    if (!int.TryParse(argument.Substring("--continuum-scale-parts=".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out parsed) || parsed < 1) { Finish("invalid-part-count", 2); return; }
                    expectedParts = parsed;
                }
                File.WriteAllText(Path.Combine(directory, "scope.txt"),
                    "Single settled stock-vessel orbital window. PlayerLoop scopes overlap and must not be summed.\n" +
                    "Stock remains authoritative; this run measures scaling and does not replace physics.\n");
                active = true;
            }
            catch (Exception error) { Fail(error); }
        }

        public void Update()
        {
            if (!active || capturing) return;
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            bool eligible = vessel != null && vessel.loaded && !vessel.packed && !FlightDriver.Pause &&
                TimeWarp.CurrentRate == 1 && vessel.situation == Vessel.Situations.ORBITING &&
                vessel.ctrlState != null && vessel.ctrlState.mainThrottle < 0.01 &&
                (expectedParts < 1 || vessel.parts.Count == expectedParts);
            if (!eligible) { eligibleSince = 0; return; }
            if (eligibleSince == 0) { eligibleSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - eligibleSince < 10) return;
            probe = new Probe(); capturing = true; StartCoroutine(Capture());
        }

        IEnumerator Capture()
        {
            IEnumerator work = probe.Run(Complete);
            try { while (active && work.MoveNext()) yield return work.Current; }
            finally { var disposable = work as IDisposable; if (disposable != null) disposable.Dispose(); capturing = false; }
        }

        void Complete(ProbeReport report)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "markers.json"), ReportJson.Encode(report));
                string reason; bool valid = Stable(report, out reason);
                File.WriteAllText(Path.Combine(directory, "status.txt"), (valid ? "complete" : "invalid") + "\nreason=" + reason + "\n");
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
                (report.substitutionCanary.status != "observed-skipped-native-tick" ||
                 report.substitutionCanary.installationStatus != "candidate-installed" ||
                 report.substitutionCanary.restorationStatus != "native-node-restored" ||
                 report.substitutionCanary.candidateCallbacks != 1))
            { reason = "physics-substitution-canary-invalid"; return false; }
            return true;
        }

        void Fail(Exception error)
        {
            Debug.LogException(error);
            try { if (directory != null) File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); } catch { }
            Finish("error", 2);
        }
        void Finish(string status, int code)
        {
            if (finished) return; finished = true; active = false;
            try { if (probe != null) probe.Dispose(); } catch (Exception error) { Debug.LogException(error); code = 2; }
            if (directory != null && !File.Exists(Path.Combine(directory, "status.txt")))
                try { File.WriteAllText(Path.Combine(directory, "status.txt"), status + "\n"); } catch { code = 2; }
            Application.Quit(code);
        }
        public void OnDestroy() { if (!finished && active) Finish("interrupted", 2); }
    }
}
