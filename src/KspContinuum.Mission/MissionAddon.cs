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
        enum Phase { Dormant, SpaceCenter, Flight, CheckpointFlight, CheckpointReady, Replay, Attach, Armed, Ascent, Transfer, Correction, Coast, Capture, WaitForSite, Landing, Settling, Done }
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
        double phaseWall, startedWall, phaseUT, lastTelemetry = -1, lastSurveyTelemetry = -1, nextAction;
        uint commandId;
        bool active, survey, siteWaitStarted, disableThrottleFloor;
        string attemptId;
        bool bootstrapPending;
        int menuReadyFrame = -1;
        double bootstrapWall;
        CheckpointSource checkpointSource;
        CheckpointRestore checkpointRestore;
        SurveyFootprint footprint;
        double daylightWindowEnd;
        double[] arrivalForecast;
        StreamWriter surveyTelemetry;
        const double OrbitAltitude = 100000;
        const double EncounterPeriapsis = 25000;

        public void Awake()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "--continuum-survey") < 0 && Array.IndexOf(arguments, "--continuum-minmus") < 0 &&
                Array.IndexOf(arguments, "--continuum-survey-disable-throttle-floor") < 0) return;
            bootstrapPending = true;
            bootstrapWall = Time.realtimeSinceStartup;
            DontDestroyOnLoad(gameObject);
            cleanup.Track("menu-ready", () => GameEvents.onLevelWasLoadedGUIReady.Remove(OnMenuReady));
            GameEvents.onLevelWasLoadedGUIReady.Add(OnMenuReady);
            Debug.Log("[ContinuumMission] Awaiting native MAINMENU GUI-ready event before mission initialization.");
        }

        void OnMenuReady(GameScenes scene)
        {
            if (bootstrapPending && scene == GameScenes.MAINMENU) menuReadyFrame = Time.frameCount;
        }

        void InitializeMission()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            survey = Array.IndexOf(arguments, "--continuum-survey") >= 0;
            disableThrottleFloor = Array.IndexOf(arguments, "--continuum-survey-disable-throttle-floor") >= 0;
            if (!survey && !disableThrottleFloor && Array.IndexOf(arguments, "--continuum-minmus") < 0) return;
            active = true;
            DontDestroyOnLoad(gameObject);
            startedWall = Time.realtimeSinceStartup;
            try
            {
                FlightTimeline.RequireOfflineControl();
                if (disableThrottleFloor && !survey) throw new InvalidOperationException("The throttle-floor experiment requires --continuum-survey.");
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("Mission requires KSP 1.12.5.");
                string assemblyVersion = typeof(MechJebCore).Assembly.GetName().Version.ToString();
                var fileVersionAttribute = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(MechJebCore).Assembly, typeof(AssemblyFileVersionAttribute));
                string fileVersion = fileVersionAttribute == null ? null : fileVersionAttribute.Version;
                if (!MissionCompatibility.IsSupported(assemblyVersion, fileVersion))
                    throw new InvalidOperationException("Mission requires MechJeb 2.15.3.0.");
                string checkpointSave = Argument(arguments, "--continuum-checkpoint-save");
                string checkpoint = Argument(arguments, "--continuum-checkpoint");
                string checkpointHash = Argument(arguments, "--continuum-checkpoint-sha256");
                if (checkpointSave != null || checkpoint != null || checkpointHash != null)
                {
                    if (!survey) throw new InvalidOperationException("Checkpoint start requires --continuum-survey.");
                    checkpointSource = CheckpointSource.Inspect(KSPUtil.ApplicationRootPath, checkpointSave, checkpoint, checkpointHash);
                }
                string id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                string outputRoot = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                if (survey)
                {
                    int index = Array.IndexOf(arguments, "--continuum-attempt-id");
                    attemptId = index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
                    if (!SurveyPolicy.ValidAttemptId(attemptId)) throw new InvalidOperationException("Survey requires --continuum-attempt-id CSP-0002-A001 (3 to 6 ordinal digits).");
                    if ((Directory.Exists(outputRoot) && Directory.GetDirectories(outputRoot, "mission-*").Any(path =>
                        File.Exists(Path.Combine(path, "mission.txt")) && File.ReadAllLines(Path.Combine(path, "mission.txt")).Contains("attemptId=" + attemptId))) ||
                        Directory.GetDirectories(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), attemptId + "-*").Length != 0)
                        throw new InvalidOperationException("Attempt identity already exists: " + attemptId);
                    if (Application.isBatchMode) throw new InvalidOperationException("Survey requires a rendered run for 1080p evidence.");
                    Screen.SetResolution(1920, 1080, false);
                }
                saveName = (survey ? attemptId + "-" : "Continuum-Minmus-") + id;
                directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData", "mission-" + id);
                Directory.CreateDirectory(directory);
                telemetry = new StreamWriter(Path.Combine(directory, "mission.csv"));
                telemetry.AutoFlush = true;
                telemetry.WriteLine("wall_s,ut_s,phase,body,situation,altitude_m,apoapsis_m,periapsis_m,surface_speed_mps,throttle,stage,parts,packed,autopilot");
                File.WriteAllText(Path.Combine(directory, "mission.txt"), "status=running\nstartupBoundary=Native MAINMENU GUI-ready event completed before initialization on a later frame\nsave=" + saveName + "\ncraft=Ships/VAB/Kerbal X.craft\nmechjebAssemblyVersion=" + assemblyVersion + "\nmechjebFileVersion=" + fileVersion + "\n");
                if (survey)
                {
                    File.AppendAllText(Path.Combine(directory, "mission.txt"), SurveyReceipt());
                    surveyTelemetry = new StreamWriter(Path.Combine(directory, "survey.csv")) { AutoFlush = true };
                    surveyTelemetry.WriteLine("wall_s,ut_s,phase,phase_wall_s,phase_ut_s,latitude_deg,longitude_deg,distance_m,sun_elevation_deg,eclipsed,radial_tilt_deg,terrain_tilt_deg,angular_speed_rad_s,attitude_error_deg,landing_step,warp_rate,packed,node_autowarp,min_throttle_enabled,min_throttle_fraction,throttle,root_rotation_x,root_rotation_y,root_rotation_z,root_rotation_w,reference_part,terrain_normal_world_x,terrain_normal_world_y,terrain_normal_world_z,terrain_hit_distance_m,screen_width,screen_height,horizontal_speed_mps,vertical_speed_mps,thrust_mode,translation_speed_active,translation_kill_horizontal");
                }
                if (Directory.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName)))
                    throw new InvalidOperationException("Refusing existing save directory.");
                HashMechJebSettings();
                if (checkpointSource != null) { LoadCheckpoint(); return; }
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
            if (bootstrapPending)
            {
                try
                {
                    if (Time.realtimeSinceStartup - bootstrapWall > 30)
                        throw new TimeoutException("Native MAINMENU GUI-ready startup boundary was not observed within 30 seconds.");
                    // HighLogic emits GUI-ready after yielding; leave its entire event dispatch before changing scenes.
                    if (menuReadyFrame < 0 || Time.frameCount <= menuReadyFrame) return;
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU)
                        throw new InvalidOperationException("Scene changed before mission initialization.");
                    bootstrapPending = false;
                    Release("menu-ready");
                    InitializeMission();
                }
                catch (Exception error) { bootstrapPending = false; active = true; Fail(error); }
                return;
            }
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
                if (survey && Time.realtimeSinceStartup - lastSurveyTelemetry >= 0.1) { WriteSurveyTelemetry(); lastSurveyTelemetry = Time.realtimeSinceStartup; }
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
                        if (survey && (Screen.width < 1920 || Screen.height < 1080)) throw new InvalidOperationException("Rendered survey resolution is below 1920x1080.");
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
                    case Phase.CheckpointFlight:
                        if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null ||
                            FlightGlobals.ActiveVessel.packed || FlightGlobals.ActiveVessel.HoldPhysics || TimeWarp.CurrentRate != 1) return;
                        if (Time.realtimeSinceStartup - phaseWall < 15) return;
                        vessel = FlightGlobals.ActiveVessel;
                        core = vessel.GetMasterMechJeb();
                        CheckpointRestore.RequireIdle(vessel, core, Path.Combine(directory, "checkpoint-idle-owners.txt"));
                        minmus = FlightGlobals.Bodies.Single(body => body.bodyName == "Minmus");
                        checkpointRestore.Verify(vessel, Path.Combine(directory, "checkpoint-load-resources.csv"));
                        if (!core.Thrust.LimiterMinThrottle || Math.Abs(core.Thrust.MinThrottle.Val - 0.05) > 1e-12)
                            throw new InvalidOperationException("Checkpoint local throttle-floor baseline was not restored.");
                        commandId = vessel.rootPart.flightID;
                        foreach (Part part in vessel.parts) landerParts.Add(part.flightID);
                        Move(Phase.CheckpointReady); break;
                    case Phase.CheckpointReady:
                        if (FlightGlobals.ActiveVessel != vessel || !vessel.loaded || vessel.packed || vessel.HoldPhysics || TimeWarp.CurrentRate != 1 || FlightDriver.Pause)
                            throw new InvalidOperationException("Checkpoint idle interval lost its normal, focused physics boundary.");
                        CheckpointRestore.RequireIdle(vessel, core, Path.Combine(directory, "checkpoint-idle-owners.txt"));
                        double checkpointNow = Planetarium.GetUniversalTime();
                        if (checkpointNow > checkpointRestore.Epoch + 1) throw new InvalidOperationException("Checkpoint fixed acquisition epoch missed.");
                        if (checkpointNow < checkpointRestore.Epoch) return;
                        if (Screen.width < 1920 || Screen.height < 1080) throw new InvalidOperationException("Rendered survey resolution is below 1920x1080.");
                        checkpointRestore.Verify(vessel, Path.Combine(directory, "checkpoint-acquisition-resources.csv"));
                        CheckpointRestore.RequireIdle(vessel, core, Path.Combine(directory, "checkpoint-acquisition-owners.txt"));
                        checkpointSource.VerifyUnchanged();
                        timeline = new FlightTimeline();
                        cleanup.Track("timeline", () => timeline.Dispose());
                        timeline.BeginRecording(vessel);
                        File.AppendAllText(Path.Combine(directory, "mission.txt"), "inputDirectory=" + timeline.OutputDirectory +
                            "\ncheckpointAcquisitionObservedUT=" + F(checkpointNow) + "\nrestoredMinimumThrottleEnabled=" + core.Thrust.LimiterMinThrottle +
                            "\nrestoredMinimumThrottleFraction=" + F(core.Thrust.MinThrottle.Val) + "\n");
                        OwnFlightCleanup();
                        core.Staging.AutoStageLimitRequest(2, this);
                        var checkpointStaging = core.Staging;
                        cleanup.Track("staging-limit", () => checkpointStaging.AutoStageLimitRemove(this));
                        core.Node.Autowarp = true;
                        PrepareSite(checkpointRestore.Epoch);
                        SaveMilestone("checkpoint-restored");
                        Move(Phase.WaitForSite); break;
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
                        OwnFlightCleanup();
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
                        if (!NodesFinished() || vessel.HoldPhysics || vessel.ctrlState.mainThrottle != 0) return;
                        if (vessel.mainBody != minmus || vessel.orbit.eccentricity >= 1 || vessel.orbit.PeA < 5000)
                            throw new InvalidOperationException("Capture burn did not leave safe Minmus orbit.");
                        SaveMilestone("minmus-orbit");
                        core.Warp.MinimumWarp(true);
                        if (survey) { PrepareSite(); Move(Phase.WaitForSite); }
                        else StartLanding();
                        break;
                    case Phase.WaitForSite:
                        WaitForSite(); break;
                    case Phase.Landing:
                        if (survey && Planetarium.GetUniversalTime() > daylightWindowEnd) throw new TimeoutException("Landing exceeded the sampled daylight interval.");
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
                        if (survey) valid = valid && SurveyObservation.Read(vessel).Qualifies && Planetarium.GetUniversalTime() <= daylightWindowEnd && Screen.width >= 1920 && Screen.height >= 1080;
                        if (acceptance.Observe(Planetarium.GetUniversalTime(), valid)) Finish(true, survey ?
                            "Survey target reached in daylight, upright and stable for 30 simulation seconds; command, engine and gear survive." :
                            "Landed on Minmus; command, engine and gear survive; settled 30 simulation seconds.");
                        break;
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        static string Argument(string[] arguments, string name)
        {
            int index = Array.IndexOf(arguments, name);
            if (index < 0) return null;
            if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--") || Array.LastIndexOf(arguments, name) != index)
                throw new InvalidOperationException("Missing or duplicate argument: " + name);
            return arguments[index + 1];
        }

        void HashMechJebSettings()
        {
            string settings = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2");
            using (var hashes = new StreamWriter(Path.Combine(directory, "mechjeb-settings.csv")))
            {
                hashes.WriteLine("filename,sha256");
                if (!Directory.Exists(settings))
                {
                    hashes.WriteLine("missing-directory,");
                    if (checkpointSource != null) throw new InvalidOperationException("Checkpoint requires existing MechJeb settings inputs.");
                    return;
                }
                foreach (string path in Directory.GetFiles(settings, "mechjeb_settings_*.cfg").OrderBy(path => path, StringComparer.Ordinal))
                    hashes.WriteLine("\"" + Path.GetFileName(path).Replace("\"", "\"\"") + "\"," + CheckpointSource.FileDigest(path));
            }
        }

        void LoadCheckpoint()
        {
            cleanup.Track("checkpoint-source", () =>
            {
                checkpointSource.VerifyUnchanged();
                File.AppendAllText(Path.Combine(directory, "mission.txt"), "parentCheckpointVerifiedAfterRun=True\n");
            });
            checkpointSource.CopyTo(Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName));
            string copy = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName, "checkpoint-source.sfs");
            File.AppendAllText(Path.Combine(directory, "mission.txt"), "parentAttemptId=" + checkpointSource.ParentAttemptId +
                "\nparentCheckpoint=" + checkpointSource.Checkpoint + "\nparentCheckpointSha256=" + checkpointSource.Sha256 +
                "\ncheckpointCopySha256=" + CheckpointSource.FileDigest(copy) + "\ncheckpointSourceUT=" + F(checkpointSource.UniversalTime) +
                "\ncheckpointAcquisitionEpochUT=" + F(checkpointSource.UniversalTime + 60) +
                "\ncheckpointTelemetryClock=Samples begin only after native flight readiness at or after source UT; loading event uses source epoch\ncheckpointMode=native reconstructed orbit and resources; controller reinitialized, no saved angular-velocity or complete control-state replay\n" +
                "neutralReplay=NOT RUN: installed MechJeb owns checkpoint controls.\n");
            Game game = GamePersistence.LoadGame("checkpoint-source", saveName, true, true);
            checkpointRestore = new CheckpointRestore(game, checkpointSource.UniversalTime);
            HighLogic.SaveFolder = saveName;
            HighLogic.CurrentGame = game;
            game.Title = saveName + " (SANDBOX)";
            game.startScene = GameScenes.FLIGHT;
            Move(Phase.CheckpointFlight, checkpointSource.UniversalTime);
            game.Start();
        }

        void OwnFlightCleanup()
        {
            Vessel controlledVessel = vessel;
            cleanup.Track("throttle", () =>
            {
                if (controlledVessel != null) controlledVessel.ctrlState.mainThrottle = 0;
                if (FlightGlobals.ActiveVessel == controlledVessel) FlightInputHandler.state.mainThrottle = 0;
            });
            var warp = core.Warp;
            cleanup.Track("warp", () => { if (FlightGlobals.ActiveVessel == controlledVessel) warp.MinimumWarp(true); });
        }

        void StartLanding()
        {
            if (vessel.packed || vessel.HoldPhysics || TimeWarp.CurrentRate != 1 || vessel.ctrlState.mainThrottle != 0 ||
                core.Node.Enabled || core.Ascent.Enabled || core.Landing.Enabled || vessel.patchedConicSolver.maneuverNodes.Count != 0)
                throw new InvalidOperationException("Landing acquisition requires normal unpacked physics, idle controllers and no maneuver nodes.");
            if (disableThrottleFloor)
            {
                var thrust = core.Thrust;
                bool previousFloor = thrust.LimiterMinThrottle;
                // Register first so reverse cleanup releases the landing controller before restoring its setting.
                cleanup.Track("landing-throttle-floor", () =>
                {
                    if (!thrust.LimiterMinThrottle) thrust.LimiterMinThrottle = previousFloor;
                });
                thrust.LimiterMinThrottle = false;
                File.AppendAllText(Path.Combine(directory, "mission.txt"), "landingThrottleFloorPrevious=" + previousFloor +
                    "\nlandingThrottleFloorApplied=False\nlandingThrottleFloorFraction=" + F(thrust.MinThrottle) + "\n");
            }
            core.Landing.TouchdownSpeed.Val = 0.5;
            core.Landing.DeployGears = true;
            core.Landing.DeployChutes = false;
            core.Landing.RCSAdjustment = false;
            if (survey) core.Target.SetPositionTarget(minmus, SurveyPolicy.Latitude, SurveyPolicy.Longitude);
            TrackController("landing", core.Landing);
            if (survey) core.Landing.LandAtPositionTarget(this); else core.Landing.LandUntargeted(this);
            Move(Phase.Landing);
            SaveMilestone("descent-start");
        }

        void PrepareSite(double? planningEpoch = null)
        {
            if (minmus.pqsController == null) throw new InvalidOperationException("Minmus PQS terrain unavailable.");
            if (Math.Abs(minmus.pqsController.radius - minmus.Radius) > 0.01)
                throw new InvalidOperationException("Survey requires matching native PQS and body reference radii.");
            footprint = SurveyGeometry.Sample(minmus.Radius, (lat, lon) => minmus.TerrainAltitude(lat, lon, true));
            using (var samples = new StreamWriter(Path.Combine(directory, "terrain.csv")))
            {
                samples.WriteLine("latitude_deg,longitude_deg,height_m");
                foreach (SurveySample sample in footprint.Samples) samples.WriteLine(F(sample.Latitude) + "," + F(sample.Longitude) + "," + F(sample.Height));
            }
            File.AppendAllText(Path.Combine(directory, "mission.txt"), "sampledMaximumSlopeDeg=" + F(footprint.MaximumSlope) + "\nsampledMinimumHeightM=" + F(footprint.MinimumHeight) + "\nsampledMaximumHeightM=" + F(footprint.MaximumHeight) + "\n");
            if (!SurveyPolicy.Finite(footprint.MaximumSlope) || footprint.MaximumSlope > SurveyPolicy.MaximumSlope)
                throw new InvalidOperationException("Site rejected: sampled footprint exceeds slope limit.");
            double duration = 2 * vessel.orbit.period + 1800;
            var model = new SurveySunModel(minmus);
            nextAction = SurveyPolicy.FindWindow(planningEpoch ?? Planetarium.GetUniversalTime(), Math.Abs(minmus.rotationPeriod), duration, ut =>
            {
                double elevation; bool eclipsed;
                model.Evaluate(ut, SurveyPolicy.Latitude, SurveyPolicy.Longitude, footprint.CenterHeight, out elevation, out eclipsed);
                return elevation >= SurveyPolicy.MinimumSun && !eclipsed;
            });
            if (!SurveyPolicy.Finite(nextAction)) throw new InvalidOperationException("Site rejected: no sampled daylight window within one Minmus rotation.");
            daylightWindowEnd = nextAction + duration;
            // Freeze scalar predictions before warp; a cached world basis cannot be mixed with later floating frames.
            arrivalForecast = new double[(int)SurveyPolicy.SunSampleSeconds + 1];
            for (int second = 0; second < arrivalForecast.Length; second++)
            {
                bool predictedEclipse;
                model.Evaluate(nextAction + second, SurveyPolicy.Latitude, SurveyPolicy.Longitude, footprint.CenterHeight, out arrivalForecast[second], out predictedEclipse);
            }
            File.AppendAllText(Path.Combine(directory, "mission.txt"), "sampledDaylightStartUT=" + F(nextAction) + "\nsampledDaylightEndUT=" + F(daylightWindowEnd) + "\n");
        }

        void WaitForSite()
        {
            if (vessel.mainBody != minmus || vessel.orbit.eccentricity >= 1 || vessel.orbit.PeA < 5000 ||
                !SurveyPolicy.Finite(vessel.orbit.PeA) || !SurveyPolicy.Finite(vessel.orbit.period) ||
                core.Node.Enabled || core.Ascent.Enabled || core.Landing.Enabled || vessel.ctrlState.mainThrottle != 0 ||
                vessel.patchedConicSolver.maneuverNodes.Count != 0)
                throw new InvalidOperationException("Daylight wait lost safe orbit or idle controller boundary.");
            double now = Planetarium.GetUniversalTime();
            if (now < nextAction)
            {
                if (!siteWaitStarted) { core.Warp.WarpToUT(nextAction); siteWaitStarted = true; }
                return;
            }
            core.Warp.MinimumWarp(true);
            if (vessel.packed || vessel.HoldPhysics || TimeWarp.CurrentRate != 1) return;
            double sun; bool eclipsed;
            new SurveySunModel(minmus).Evaluate(now, SurveyPolicy.Latitude, SurveyPolicy.Longitude, footprint.CenterHeight, out sun, out eclipsed);
            double nativeSun = SurveyGeometry.Elevation(SurveySunModel.Vector(minmus.GetSurfaceNVector(SurveyPolicy.Latitude, SurveyPolicy.Longitude)),
                SurveySunModel.Vector(Planetarium.fetch.Sun.position - minmus.GetWorldSurfacePosition(SurveyPolicy.Latitude, SurveyPolicy.Longitude, footprint.CenterHeight)));
            double predictedArrivalSun = SurveyPolicy.ArrivalForecast(arrivalForecast, now - nextAction);
            double residual = Math.Abs(nativeSun - predictedArrivalSun);
            double currentModelResidual = Math.Abs(nativeSun - sun);
            File.AppendAllText(Path.Combine(directory, "mission.txt"), "arrivalPredictedSunElevationDeg=" + F(predictedArrivalSun) +
                "\narrivalObservedSunElevationDeg=" + F(nativeSun) + "\narrivalCurrentModelResidualDeg=" + F(currentModelResidual) + "\narrivalSunResidualDeg=" + F(residual) + "\narrivalDelayS=" + F(now - nextAction) + "\n");
            if (!SurveyPolicy.Finite(residual) || residual > 0.1 || !SurveyPolicy.Finite(currentModelResidual) || currentModelResidual > 0.01) throw new InvalidOperationException("Frozen daylight forecast disagrees with current native geometry by more than 0.1 degree.");
            if (now > nextAction + SurveyPolicy.SunSampleSeconds || sun < SurveyPolicy.MinimumSun || eclipsed)
                throw new InvalidOperationException("Daylight window missed or failed current-epoch recheck.");
            StartLanding();
        }

        string SurveyReceipt()
        {
            return "program=Continuum Space Program\nmissionId=CSP-0002\nmissionName=Minmus Survey 1\nattemptId=" + attemptId +
                "\nvehicleDesignId=CV-0001-R01\nvehicleName=Kerbal X / stock\nsiteId=SITE-MIN-001\ntargetLatitudeDeg=" + F(SurveyPolicy.Latitude) +
                "\ntargetLongitudeDeg=" + F(SurveyPolicy.Longitude) + "\nmaximumDistanceM=100\nminimumSunElevationDeg=20\nmaximumSampledSlopeDeg=2\nmaximumTerrainTiltDeg=10\nmaximumAngularSpeedRadS=0.01\nsettledDurationS=30\nfootprint=23x23 samples at 10m spacing; sampled radial PQS triangles, not collider clearance or terrain horizon\n" +
                "daylight=60s sampled central-ray spherical eclipse model; interval allowance 2 captured orbit periods + 1800s, not guaranteed touchdown\n" +
                "frames=current-epoch body normal basis plus signed rotationPeriod; recursive getTruePositionAtUT ephemerides\n" +
                "arrivalWitness=pre-warp 1s scalar forecast samples interpolated at actual arrival; maximum residual 0.1deg; current model/native residual 0.01deg\n" +
                "coastAcceleration=MechJeb targeted landing guarded autowarp; no manual descent warp\nrequestedResolution=1920x1080\n" +
                "experimentVariant=" + (disableThrottleFloor ? "landing-minimum-throttle-floor-disabled" : "donor-default-throttle-floor") +
                "\nlandingThrottleFloorPolicy=" + (disableThrottleFloor ? "disable at owned landing acquisition; restore prior value after releasing landing, unless changed externally" : "preserve donor setting") + "\n";
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
            if (phase == Phase.SpaceCenter || phase == Phase.Flight || phase == Phase.CheckpointFlight || phase == Phase.CheckpointReady) return 300;
            if (phase == Phase.Replay || phase == Phase.Attach || phase == Phase.Armed || phase == Phase.Settling) return 180;
            return 3600;
        }

        void Move(Phase next, double? epoch = null)
        {
            phase = next; phaseWall = Time.realtimeSinceStartup; phaseUT = epoch ?? Planetarium.GetUniversalTime();
            Debug.Log("[ContinuumMission] " + next);
            if (directory != null) File.AppendAllText(Path.Combine(directory, "events.txt"), F(phaseUT) + " " + next + "\n");
        }

        bool HasSimulationClock()
        {
            double now = Planetarium.GetUniversalTime();
            return checkpointSource == null || (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null &&
                SurveyPolicy.Finite(now) && now >= checkpointSource.UniversalTime);
        }

        void WriteTelemetry()
        {
            if (telemetry == null) return;
            if (!HasSimulationClock()) return;
            Vessel current = vessel != null ? vessel : FlightGlobals.ActiveVessel;
            string status = core == null ? "" : phase == Phase.Ascent ? core.Ascent.Status : phase == Phase.Landing ? core.Landing.Status : core.Node.State.ToString();
            telemetry.WriteLine(string.Join(",", F(Time.realtimeSinceStartup - startedWall), F(Planetarium.GetUniversalTime()), phase.ToString(),
                current == null ? "" : current.mainBody.bodyName, current == null ? "" : current.situation.ToString(),
                current == null ? "" : F(current.altitude), current == null ? "" : F(current.orbit.ApA), current == null ? "" : F(current.orbit.PeA),
                current == null ? "" : F(current.srfSpeed), current == null ? "" : F(current.ctrlState.mainThrottle),
                current == null ? "" : current.currentStage.ToString(CultureInfo.InvariantCulture), current == null ? "" : current.parts.Count.ToString(CultureInfo.InvariantCulture),
                current == null ? "" : current.packed.ToString(), "\"" + status.Replace("\"", "\"\"") + "\""));
        }

        void WriteSurveyTelemetry()
        {
            Vessel current = vessel;
            if (surveyTelemetry == null || current == null || core == null || !HasSimulationClock()) return;
            SurveyObservation observation = current.mainBody == minmus ? SurveyObservation.Read(current) : null;
            Quaternion rotation = current.rootPart.transform.rotation;
            Vector3 terrain = current.vesselTransform.TransformDirection(current.terrainNormal);
            double now = Planetarium.GetUniversalTime();
            surveyTelemetry.WriteLine(string.Join(",", F(Time.realtimeSinceStartup - startedWall), F(now), phase.ToString(),
                F(Time.realtimeSinceStartup - phaseWall), F(now - phaseUT), F(current.latitude), F(current.longitude),
                observation == null ? "" : F(observation.Distance), observation == null ? "" : F(observation.SunElevation),
                observation == null ? "" : observation.Eclipsed.ToString(), observation == null ? "" : F(observation.RadialTilt),
                observation == null ? "" : F(observation.TerrainTilt), F(current.angularVelocity.magnitude), F(core.Attitude.attitudeAngleFromTarget()),
                core.Landing.CurrentStep == null ? "" : core.Landing.CurrentStep.GetType().Name, F(TimeWarp.CurrentRate), current.packed.ToString(),
                core.Node.Autowarp.ToString(), core.Thrust.LimiterMinThrottle.ToString(), F(core.Thrust.MinThrottle), F(current.ctrlState.mainThrottle),
                F(rotation.x), F(rotation.y), F(rotation.z), F(rotation.w), current.GetReferenceTransformPart() == null ? "" : current.GetReferenceTransformPart().flightID.ToString(CultureInfo.InvariantCulture),
                F(terrain.x), F(terrain.y), F(terrain.z), F(current.heightFromTerrain), Screen.width.ToString(CultureInfo.InvariantCulture), Screen.height.ToString(CultureInfo.InvariantCulture),
                F(core.VesselState.speedSurfaceHorizontal), F(core.VesselState.speedVertical), core.Thrust.Tmode.ToString(),
                F(core.Thrust.TransSpdAct), core.Thrust.TransKillH.ToString()));
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
                    int width = 0, height = 0;
                    if (File.Exists(path))
                    {
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (file.Length >= 33)
                            {
                                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                                complete = signature.All(value => file.ReadByte() == value);
                                file.Seek(16, SeekOrigin.Begin);
                                width = ReadPngInt(file); height = ReadPngInt(file);
                                file.Seek(-12, SeekOrigin.End);
                                byte[] ending = { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
                                complete = complete && ending.All(value => file.ReadByte() == value);
                            }
                        }
                    }
                    if (complete || Time.realtimeSinceStartup - item.Value > 30)
                    {
                        File.AppendAllText(Path.Combine(directory, "screenshots.csv"), F(Planetarium.GetUniversalTime()) + "," +
                            (complete ? (survey && (width < 1920 || height < 1080) ? "png-below-required-resolution" : "png-written") : "unconfirmed") + "," + item.Key + "," + width + "," + height + "\n");
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

        static int ReadPngInt(Stream stream)
        {
            return (stream.ReadByte() << 24) | (stream.ReadByte() << 16) | (stream.ReadByte() << 8) | stream.ReadByte();
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
            StreamWriter surveyWriter = surveyTelemetry;
            surveyTelemetry = null;
            if (surveyWriter != null) try { surveyWriter.Dispose(); } catch (Exception error) { errors.Add(error); }
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
                WriteSurveyTelemetry();
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
