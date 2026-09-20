using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using MuMech;
using KSP.UI.Screens;
using UnityEngine;

namespace KspContinuum.Mission
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class MissionAddon : MonoBehaviour
    {
        enum Phase { Dormant, SpaceCenter, Flight, Replay, Attach, Armed, Ascent, Transfer, Correction, Coast, Capture, Landing, Settling, Done }
        Phase phase;
        readonly LandingAcceptance acceptance = new LandingAcceptance();
        readonly MissionCleanup cleanup = new MissionCleanup();
        readonly HashSet<uint> landerParts = new HashSet<uint>();
        readonly Dictionary<string, double> screenshots = new Dictionary<string, double>();
        FlightTimeline timeline;
        MechJebCore core;
        Vessel vessel;
        CelestialBody minmus;
        StreamWriter telemetry;
        string directory, saveName;
        double phaseWall, startedWall, phaseUT, lastTelemetry = -1, nextAction;
        uint commandId;
        bool active;
        const double OrbitAltitude = 100000;
        const double EncounterPeriapsis = 25000;

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-minmus") < 0) return;
            active = true;
            DontDestroyOnLoad(gameObject);
            startedWall = Time.realtimeSinceStartup;
            try
            {
                FlightTimeline.RequireOfflineControl();
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("Mission requires KSP 1.12.5.");
                string assemblyVersion = typeof(MechJebCore).Assembly.GetName().Version.ToString();
                var fileVersionAttribute = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(MechJebCore).Assembly, typeof(AssemblyFileVersionAttribute));
                string fileVersion = fileVersionAttribute == null ? null : fileVersionAttribute.Version;
                if (!MissionCompatibility.IsSupported(assemblyVersion, fileVersion))
                    throw new InvalidOperationException("Mission requires MechJeb 2.15.3.0.");
                string id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                saveName = "Continuum-Minmus-" + id;
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData", "mission-" + id);
                Directory.CreateDirectory(directory);
                telemetry = new StreamWriter(Path.Combine(directory, "mission.csv"));
                telemetry.AutoFlush = true;
                telemetry.WriteLine("wall_s,ut_s,phase,body,situation,altitude_m,apoapsis_m,periapsis_m,surface_speed_mps,throttle,stage,parts,packed,autopilot");
                File.WriteAllText(Path.Combine(directory, "mission.txt"), "status=running\nsave=" + saveName + "\ncraft=Ships/VAB/Kerbal X.craft\nmechjebAssemblyVersion=" + assemblyVersion + "\nmechjebFileVersion=" + fileVersion + "\n");
                if (Directory.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName)))
                    throw new InvalidOperationException("Refusing existing save directory.");
                var parameters = GameParameters.GetDefaultParameters(Game.Modes.SANDBOX, GameParameters.Preset.Normal);
                HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveName, Game.Modes.SANDBOX, parameters,
                    "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
                Move(Phase.SpaceCenter);
                HighLogic.CurrentGame.Start();
            }
            catch (Exception ex) { Fail(ex); }
        }

        public void Update()
        {
            if (!active) return;
            CheckScreenshots();
            if (phase == Phase.Done) return;
            try
            {
                FlightTimeline.RequireOfflineControl();
                if (Time.realtimeSinceStartup - startedWall > 14400) throw new TimeoutException("Mission exceeded four wall-clock hours.");
                if (Time.realtimeSinceStartup - phaseWall > PhaseTimeout()) throw new TimeoutException("Phase timed out: " + phase);
                if (timeline != null) timeline.Tick();
                if (phase >= Phase.Armed && phase <= Phase.Settling && !timeline.IsRecording)
                    throw new InvalidOperationException("Input recording stopped: " + timeline.Status);
                if (phase >= Phase.Transfer && phase <= Phase.Settling && vessel != null && vessel.currentStage <= 2)
                {
                    Release("staging");
                    if (vessel.currentStage < 2) throw new InvalidOperationException("Staging crossed the protected lander boundary.");
                }
                if (Time.realtimeSinceStartup - lastTelemetry >= 1) { WriteTelemetry(); lastTelemetry = Time.realtimeSinceStartup; }
                if (phase >= Phase.Replay && phase < Phase.Done)
                {
                    if (!HighLogic.LoadedSceneIsFlight) throw new InvalidOperationException("Unexpected scene departure.");
                    if (vessel == null || FlightGlobals.ActiveVessel != vessel || !vessel.loaded)
                        throw new InvalidOperationException("Mission vessel destroyed, unloaded or focus changed.");
                    if (!vessel.parts.Any(p => p.flightID == commandId)) throw new InvalidOperationException("Command part lost.");
                    if (FlightDriver.Pause) return;
                }
                switch (phase)
                {
                    case Phase.SpaceCenter:
                        if (HighLogic.LoadedScene != GameScenes.SPACECENTER || Time.realtimeSinceStartup - phaseWall < 5) return;
                        Launch(); break;
                    case Phase.Flight:
                        if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) return;
                        vessel = FlightGlobals.ActiveVessel;
                        if (vessel.situation != Vessel.Situations.PRELAUNCH) throw new InvalidOperationException("Expected stock craft on launchpad.");
                        commandId = vessel.rootPart.flightID;
                        foreach (Part part in vessel.parts)
                            if (part.Modules.Contains("ModuleLandingLeg") || part.Modules.Contains("ModuleWheelDeployment") ||
                                (part.inverseStage == 2 && part.FindModuleImplementing<ModuleEngines>() != null)) landerParts.Add(part.flightID);
                        if (landerParts.Count < 2) throw new InvalidOperationException("Stock lander engine/gear fingerprint missing.");
                        if (Time.realtimeSinceStartup - phaseWall < 15 || vessel.HoldPhysics || !vessel.IsControllable) return;
                        timeline = new FlightTimeline();
                        cleanup.Track("timeline", () => timeline.Dispose());
                        core = vessel.GetMasterMechJeb();
                        if (core != null)
                        {
                            File.AppendAllText(Path.Combine(directory, "mission.txt"), "neutralReplay=NOT RUN: installed MechJeb controller owns a vessel callback; strict replay guard preserved.\n");
                            Debug.Log("[ContinuumMission] Neutral replay NOT RUN: reusing installed MechJeb owner for recorded mission.");
                            RequestScreenshot("launchpad");
                            Move(Phase.Attach); break;
                        }
                        vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                        vessel.Autopilot.Disable();
                        timeline.BeginReplay(vessel, NeutralTimeline());
                        RequestScreenshot("launchpad");
                        Move(Phase.Replay); break;
                    case Phase.Replay:
                        if (Planetarium.GetUniversalTime() - phaseUT < 3) return;
                        if (timeline.IsReplaying || timeline.Status != "Replay complete.")
                            throw new InvalidOperationException("Neutral replay did not complete: " + timeline.Status);
                        if (vessel.ctrlState.mainThrottle != 0 || vessel.situation != Vessel.Situations.PRELAUNCH)
                            throw new InvalidOperationException("Neutral replay changed prelaunch state.");
                        File.AppendAllText(Path.Combine(directory, "mission.txt"), "neutralReplay=" + timeline.Status + "\n");
                        timeline.Stop();
                        AttachAutopilot(); Move(Phase.Attach); break;
                    case Phase.Attach:
                        if (Planetarium.GetUniversalTime() - phaseUT < 3) return;
                        if (vessel.GetMasterMechJeb() != core || core.AscentSettings == null) throw new InvalidOperationException("MechJeb initialization failed.");
                        if (core.Ascent.Enabled || core.Node.Enabled || core.Landing.Enabled)
                            throw new InvalidOperationException("Existing MechJeb mission controller is already active.");
                        vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                        vessel.Autopilot.Disable();
                        minmus = FlightGlobals.Bodies.Single(body => body.bodyName == "Minmus");
                        timeline.BeginRecording(vessel);
                        File.AppendAllText(Path.Combine(directory, "mission.txt"), "inputDirectory=" + timeline.OutputDirectory + "\n");
                        core.Staging.AutostageLimit.Val = 2;
                        var staging = core.Staging;
                        cleanup.Track("staging-limit", () => staging.AutoStageLimitRemove(this));
                        core.Staging.AutoStageLimitRequest(2, this);
                        core.AscentSettings.AscentType = AscentType.CLASSIC;
                        core.AscentSettings.DesiredOrbitAltitude.Val = OrbitAltitude;
                        core.AscentSettings.DesiredInclination.Val = 0;
                        core.AscentSettings.Autostage = true;
                        core.AscentSettings.SkipCircularization = false;
                        Move(Phase.Armed); break;
                    case Phase.Armed:
                        if (Planetarium.GetUniversalTime() - phaseUT < 0.5) return;
                        Vessel controlledVessel = vessel;
                        cleanup.Track("throttle", () =>
                        {
                            if (controlledVessel != null) controlledVessel.ctrlState.mainThrottle = 0;
                            if (FlightGlobals.ActiveVessel == controlledVessel) FlightInputHandler.state.mainThrottle = 0;
                        });
                        var warp = core.Warp;
                        cleanup.Track("warp", () => { if (FlightGlobals.ActiveVessel == controlledVessel) warp.MinimumWarp(true); });
                        TrackController("ascent", core.Ascent);
                        core.Ascent.Users.Add(this);
                        StageManager.ActivateNextStage(); Move(Phase.Ascent); break;
                    case Phase.Ascent:
                        if (core.Ascent.Enabled) return;
                        if (vessel.orbit.PeA < 70000 || vessel.orbit.eccentricity >= 1) throw new InvalidOperationException("Ascent ended without safe Kerbin orbit.");
                        SaveMilestone("kerbin-orbit");
                        core.Target.Set(minmus);
                        core.AscentSettings.Autostage = false;
                        // LKO can be reached before the launch stage is exhausted; retain staging through transfer, bounded at 2.
                        if (vessel.currentStage > 2) { TrackController("staging", core.Staging); core.Staging.Users.Add(this); }
                        // The transfer operation cannot plan a body's SOI insertion correctly.
                        Queue(new OperationGeneric { Capture = false, PlanCapture = false, Rendezvous = true, Coplanar = false });
                        Move(Phase.Transfer); break;
                    case Phase.Transfer:
                        if (!NodesFinished()) return;
                        var correction = new OperationCourseCorrection();
                        correction.CourseCorrectFinalPeA.Val = EncounterPeriapsis;
                        Queue(correction); Move(Phase.Correction); break;
                    case Phase.Correction:
                        if (!NodesFinished()) return;
                        Orbit encounter = FindEncounter();
                        if (encounter == null || encounter.PeA < 10000 || encounter.PeA > 100000)
                            throw new InvalidOperationException("Correction did not produce a safe Minmus encounter.");
                        SaveMilestone("minmus-encounter-planned");
                        nextAction = encounter.StartUT + 2;
                        core.Warp.WarpToUT(nextAction); Move(Phase.Coast); break;
                    case Phase.Coast:
                        if (vessel.mainBody != minmus) return;
                        core.Warp.MinimumWarp(true);
                        if (vessel.packed || TimeWarp.CurrentRate != 1) return;
                        double periapsisUT = vessel.orbit.NextPeriapsisTime(Planetarium.GetUniversalTime());
                        if (vessel.orbit.PeA < 10000 || periapsisUT <= Planetarium.GetUniversalTime()) throw new InvalidOperationException("Unsafe actual Minmus capture geometry.");
                        vessel.PlaceManeuverNode(vessel.orbit, OrbitalManeuverCalculator.DeltaVToCircularize(vessel.orbit, periapsisUT), periapsisUT);
                        TrackController("node", core.Node);
                        core.Node.ExecuteAllNodes(this); Move(Phase.Capture); break;
                    case Phase.Capture:
                        if (!NodesFinished()) return;
                        if (vessel.mainBody != minmus || vessel.orbit.eccentricity >= 1 || vessel.orbit.PeA < 5000)
                            throw new InvalidOperationException("Capture burn did not leave safe Minmus orbit.");
                        SaveMilestone("minmus-orbit");
                        core.Warp.MinimumWarp(true);
                        core.Landing.TouchdownSpeed.Val = 0.5;
                        core.Landing.DeployGears = true;
                        core.Landing.DeployChutes = false;
                        core.Landing.RCSAdjustment = false;
                        TrackController("landing", core.Landing);
                        core.Landing.LandUntargeted(this); Move(Phase.Landing);
                        SaveMilestone("descent-start"); break;
                    case Phase.Landing:
                        if (vessel.situation == Vessel.Situations.LANDED)
                        {
                            Release("landing"); core.Warp.MinimumWarp(true);
                            FlightInputHandler.state.mainThrottle = vessel.ctrlState.mainThrottle = 0;
                            Move(Phase.Settling);
                        }
                        else if (!core.Landing.Enabled) throw new InvalidOperationException("Landing autopilot stopped before touchdown.");
                        break;
                    case Phase.Settling:
                        bool survivors = landerParts.All(id => vessel.parts.Any(p => p.flightID == id));
                        bool valid = LandingAcceptance.Qualifies(vessel.mainBody.bodyName, vessel.situation == Vessel.Situations.LANDED,
                            survivors && vessel.rootPart.flightID == commandId, vessel.ctrlState.mainThrottle, vessel.srfSpeed,
                            TimeWarp.CurrentRate == 1 && !vessel.packed && !FlightDriver.Pause);
                        if (acceptance.Observe(Planetarium.GetUniversalTime(), valid)) Finish(true, "Landed on Minmus; command, engine and gear survive; settled 30 simulation seconds.");
                        break;
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        void Launch()
        {
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "Ships", "VAB", "Kerbal X.craft");
            EditorDriver.editorFacility = EditorFacility.VAB;
            ShipTemplate template = ShipConstruction.LoadTemplate(path);
            if (template == null) throw new InvalidOperationException("Stock Kerbal X template unavailable.");
            VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(template.config);
            manifest = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(template.config, manifest, true, false);
            if (manifest == null) throw new InvalidOperationException("Crew manifest unavailable.");
            if (ShipConstruction.FindVesselsLandedAt(HighLogic.CurrentGame.flightState, "LaunchPad").Count != 0)
                throw new InvalidOperationException("Launchpad is occupied; refusing cleanup.");
            Move(Phase.Flight);
            FlightDriver.StartWithNewLaunch(path, HighLogic.CurrentGame.flagURL, "LaunchPad", manifest);
        }

        void AttachAutopilot()
        {
            if (vessel.GetMasterMechJeb() != null) throw new InvalidOperationException("Unexpected pre-existing MechJeb owner.");
            var module = new ConfigNode("MODULE"); module.AddValue("name", "MechJebCore");
            core = vessel.rootPart.AddModule(module) as MechJebCore;
            if (core == null) throw new InvalidOperationException("Unable to attach mission-only MechJeb module.");
            core.OnStart(PartModule.StartState.PreLaunch);
        }

        static InputTimeline NeutralTimeline()
        {
            var tracks = new List<ScalarTrack>();
            string[] names = { "mainThrottle", "roll", "yaw", "pitch", "rollTrim", "yawTrim", "pitchTrim", "wheelSteer", "wheelSteerTrim", "wheelThrottle", "wheelThrottleTrim", "X", "Y", "Z", "killRot", "gearUp", "gearDown", "headlight" };
            foreach (string name in names)
            {
                bool positive = name == "mainThrottle" || name == "killRot" || name == "gearUp" || name == "gearDown" || name == "headlight";
                tracks.Add(new ScalarTrack(name, positive ? 0 : -1, 1, 2, new[] { new TimelineKey(0, 0, TimelineMode.Step) }));
            }
            return new InputTimeline(2, tracks, new TimelineEvent[0]);
        }

        void Queue(Operation operation)
        {
            if (vessel.patchedConicSolver.maneuverNodes.Count != 0) throw new InvalidOperationException("Unexpected existing maneuver node.");
            List<ManeuverParameters> nodes = operation.MakeNodes(vessel.orbit, Planetarium.GetUniversalTime() + 30, core.Target);
            if (nodes == null || nodes.Count == 0) throw new InvalidOperationException("Maneuver planning failed: " + operation.GetErrorMessage());
            foreach (ManeuverParameters node in nodes)
            {
                if (double.IsNaN(node.UT) || double.IsInfinity(node.UT) || node.UT <= Planetarium.GetUniversalTime() ||
                    double.IsNaN(node.dV.magnitude) || double.IsInfinity(node.dV.magnitude)) throw new InvalidOperationException("Invalid planned maneuver.");
                vessel.PlaceManeuverNode(vessel.orbit, node.dV, node.UT);
            }
            core.Node.Autowarp = true;
            TrackController("node", core.Node);
            core.Node.ExecuteAllNodes(this);
        }

        bool NodesFinished()
        {
            if (core.Node.Enabled) return false;
            if (vessel.patchedConicSolver.maneuverNodes.Count != 0) throw new InvalidOperationException("Node executor stopped with unfinished maneuver.");
            core.Warp.MinimumWarp(true);
            return !vessel.packed && TimeWarp.CurrentRate == 1;
        }

        Orbit FindEncounter()
        {
            Orbit orbit = vessel.orbit;
            for (int i = 0; orbit != null && i < 10; i++, orbit = orbit.nextPatch)
                if (orbit.referenceBody == minmus) return orbit;
            return null;
        }

        double PhaseTimeout()
        {
            if (phase == Phase.SpaceCenter || phase == Phase.Flight) return 300;
            if (phase == Phase.Replay || phase == Phase.Attach || phase == Phase.Armed || phase == Phase.Settling) return 180;
            return 3600;
        }

        void Move(Phase next)
        {
            phase = next; phaseWall = Time.realtimeSinceStartup; phaseUT = Planetarium.GetUniversalTime();
            Debug.Log("[ContinuumMission] " + next);
            if (directory != null) File.AppendAllText(Path.Combine(directory, "events.txt"), F(phaseUT) + " " + next + "\n");
        }

        void WriteTelemetry()
        {
            if (telemetry == null) return;
            Vessel current = vessel != null ? vessel : FlightGlobals.ActiveVessel;
            string status = core == null ? "" : phase == Phase.Ascent ? core.Ascent.Status : phase == Phase.Landing ? core.Landing.Status : core.Node.State.ToString();
            telemetry.WriteLine(string.Join(",", F(Time.realtimeSinceStartup - startedWall), F(Planetarium.GetUniversalTime()), phase.ToString(),
                current == null ? "" : current.mainBody.bodyName, current == null ? "" : current.situation.ToString(),
                current == null ? "" : F(current.altitude), current == null ? "" : F(current.orbit.ApA), current == null ? "" : F(current.orbit.PeA),
                current == null ? "" : F(current.srfSpeed), current == null ? "" : F(current.ctrlState.mainThrottle),
                current == null ? "" : current.currentStage.ToString(CultureInfo.InvariantCulture), current == null ? "" : current.parts.Count.ToString(CultureInfo.InvariantCulture),
                current == null ? "" : current.packed.ToString(), "\"" + status.Replace("\"", "\"\"") + "\""));
        }

        static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        void SaveMilestone(string milestone)
        {
            if (HighLogic.CurrentGame == null || HighLogic.SaveFolder != saveName)
                throw new InvalidOperationException("Refusing to save outside the mission sandbox.");
            string name = milestone + "-" + Guid.NewGuid().ToString("N");
            string relative = "saves/" + saveName + "/" + name + ".sfs";
            if (File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, relative)))
                throw new InvalidOperationException("Refusing to replace an existing milestone.");
            GamePersistence.SaveGame(name, saveName, SaveMode.ABORT);
            if (!File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, relative)))
                throw new IOException("Milestone save was not created.");
            File.AppendAllText(Path.Combine(directory, "milestones.csv"), F(Planetarium.GetUniversalTime()) + "," + phase + "," + relative + "\n");
            Debug.Log("[ContinuumMission] Milestone " + milestone + " at UT " + F(Planetarium.GetUniversalTime()));
            RequestScreenshot(milestone);
        }

        void RequestScreenshot(string milestone)
        {
            if (Application.isBatchMode || directory == null) return;
            string file = milestone + "-" + Guid.NewGuid().ToString("N") + ".png";
            try
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(directory, file));
                screenshots.Add(file, Time.realtimeSinceStartup);
                File.AppendAllText(Path.Combine(directory, "screenshots.csv"), F(Planetarium.GetUniversalTime()) + ",requested," + file + "\n");
            }
            catch (Exception error) { Debug.LogWarning("[ContinuumMission] Screenshot request failed: " + error.Message); }
        }

        void CheckScreenshots()
        {
            foreach (var item in screenshots.ToArray())
            {
                try
                {
                    string path = Path.Combine(directory, item.Key);
                    bool complete = false;
                    if (File.Exists(path))
                    {
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (file.Length >= 20)
                            {
                                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                                complete = signature.All(value => file.ReadByte() == value);
                                file.Seek(-12, SeekOrigin.End);
                                byte[] ending = { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
                                complete = complete && ending.All(value => file.ReadByte() == value);
                            }
                        }
                    }
                    if (complete || Time.realtimeSinceStartup - item.Value > 30)
                    {
                        File.AppendAllText(Path.Combine(directory, "screenshots.csv"), F(Planetarium.GetUniversalTime()) + "," +
                            (complete ? "png-written" : "unconfirmed") + "," + item.Key + "\n");
                        screenshots.Remove(item.Key);
                    }
                }
                catch (IOException) { }
                catch (Exception error)
                {
                    Debug.LogWarning("[ContinuumMission] Screenshot verification failed: " + error.Message);
                    screenshots.Remove(item.Key);
                }
            }
        }

        void Fail(Exception error)
        {
            Debug.LogError("[ContinuumMission] " + error);
            Finish(false, error.GetType().Name + ": " + error.Message);
        }

        void TrackController(string name, ComputerModule controller)
        {
            cleanup.Track(name, () =>
            {
                if (controller.Users.Contains(this)) controller.Users.Remove(this);
            });
        }

        void Release(string name)
        {
            Exception error = cleanup.Release(name);
            if (error != null) throw new InvalidOperationException("Unable to release mission resource: " + name, error);
        }

        List<Exception> ReleaseOwned()
        {
            var errors = new List<Exception>(cleanup.ReleaseAll());
            foreach (Exception error in errors) Debug.LogError("[ContinuumMission] Owned resource cleanup failed: " + error);
            return errors;
        }

        void CloseTelemetry(List<Exception> errors)
        {
            StreamWriter writer = telemetry;
            telemetry = null;
            if (writer == null) return;
            try { writer.Dispose(); }
            catch (Exception error) { errors.Add(error); Debug.LogError("[ContinuumMission] Telemetry close failed: " + error); }
        }

        void Finish(bool passed, string reason)
        {
            if (phase == Phase.Done) return;
            phase = Phase.Done;
            List<Exception> errors = ReleaseOwned();
            if (errors.Count != 0) passed = false;
            try
            {
                if (HighLogic.CurrentGame != null && HighLogic.SaveFolder == saveName)
                    SaveMilestone(passed ? "minmus-landed" : "mission-failed");
                WriteTelemetry();
            }
            catch (Exception error)
            {
                errors.Add(error);
                Debug.LogError("[ContinuumMission] Finalization failed: " + error);
            }
            finally
            {
                CloseTelemetry(errors);
                if (errors.Count != 0) passed = false;
                try
                {
                    if (directory != null) File.AppendAllText(Path.Combine(directory, "mission.txt"),
                        "status=" + (passed ? "passed" : "failed") + "\nreason=" + reason.Replace('\n', ' ') + "\n" +
                        string.Concat(errors.Select(error => "finalization=" + error.GetType().Name + ": " + error.Message.Replace('\n', ' ') + "\n")));
                }
                catch (Exception error) { passed = false; Debug.LogError("[ContinuumMission] Outcome receipt failed: " + error); }
                Debug.Log("[ContinuumMission] " + (passed ? "PASSED" : "FAILED") + ": " + reason);
                if (MissionCompatibility.ShouldExit(Application.isBatchMode, Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-exit") >= 0))
                    Application.Quit(passed ? 0 : 1);
                else Debug.Log("[ContinuumMission] Mission stopped; owned cleanup attempted. Rendered scene remains open for inspection.");
            }
        }

        public void OnDestroy()
        {
            List<Exception> errors = ReleaseOwned();
            CloseTelemetry(errors);
        }
    }
}
