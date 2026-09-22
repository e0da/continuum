using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AeroCaptureQualification : MonoBehaviour
    {
        const float TimeoutSeconds = 120f;
        const float SettleSeconds = 2f;
        string directory;
        string vesselId;
        int partCount;
        float started;
        float eligibleSince;
        AeroCaptureSession capture;
        bool active;
        bool finished;

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-aero-capture-001") < 0) return;
            try
            {
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                    "aero-capture-001-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "scope.txt"),
                    "AERO-CAPTURE-001: one active, unpacked stock vessel in a controlled atmospheric flight state.\n" +
                    "Stock remains authoritative. Capture is read only, bounded to 64 physics samples and 128 parts per sample.\n" +
                    "The run waits two seconds for an eligible state and exits after completion or a 120 second wall-clock timeout.\n");
                if (!Supported()) { Finish("unsupported-version", 2, "KSP 1.12.5 is required."); return; }
                started = Time.realtimeSinceStartup;
                active = true;
            }
            catch (Exception error) { Fail(error); }
        }

        public void Update()
        {
            if (!active) return;
            if (Time.realtimeSinceStartup - started > TimeoutSeconds)
            { Finish("timeout", 2, "No complete bounded capture before the wall-clock timeout."); return; }

            Vessel vessel = EligibleVessel();
            if (capture == null)
            {
                if (vessel == null) { eligibleSince = 0; return; }
                if (eligibleSince == 0) { eligibleSince = Time.realtimeSinceStartup; return; }
                if (Time.realtimeSinceStartup - eligibleSince < SettleSeconds) return;
                Begin(vessel);
                return;
            }

            if (vessel == null || vessel.id.ToString("D") != vesselId || vessel.parts.Count != partCount)
            { Finish("invalid", 2, "The qualified vessel or atmospheric flight context changed during capture."); return; }
            if (capture.ReachedBound) Finish("complete", 0, "Captured the declared 64-sample bound.");
        }

        void Begin(Vessel vessel)
        {
            vesselId = vessel.id.ToString("D");
            partCount = vessel.parts.Count;
            File.WriteAllText(Path.Combine(directory, "start.txt"),
                "vessel=" + vesselId + "\nparts=" + partCount.ToString(CultureInfo.InvariantCulture) +
                "\nbody=" + vessel.mainBody.bodyName + "\nsituation=" + vessel.situation +
                "\nut=" + Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture) +
                "\ndensity=" + vessel.atmDensity.ToString("R", CultureInfo.InvariantCulture) + "\n");
            capture = new AeroCaptureSession();
            capture.Start();
            if (capture.Report != null) Finish("invalid", 2, "Capture could not install the pinned stock observation seam.");
        }

        static Vessel EligibleVessel()
        {
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || !vessel.loaded || vessel.packed || FlightDriver.Pause || TimeWarp.CurrentRate != 1 ||
                Time.timeScale != 1 || vessel.mainBody == null || !vessel.mainBody.atmosphere || vessel.atmDensity <= 0 ||
                vessel.LandedOrSplashed || vessel.parts == null || vessel.parts.Count < 1 ||
                vessel.parts.Count > AeroCaptureReport.MaximumPartsPerSample) return null;
            return vessel;
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
            finished = true;
            active = false;
            bool captureReleased = true;
            AeroCaptureReport report = null;
            try
            {
                if (capture != null)
                {
                    capture.Dispose();
                    report = capture.Report;
                    WriteAtomic(Path.Combine(directory, "aero-capture.json"), ReportJson.Encode(report));
                }
            }
            catch (Exception error) { captureReleased = false; code = 2; status = "error"; reason = error.GetType().Name; Debug.LogException(error); }

            if (status == "complete" && (report == null || report.disposition != AeroCaptureDisposition.Valid ||
                report.samples.Count != AeroCaptureReport.MaximumSamples))
            { status = "invalid"; reason = "The terminal capture receipt did not contain the declared valid sample bound."; code = 2; }

            try
            {
                QualificationShutdown.RecordCaptureCleanup(captureReleased && report != null && report.cleanup == AeroCleanupOutcome.RemovedOwnedPatches);
                ShutdownReceipt shutdown = QualificationShutdown.Requests.Request("aero-capture-001-" + status);
                if (shutdown.HasErrors) code = 2;
                string captureStatus = status == "complete" || status == "timeout" || status == "interrupted" ? status : "error";
                WriteAtomic(Path.Combine(directory, "shutdown.txt"), shutdown.Text(captureStatus));
                WriteAtomic(Path.Combine(directory, "status.txt"),
                    status + "\nreason=" + reason.Replace('\n', ' ') + "\nsamples=" +
                    (report == null ? "0" : report.samples.Count.ToString(CultureInfo.InvariantCulture)) + "\n");
            }
            catch (Exception error) { code = 2; Debug.LogException(error); }
            Application.Quit(code);
        }

        static void WriteAtomic(string path, string text)
        {
            string temporary = path + ".tmp";
            try { File.WriteAllText(temporary, text); File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static bool Supported()
        {
            return Versioning.version_major == 1 && Versioning.version_minor == 12 && Versioning.Revision == 5;
        }

        public void OnDestroy()
        {
            if (active && !finished) Finish("interrupted", 2, "The flight scene ended before capture completed.");
        }
    }
}
