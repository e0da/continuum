using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class LiveControlAddon : MonoBehaviour
    {
        LoopbackSnapshotServer server;
        LiveControlPlane control;
        long observedFixedCallbacks;
        int mainThreadId;
        Vessel lastVessel;
        GameScenes lastScene;
        bool hasScene;

        public void Start()
        {
            const string prefix = "--continuum-control-port=";
            string portText = null;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith(prefix, StringComparison.Ordinal)) portText = argument.Substring(prefix.Length);
            if (portText == null) return;
            int port;
            if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5 ||
                !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
            { UnityEngine.Debug.LogError("[Continuum] Invalid or unsupported read-only control request."); return; }
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            control = new LiveControlPlane(Guid.NewGuid().ToString("D"));
            try { server = new LoopbackSnapshotServer(port); }
            catch (Exception error)
            { UnityEngine.Debug.LogError("[Continuum] Control listener failed: " + error.GetType().Name); control = null; }
        }

        public void FixedUpdate() { if (server != null) observedFixedCallbacks++; }

        public void Update()
        {
            if (server == null) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("Live control capture must run on Unity's main thread.");
            Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            GameScenes scene = HighLogic.LoadedScene;
            bool replaced = !ReferenceEquals(vessel, lastVessel);
            if (!hasScene || replaced || scene != lastScene)
            {
                control.Observe(scene.ToString(), vessel == null ? null : vessel.id.ToString("D"), replaced);
                lastVessel = vessel;
                lastScene = scene;
                hasScene = true;
            }
            server.DrainOne(command => Handle(command, vessel));
        }

        string Handle(string command, Vessel vessel)
        {
            long started = Stopwatch.GetTimestamp();
            LiveControlReply reply = control.Execute(LiveControlRequest.Parse(command), Time.frameCount, observedFixedCallbacks,
                () => Capture(vessel));
            reply.observerNanoseconds = (long)((Stopwatch.GetTimestamp() - started) *
                (1000000000.0 / Stopwatch.Frequency));
            return ReportJson.Encode(reply);
        }

        static LiveVesselSnapshot Capture(Vessel vessel)
        {
            if (vessel == null) return null;
            return new LiveVesselSnapshot {
                vesselId = vessel.id.ToString("D"), name = vessel.vesselName,
                body = vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                situation = vessel.situation.ToString(), loaded = vessel.loaded, packed = vessel.packed,
                parts = vessel.parts == null ? 0 : vessel.parts.Count,
                universalTime = Planetarium.GetUniversalTime(), altitude = vessel.altitude,
                surfaceSpeed = vessel.srfSpeed, orbitalSpeed = vessel.obt_speed,
                throttle = vessel.ctrlState == null ? (double?)null : vessel.ctrlState.mainThrottle
            };
        }

        public void OnDestroy()
        {
            if (server != null) { server.Dispose(); server = null; }
        }
    }
}
