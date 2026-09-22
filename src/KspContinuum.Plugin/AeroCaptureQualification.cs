using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class AeroCaptureQualification : MonoBehaviour
    {
        const float TimeoutSeconds = 120f, SettleSeconds = 2f;
        string directory, sourcePath, sourceHash, saveName, checkpointName, vesselId;
        int partCount, menuReadyFrame = -1;
        float started, eligibleSince;
        Guid expectedVesselId;
        Game loadedGame;
        AeroCaptureSession capture;
        bool active, finished, loading;

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "--continuum-aero-capture-001") < 0) return;
            active = true; started = Time.realtimeSinceStartup;
            DontDestroyOnLoad(gameObject);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnGuiReady);
            try
            {
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                    "aero-capture-001-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                AeroQualificationSelection selection = AeroQualificationSelection.Parse(arguments);
                saveName = selection.Save; checkpointName = selection.Checkpoint;
                File.WriteAllText(Path.Combine(directory, "scope.txt"),
                    "AERO-CAPTURE-001: one named, immutable stock checkpoint loaded from the native main menu.\n" +
                    "Stock remains authoritative. Capture is read only, bounded to 64 physics samples and 128 parts per sample.\n" +
                    "The run waits two seconds for an eligible state and exits after completion or a 120 second wall-clock timeout.\n");
                if (!Supported()) { Finish("unsupported-version", 2, "KSP 1.12.5 is required."); return; }
                sourcePath = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName, checkpointName + ".sfs");
                RejectSourcePath(); sourceHash = HashFile(sourcePath);
            }
            catch (Exception error) { Fail(error); }
        }

        void OnGuiReady(GameScenes scene)
        {
            if (active && !loading && scene == GameScenes.MAINMENU) menuReadyFrame = Time.frameCount;
        }

        public void Update()
        {
            if (!active) return;
            if (Time.realtimeSinceStartup - started > TimeoutSeconds)
            { Finish("timeout", 2, "No complete bounded capture before the wall-clock timeout."); return; }
            try
            {
                if (!loading)
                {
                    if (menuReadyFrame < 0 || Time.frameCount <= menuReadyFrame) return;
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU) throw new InvalidOperationException("Scene changed before checkpoint loading.");
                    LoadCheckpoint(); return;
                }
                if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;
                if (!ReferenceEquals(HighLogic.CurrentGame, loadedGame) || HighLogic.SaveFolder != saveName)
                    throw new InvalidOperationException("Loaded game identity does not match the requested checkpoint.");
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || vessel.id != expectedVesselId)
                    throw new InvalidOperationException("Active vessel identity does not match the checkpoint active vessel.");
                RunCapture(vessel);
            }
            catch (Exception error) { Fail(error); }
        }

        void LoadCheckpoint()
        {
            loadedGame = GamePersistence.LoadGame(checkpointName, saveName, true, true);
            if (loadedGame == null || !loadedGame.compatible || loadedGame.flightState == null ||
                loadedGame.flightState.activeVesselIdx < 0 || loadedGame.flightState.activeVesselIdx >= loadedGame.flightState.protoVessels.Count)
                throw new InvalidOperationException("Checkpoint is not a compatible flight with an active vessel.");
            expectedVesselId = loadedGame.flightState.protoVessels[loadedGame.flightState.activeVesselIdx].vesselID;
            HighLogic.SaveFolder = saveName; HighLogic.CurrentGame = loadedGame;
            loadedGame.startScene = GameScenes.FLIGHT; loading = true; loadedGame.Start();
        }

        void RunCapture(Vessel vessel)
        {
            Vessel eligible = EligibleVessel(vessel);
            if (capture == null)
            {
                if (eligible == null) { eligibleSince = 0; return; }
                if (eligibleSince == 0) { eligibleSince = Time.realtimeSinceStartup; return; }
                if (Time.realtimeSinceStartup - eligibleSince < SettleSeconds) return;
                Begin(eligible); return;
            }
            if (capture.Report != null)
            {
                Finish("invalid", 2, "Capture terminated before reaching its bound: " +
                    capture.Report.reason + (string.IsNullOrEmpty(capture.FailureDetail) ? "." : " (" + capture.FailureDetail + ")."));
                return;
            }
            if (eligible == null || vessel.id.ToString("D") != vesselId || vessel.parts.Count != partCount)
            { Finish("invalid", 2, "The qualified vessel or atmospheric flight context changed during capture."); return; }
            if (capture.ReachedBound) Finish("complete", 0, "Captured the declared 64-sample bound.");
        }

        void Begin(Vessel vessel)
        {
            vesselId = vessel.id.ToString("D"); partCount = vessel.parts.Count;
            File.WriteAllText(Path.Combine(directory, "start.txt"),
                "save=" + saveName + "\ncheckpoint=" + checkpointName + "\nsourceSha256=" + sourceHash +
                "\nvessel=" + vesselId + "\nparts=" + partCount.ToString(CultureInfo.InvariantCulture) +
                "\nbody=" + vessel.mainBody.bodyName + "\nsituation=" + vessel.situation +
                "\nut=" + Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture) +
                "\ndensity=" + vessel.atmDensity.ToString("R", CultureInfo.InvariantCulture) + "\n");
            capture = new AeroCaptureSession(); capture.Start();
            if (capture.Report != null) Finish("invalid", 2, "Capture could not install the pinned stock observation seam.");
        }

        static Vessel EligibleVessel(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded || vessel.packed || FlightDriver.Pause || TimeWarp.CurrentRate != 1 ||
                Time.timeScale != 1 || vessel.mainBody == null || !vessel.mainBody.atmosphere || vessel.atmDensity <= 0 ||
                vessel.LandedOrSplashed || vessel.parts == null || vessel.parts.Count < 1 ||
                vessel.parts.Count > AeroCaptureReport.MaximumPartsPerSample) return null;
            return vessel;
        }

        void RejectSourcePath()
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(sourcePath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                throw new InvalidOperationException("Requested aerodynamic checkpoint does not exist inside this KSP instance.");
            for (string current = full; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic aerodynamic checkpoint paths are not supported.");
                if (current == root) return;
            }
            throw new InvalidOperationException("Aerodynamic checkpoint escaped the KSP instance root.");
        }

        void Fail(Exception error)
        {
            Debug.LogException(error);
            try { if (directory != null) File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); }
            catch (Exception exportError) { Debug.LogException(exportError); }
            Finish("error", 2, error.GetType().Name);
        }

        void Finish(string status, int code, string reason)
        {
            if (finished) return;
            finished = true; active = false; GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            bool captureReleased = true, sourceUnchanged = sourcePath != null && sourceHash != null;
            AeroCaptureReport report = null;
            try
            {
                if (capture != null) { capture.Dispose(); report = capture.Report; WriteAtomic(Path.Combine(directory, "aero-capture.json"), ReportJson.Encode(report)); }
                if (sourceUnchanged && HashFile(sourcePath) != sourceHash)
                    throw new InvalidOperationException("Source checkpoint changed during qualification.");
            }
            catch (Exception error)
            { captureReleased = false; sourceUnchanged = false; code = 2; status = "error"; reason = error.GetType().Name; Debug.LogException(error); }
            if (status == "complete" && (report == null || report.disposition != AeroCaptureDisposition.Valid || report.samples.Count != AeroCaptureReport.MaximumSamples))
            { status = "invalid"; reason = "The terminal capture receipt did not contain the declared valid sample bound."; code = 2; }
            try
            {
                QualificationShutdown.RecordCaptureCleanup(captureReleased && report != null && report.cleanup == AeroCleanupOutcome.RemovedOwnedPatches);
                ShutdownReceipt shutdown = QualificationShutdown.Requests.Request("aero-capture-001-" + status);
                if (shutdown.HasErrors) code = 2;
                string captureStatus = status == "complete" || status == "timeout" || status == "interrupted" ? status : "error";
                WriteAtomic(Path.Combine(directory, "shutdown.txt"), shutdown.Text(captureStatus));
                WriteAtomic(Path.Combine(directory, "status.txt"), status + "\nreason=" + reason.Replace('\n', ' ') + "\nsamples=" +
                    (report == null ? "0" : report.samples.Count.ToString(CultureInfo.InvariantCulture)) + "\nsourceUnchanged=" +
                    sourceUnchanged + "\n");
            }
            catch (Exception error) { code = 2; Debug.LogException(error); }
            Application.Quit(code);
        }

        static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static void WriteAtomic(string path, string text)
        {
            string temporary = path + ".tmp";
            try { File.WriteAllText(temporary, text); File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        static bool Supported() { return Versioning.version_major == 1 && Versioning.version_minor == 12 && Versioning.Revision == 5; }
        public void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            if (active && !finished) Finish("interrupted", 2, "The qualification controller was destroyed before capture completed.");
        }
    }
}
