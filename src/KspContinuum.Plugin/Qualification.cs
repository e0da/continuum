using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Qualification : MonoBehaviour
    {
        string directory;
        Probe probe;
        FlightPanel panel;
        bool active, capturing, exiting;
        int next;
        float started;
        readonly string[] phases = { "coast", "powered", "contact" };

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-qualify") < 0) return;
            if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
            { Application.Quit(2); return; }
            try
            {
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData",
                    "qualification-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "scope.txt"),
                    "Start-context classified windows; inspect each raw frame for changes. Stock remains authoritative.\n" +
                    "Coast: near-zero throttle command, unpacked, airborne. Powered: throttle command > 0.05. Contact: landed or splashed.\n" +
                    "Throttle is a command, not proof of applied force. Contact is a native situation, not collision profiling attribution.\n");
                started = Time.realtimeSinceStartup; active = true;
            }
            catch (Exception error) { Fail(error); }
        }

        public void Update()
        {
            if (!active) return;
            if (Time.realtimeSinceStartup - started > 600) { Finish("timeout", 2); return; }
            if (capturing) return;
            if (panel == null) panel = FindObjectOfType<FlightPanel>();
            if (panel == null) return;
            if (next > 0 && !panel.ShadowRunning)
            {
                string receipt = Path.Combine(directory, phases[next - 1] + "-shadow.json");
                if (!File.Exists(receipt))
                {
                    try
                    {
                        if (string.IsNullOrEmpty(panel.ShadowReportPath)) throw new InvalidOperationException("Shadow capture produced no receipt.");
                        File.Copy(panel.ShadowReportPath, receipt, false);
                    }
                    catch (Exception error) { Fail(error); return; }
                }
            }
            if (next == phases.Length)
            {
                if (!panel.ShadowRunning) Finish("complete", 0);
                return;
            }
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || !vessel.loaded || vessel.packed || FlightDriver.Pause || TimeWarp.CurrentRate != 1) return;
            double throttle = vessel.ctrlState.mainThrottle;
            bool contact = vessel.LandedOrSplashed;
            bool selected = next == 0 ? !contact && throttle < 0.01 : next == 1 ? !contact && throttle > 0.05 : contact;
            if (!selected || panel.ShadowRunning) return;
            string phase = phases[next];
            try
            {
                File.WriteAllText(Path.Combine(directory, phase + "-start.txt"),
                    "vessel=" + vessel.id + "\nsituation=" + vessel.situation + "\nparts=" + vessel.parts.Count +
                    "\nut=" + Planetarium.GetUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "\nthrottleCommand=" + throttle.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                panel.BeginShadowCapture();
                probe = new Probe(); capturing = true;
                StartCoroutine(Capture(phase));
            }
            catch (Exception error) { Fail(error); }
        }

        IEnumerator Capture(string phase)
        {
            IEnumerator work = probe.Run(report => File.WriteAllText(Path.Combine(directory, phase + "-markers.json"), ReportJson.Encode(report)));
            try
            {
                while (active)
                {
                    bool more;
                    try { more = work.MoveNext(); }
                    catch (Exception error) { Fail(error); yield break; }
                    if (!more) break;
                    yield return work.Current;
                }
                if (active) next++;
            }
            finally
            {
                var disposable = work as IDisposable;
                if (disposable != null) disposable.Dispose();
                capturing = false;
            }
        }
        void Fail(Exception error)
        {
            Debug.LogException(error);
            try { if (directory != null) File.WriteAllText(Path.Combine(directory, "error.txt"), error.ToString()); }
            catch (Exception exportError) { Debug.LogException(exportError); }
            finally { Finish("error", 2); }
        }
        void Finish(string status, int code)
        {
            if (exiting) return;
            exiting = true; active = false;
            bool captureReleased = StopCaptures();
            int exitCode = code;
            try
            {
                QualificationShutdown.RecordCaptureCleanup(captureReleased);
                ShutdownReceipt shutdown = QualificationShutdown.Requests.Request("qualification-" + status);
                if (shutdown.HasErrors) exitCode = 2;
                File.WriteAllText(Path.Combine(directory, "shutdown.txt"), shutdown.Text(status));
            }
            catch (Exception error) { exitCode = 2; Debug.LogException(error); }
            try { File.WriteAllText(Path.Combine(directory, "status.txt"), status + "\ncompletedWindows=" + next + "\n"); }
            catch (Exception error) { exitCode = 2; Debug.LogException(error); }
            finally { Application.Quit(exitCode); }
        }
        bool StopCaptures()
        {
            bool success = true;
            try { if (probe != null) probe.Dispose(); }
            catch (Exception error) { success = false; Debug.LogException(error); }
            try { if (panel != null) panel.StopShadowCapture(); }
            catch (Exception error) { success = false; Debug.LogException(error); }
            return success;
        }
        public void OnDestroy()
        {
            StopCaptures();
            try
            {
                if (active && directory != null) File.WriteAllText(Path.Combine(directory, "status.txt"), "interrupted\ncompletedWindows=" + next + "\n");
            }
            catch (Exception error) { Debug.LogException(error); }
            finally { active = false; }
        }
    }
}
