using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class IndependentCoastAdapter : MonoBehaviour
    {
        const string Flag = "--continuum-coast-canary";
        const string QuitFlag = "--continuum-coast-quit-after-qualification";
        const string CadencePrefix = "--continuum-coast-publication-seconds=";
        const string DirectCadenceFlag = "--continuum-coast-direct-presentation";
        const string EventRadiusPrefix = "--continuum-coast-event-radius-meters=";
        const string EventDirectionPrefix = "--continuum-coast-event-direction=";
        const string EventHorizonPrefix = "--continuum-coast-event-horizon-seconds=";
        const string EventGuardPrefix = "--continuum-coast-event-guard-seconds=";
        const string Owner = "continuum.independent-coast-adapter";
        const int MaximumCalls = 256;
        const double ForecastSeconds = 600;
        const double DefaultEventHorizonSeconds = 7200;
        const double DefaultEventGuardSeconds = 5;
        const double EventTimeToleranceSeconds = 1e-6;
        const double PositionToleranceMeters = 1;
        const double VelocityToleranceMetersPerSecond = .01;
        static IndependentCoastAdapter instance;
        [ThreadStatic] static SuppressionToken token;
        static readonly AccessTools.FieldRef<OrbitDriver, bool> DriverReady =
            AccessTools.FieldRefAccess<OrbitDriver, bool>("ready");

        sealed class SuppressionToken
        {
            public Orbit Orbit;
            public long UniversalTimeBits;
            public DriverCall Call;
        }

        sealed class DriverCall
        {
            public bool Candidate, Consumed;
            public long Started, SampleTicks, SeedTicks;
            public double UniversalTime;
            public CoastingBody Body;
        }

        sealed class ForecastResult
        {
            public CoastingAdvanceResult Advance;
            public RadiusCrossingEvent Event;
        }

        Harmony harmony;
        MethodInfo driverTarget, orbitTarget;
        CoastingAdapterReport report;
        CoastingEngine engine;
        CoastPresentationCadence cadence;
        Func<double, CoastingBody> sampleBody;
        Task<ForecastResult> forecast;
        Orbit stockReference;
        Vessel vessel;
        Vessel warpCandidate;
        CelestialBody referenceBody;
        CelestialBody warpCandidateBody;
        int stableUnpackedFrames;
        bool warpRequested;
        bool eventConfigured;
        double eventRadius, eventHorizon, eventGuard;
        RadiusCrossingDirection eventDirection;
        RadiusCrossingEvent predictedEvent;
        bool requested, active, failed, stopRequested, finished, exported, quitAfter;
        long callbackTicks, maximumCallbackTicks, sampleTicks, maximumSampleTicks,
            seedTicks, maximumSeedTicks, residualTicks, maximumResidualTicks;

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, Flag) < 0) return;
            requested = true; quitAfter = Array.IndexOf(arguments, QuitFlag) >= 0;
            report = new CoastingAdapterReport { status = "waiting-for-packed-orbit" };
            try
            {
                cadence = Array.IndexOf(arguments, DirectCadenceFlag) >= 0 ?
                    CoastPresentationCadence.Direct() : new CoastPresentationCadence(ParseCadence(arguments));
                ParseEventConfiguration(arguments);
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("KSP 1.12.5 is required.");
                driverTarget = AccessTools.DeclaredMethod(typeof(OrbitDriver), "UpdateOrbit", new[] { typeof(bool) });
                orbitTarget = AccessTools.DeclaredMethod(typeof(Orbit), "UpdateFromUT", new[] { typeof(double) });
                if (driverTarget == null || orbitTarget == null) throw new MissingMethodException("Pinned orbital update seam is unavailable.");
                RequireUncontested(driverTarget); RequireUncontested(orbitTarget);
                harmony = new Harmony(Owner); instance = this;
                harmony.Patch(driverTarget,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IndependentCoastAdapter), "DriverPrefix"), Priority.First),
                    postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IndependentCoastAdapter), "DriverPostfix"), Priority.Last),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IndependentCoastAdapter), "DriverFinalizer"), Priority.Last));
                harmony.Patch(orbitTarget,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IndependentCoastAdapter), "OrbitPrefix"), Priority.First));
                report.installationStatus = "installed";
            }
            catch (Exception error) { Fail("installation-failed:" + error.GetType().Name); Finish(); }
        }

        public void Update()
        {
            if (!requested || finished) return;
            try
            {
                if (failed) { StopWarpAfterFailure(); Finish(); return; }
                if (stopRequested) { ReleaseToStock(); Finish(); return; }
                if (!active && engine == null) TrySeed();
                if (!active && forecast != null && forecast.IsCompleted) AdmitForecast();
                if (active && predictedEvent != null && CoastingEventScheduler.GuardReached(
                    Planetarium.GetUniversalTime(), predictedEvent.TimeSeconds, eventGuard))
                { StopForEvent(); return; }
                if (active && (!Eligible(vessel) || vessel.mainBody != referenceBody || vessel.orbit.referenceBody != referenceBody))
                    Stop("admission-ended");
                if (active && !TargetsOwned()) Stop("patch-graph-changed");
            }
            catch (Exception error) { Fail("update-failed:" + error.GetType().Name); Finish(); }
        }

        void TrySeed()
        {
            Vessel current = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (current == null || current.orbit == null || current.mainBody == null ||
                current.situation != Vessel.Situations.ORBITING)
            {
                ResetWarpCandidate();
                return;
            }
            if (!warpRequested)
            {
                if (!ReadyForWarp(current)) { ResetWarpCandidate(); return; }
                if (current != warpCandidate || current.mainBody != warpCandidateBody)
                { warpCandidate = current; warpCandidateBody = current.mainBody; stableUnpackedFrames = 0; }
                stableUnpackedFrames++;
                if (stableUnpackedFrames >= 3)
                { TimeWarp.SetRate(1, true); warpRequested = true; }
                return;
            }
            if (current != warpCandidate || current.mainBody != warpCandidateBody)
            { ResetWarpCandidate(); return; }
            if (TimeWarp.CurrentRateIndex <= 0 || TimeWarp.WarpMode != TimeWarp.Modes.HIGH) return;
            if (!Eligible(current)) return;
            double ut = Planetarium.GetUniversalTime();
            double atmosphere = current.mainBody.atmosphere ? current.mainBody.atmosphereDepth : 0;
            bool hasNextPatch = current.orbit.nextPatch != null;
            double requiredHorizon = eventConfigured ? eventHorizon : ForecastSeconds;
            bool nextPatchAfterForecast = Finite(current.orbit.EndUT) && current.orbit.EndUT > ut + requiredHorizon;
            report.admittedEccentricity = current.orbit.eccentricity;
            report.admittedPeriapsisAltitude = current.orbit.PeA;
            report.admittedApoapsisRadius = current.orbit.ApR;
            report.admittedSphereOfInfluence = Number(current.mainBody.sphereOfInfluence);
            report.admittedPatchEndUniversalTime = Number(current.orbit.EndUT);
            report.admittedPatchEndTransition = current.orbit.patchEndTransition.ToString();
            report.admittedHasNextPatch = hasNextPatch;
            double sphereOfInfluence = current.mainBody.sphereOfInfluence;
            bool validSphereOfInfluence = (Finite(sphereOfInfluence) && sphereOfInfluence > 0) ||
                double.IsPositiveInfinity(sphereOfInfluence);
            if (!Finite(current.orbit.eccentricity) || current.orbit.eccentricity < 0 || current.orbit.eccentricity >= 1 ||
                !Finite(current.orbit.PeR) || current.orbit.PeR <= current.mainBody.Radius + atmosphere + 1000 ||
                !Finite(current.orbit.ApR) || !validSphereOfInfluence || current.orbit.ApR >= sphereOfInfluence ||
                (hasNextPatch && !nextPatchAfterForecast))
            { Fail("orbit-outside-qualified-coast-domain"); return; }
            Vector3d position = current.orbit.getRelativePositionAtUT(ut);
            Vector3d velocity = current.orbit.getOrbitalVelocityAtUT(ut);
            vessel = current; referenceBody = current.mainBody;
            engine = new CoastingEngine(ut, referenceBody.gravParameter,
                new[] { new CoastingBody(0, V(position), V(velocity)) }, 1);
            sampleBody = SampleBody;
            stockReference = new Orbit();
            stockReference.UpdateFromStateVectors(position, velocity, referenceBody, ut);
            report.seedUniversalTime = ut; report.vesselId = vessel.id.ToString("D");
            report.referenceBody = referenceBody.bodyName; report.status = "forecasting";
            report.admittedWarpRateIndex = TimeWarp.CurrentRateIndex;
            if (eventConfigured)
            {
                report.eventConfigured = true; report.eventRadiusMeters = eventRadius;
                report.eventDirection = eventDirection.ToString().ToLowerInvariant();
                report.eventSearchHorizonSeconds = eventHorizon; report.eventGuardSeconds = eventGuard;
                forecast = Task.Run(() =>
                {
                    RadiusCrossingEvent found = CoastingEventScheduler.FindFirst(engine,
                        new RadiusCrossingSearch(ut, ut + eventHorizon, eventRadius, eventDirection,
                            EventTimeToleranceSeconds));
                    return found == null ? new ForecastResult() : new ForecastResult
                    { Event = found, Advance = engine.AdvanceTo(found.TimeSeconds) };
                });
            }
            else forecast = Task.Run(() => new ForecastResult
            { Advance = engine.AdvanceTo(ut + ForecastSeconds, 3600) });
        }

        static bool ReadyForWarp(Vessel candidate)
        {
            OrbitDriver driver = candidate == null ? null : candidate.orbitDriver;
            return candidate != null && candidate == FlightGlobals.ActiveVessel && candidate.loaded && !candidate.packed &&
                !FlightDriver.Pause && TimeWarp.CurrentRateIndex == 0 && driver != null && DriverReady(driver) &&
                driver.updateMode == OrbitDriver.UpdateMode.TRACK_Phys;
        }

        void ResetWarpCandidate()
        {
            warpCandidate = null; warpCandidateBody = null; stableUnpackedFrames = 0; warpRequested = false;
        }

        void AdmitForecast()
        {
            if (forecast.IsFaulted || forecast.IsCanceled)
            { Fail("forecast-failed"); forecast = null; return; }
            ForecastResult result = forecast.Result;
            if (eventConfigured && result.Event == null)
            { report.eventFound = false; Fail("no-radius-event-within-horizon"); forecast = null; StopWarpAfterFailure(); return; }
            predictedEvent = result.Event;
            if (predictedEvent != null)
            {
                report.eventFound = true; report.eventKind = predictedEvent.Kind;
                report.eventUniversalTime = predictedEvent.TimeSeconds;
                report.eventEvaluations = predictedEvent.Evaluations;
                report.eventTimeToleranceSeconds = EventTimeToleranceSeconds;
                report.eventRadiusErrorMeters = Math.Abs(Magnitude(predictedEvent.Body.Position) - eventRadius);
                report.engineFrontierAtEvent = result.Advance.Final.TimeSeconds == predictedEvent.TimeSeconds;
                if (!report.engineFrontierAtEvent)
                { Fail("engine-frontier-missed-event"); forecast = null; StopWarpAfterFailure(); return; }
            }
            report.forecastUniversalTime = result.Advance.Final.TimeSeconds;
            double now = Planetarium.GetUniversalTime();
            report.forecastCompletedBeforePresentation = now < result.Advance.Final.TimeSeconds;
            if (!report.forecastCompletedBeforePresentation)
            { Fail("forecast-did-not-lead-presentation"); forecast = null; StopWarpAfterFailure(); return; }
            if (predictedEvent != null && CoastingEventScheduler.GuardReached(now, predictedEvent.TimeSeconds, eventGuard))
            { Fail("event-guard-window-missed"); forecast = null; StopWarpAfterFailure(); return; }
            report.authorityAdmissionAttested = TargetsOwned();
            if (!report.authorityAdmissionAttested) { Fail("authority-admission-failed"); forecast = null; return; }
            active = true; report.status = "active";
        }

        void StopForEvent()
        {
            double ut = Planetarium.GetUniversalTime();
            report.warpStopRequested = true; report.warpStopRequestUniversalTime = ut;
            report.warpRateIndexBeforeStopRequest = TimeWarp.CurrentRateIndex;
            Stop("event-guard-reached");
            ReleaseToStock();
            try
            {
                TimeWarp.SetRate(0, true); report.warpRateIndexAfterStopRequest = TimeWarp.CurrentRateIndex;
                if (report.warpRateIndexAfterStopRequest != 0) Fail("warp-stop-not-observed");
            }
            catch (Exception error) { Fail("warp-stop-failed:" + error.GetType().Name); }
            Finish();
        }

        void StopWarpAfterFailure()
        {
            if (!eventConfigured || report.warpStopRequested) return;
            report.warpStopRequested = true; report.warpStopRequestUniversalTime = Planetarium.GetUniversalTime();
            report.warpRateIndexBeforeStopRequest = TimeWarp.CurrentRateIndex;
            try { TimeWarp.SetRate(0, true); report.warpRateIndexAfterStopRequest = TimeWarp.CurrentRateIndex; }
            catch (Exception error) { report.errors++; report.reason += ":warp-stop-failed:" + error.GetType().Name; }
        }

        static bool DriverPrefix(OrbitDriver __instance, bool offset, out DriverCall __state)
        {
            __state = new DriverCall();
            IndependentCoastAdapter owner = instance;
            if (owner == null || !owner.active || __instance != owner.vessel.orbitDriver) return true;
            try
            {
                if (!owner.Eligible(owner.vessel) || owner.vessel.mainBody != owner.referenceBody ||
                    __instance.orbit.referenceBody != owner.referenceBody)
                { owner.report.stockFallbacks++; owner.Stop("driver-state-changed"); return true; }
                double ut = Planetarium.GetUniversalTime();
                __state.Candidate = true; __state.Started = Stopwatch.GetTimestamp(); __state.UniversalTime = ut;
                long sampleStarted = Stopwatch.GetTimestamp();
                CoastingBody body = owner.cadence.Evaluate(ut, owner.sampleBody);
                __state.SampleTicks = Stopwatch.GetTimestamp() - sampleStarted;
                long seedStarted = Stopwatch.GetTimestamp();
                __instance.orbit.UpdateFromStateVectors(V(body.Position), V(body.Velocity), owner.referenceBody, ut);
                __state.SeedTicks = Stopwatch.GetTimestamp() - seedStarted;
                __state.Body = body;
                token = new SuppressionToken { Orbit = __instance.orbit,
                    UniversalTimeBits = BitConverter.DoubleToInt64Bits(ut), Call = __state };
                return true;
            }
            catch (Exception error)
            {
                __state.Candidate = false; token = null; owner.report.errors++; owner.report.stockFallbacks++;
                owner.Stop("candidate-failed:" + error.GetType().Name); return true;
            }
        }

        static bool OrbitPrefix(Orbit __instance, double UT)
        {
            SuppressionToken current = token;
            if (current == null || current.Orbit != __instance ||
                current.UniversalTimeBits != BitConverter.DoubleToInt64Bits(UT)) return true;
            token = null; current.Call.Consumed = true; return false;
        }

        static void DriverPostfix(OrbitDriver __instance, DriverCall __state)
        {
            IndependentCoastAdapter owner = instance;
            if (owner == null || __state == null || !__state.Candidate) return;
            if (!__state.Consumed) { owner.Fail("one-shot-not-consumed"); return; }
            try
            {
                if (__instance != owner.vessel.orbitDriver || __instance.orbit.referenceBody != owner.referenceBody)
                { owner.Fail("driver-readback-changed"); return; }
                Vector3d position = owner.stockReference.getRelativePositionAtUT(__state.UniversalTime);
                Vector3d velocity = owner.stockReference.getOrbitalVelocityAtUT(__state.UniversalTime);
                CoastingBody expected = __state.Body;
                Vec expectedPosition = expected.Position, expectedVelocity = expected.Velocity;
                owner.report.maximumPositionErrorMeters = Math.Max(owner.report.maximumPositionErrorMeters,
                    Magnitude(V(position) + expectedPosition * -1));
                owner.report.maximumVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumVelocityErrorMetersPerSecond,
                    Magnitude(V(velocity) + expectedVelocity * -1));
                Vector3d injectedPosition = __instance.orbit.getRelativePositionAtUT(__state.UniversalTime);
                Vector3d injectedVelocity = __instance.orbit.getOrbitalVelocityAtUT(__state.UniversalTime);
                owner.report.maximumInjectedPositionErrorMeters = Math.Max(owner.report.maximumInjectedPositionErrorMeters,
                    Magnitude(V(injectedPosition) + expectedPosition * -1));
                owner.report.maximumInjectedVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumInjectedVelocityErrorMetersPerSecond,
                    Magnitude(V(injectedVelocity) + expectedVelocity * -1));
                Vector3d expectedDriverPosition = __instance.orbit.pos; expectedDriverPosition.Swizzle();
                Vector3d expectedDriverVelocity = __instance.orbit.vel; expectedDriverVelocity.Swizzle();
                owner.report.maximumDriverPositionErrorMeters = Math.Max(owner.report.maximumDriverPositionErrorMeters,
                    Magnitude(V(__instance.pos) + V(expectedDriverPosition) * -1));
                owner.report.maximumDriverVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumDriverVelocityErrorMetersPerSecond,
                    Magnitude(V(__instance.vel) + V(expectedDriverVelocity) * -1));
                long callback = Stopwatch.GetTimestamp() - __state.Started;
                long residual = Math.Max(0, callback - __state.SampleTicks - __state.SeedTicks);
                owner.callbackTicks += callback; owner.maximumCallbackTicks = Math.Max(owner.maximumCallbackTicks, callback);
                owner.sampleTicks += __state.SampleTicks; owner.maximumSampleTicks = Math.Max(owner.maximumSampleTicks, __state.SampleTicks);
                owner.seedTicks += __state.SeedTicks; owner.maximumSeedTicks = Math.Max(owner.maximumSeedTicks, __state.SeedTicks);
                owner.residualTicks += residual; owner.maximumResidualTicks = Math.Max(owner.maximumResidualTicks, residual);
                owner.report.candidateDriverCalls++; owner.report.suppressedStockPropagations++;
                bool accurate = owner.report.maximumPositionErrorMeters <= PositionToleranceMeters &&
                    owner.report.maximumVelocityErrorMetersPerSecond <= VelocityToleranceMetersPerSecond &&
                    owner.report.maximumInjectedPositionErrorMeters <= 1e-6 &&
                    owner.report.maximumInjectedVelocityErrorMetersPerSecond <= 1e-6 &&
                    owner.report.maximumDriverPositionErrorMeters <= 1e-6 &&
                    owner.report.maximumDriverVelocityErrorMetersPerSecond <= 1e-6 &&
                    owner.report.suppressedStockPropagations >= 1;
                if (!accurate) owner.Stop("comparison-outside-tolerance");
                else if (!owner.eventConfigured && owner.report.candidateDriverCalls >= MaximumCalls)
                {
                    owner.Stop("bounded-call-limit-reached");
                }
            }
            catch (Exception error) { owner.Fail("readback-failed:" + error.GetType().Name); }
        }

        static Exception DriverFinalizer(Exception __exception, DriverCall __state)
        {
            IndependentCoastAdapter owner = instance;
            if (__state != null && __state.Candidate && !__state.Consumed && owner != null)
                owner.Fail("driver-exited-with-unconsumed-token");
            token = null;
            return __exception;
        }

        bool Eligible(Vessel candidate)
        {
            OrbitDriver driver = candidate == null ? null : candidate.orbitDriver;
            return candidate != null && candidate == FlightGlobals.ActiveVessel && candidate.loaded && candidate.packed &&
                !FlightDriver.Pause && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH &&
                driver != null && DriverReady(driver) &&
                driver.updateMode == OrbitDriver.UpdateMode.UPDATE && candidate.orbit != null;
        }

        void Stop(string reason)
        {
            if (!active) return;
            active = false; stopRequested = true; report.reason = reason;
            if (reason == "bounded-call-limit-reached" || reason == "event-guard-reached") report.status = "complete";
            else if (reason == "comparison-outside-tolerance") report.status = "invalid";
            else if (report.status != "invalid") report.status = "released";
        }

        void ReleaseToStock()
        {
            try
            {
                if (vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.orbit == null ||
                    referenceBody == null || vessel.mainBody != referenceBody || vessel.orbit.referenceBody != referenceBody ||
                    !vessel.packed || vessel.orbitDriver == null ||
                    vessel.orbitDriver.updateMode != OrbitDriver.UpdateMode.UPDATE)
                {
                    if (report.status == "complete")
                    { report.status = "invalid"; report.reason = "release-domain-changed"; }
                    return;
                }
                double ut = Planetarium.GetUniversalTime();
                CoastingBody body = engine.SampleAt(ut).Bodies[0];
                vessel.orbit.UpdateFromStateVectors(V(body.Position), V(body.Velocity), referenceBody, ut);
                report.releasedToStock = true; report.finalGameUniversalTime = ut;
                report.frontierUnchangedByPresentation = engine.Current.TimeSeconds == report.forecastUniversalTime;
            }
            catch (Exception error) { report.errors++; report.status = "invalid"; report.reason = "release-failed:" + error.GetType().Name; }
        }

        void Fail(string reason)
        {
            active = false; failed = true;
            if (report != null) { report.errors++; report.status = "invalid"; report.reason = reason; }
        }

        void Finish()
        {
            if (finished) return; finished = true;
            report.authorityExitAttested = TargetsOwned(); Cleanup(); Export();
            if (quitAfter)
            {
                try { if (TimeWarp.CurrentRateIndex != 0) TimeWarp.SetRate(0, true); }
                catch (Exception error) { UnityEngine.Debug.LogException(error); }
                Application.Quit(report.status == "complete" && report.releasedToStock &&
                    report.authorityExitAttested && report.cleanupStatus == "removed-owned-patches" ? 0 : 2);
            }
        }

        static void RequireUncontested(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches != null && (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 ||
                patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0))
                throw new InvalidOperationException("Orbital target already has Harmony patches.");
        }

        bool TargetsOwned()
        {
            return HasOnlyOwner(driverTarget, 1, 1, 1) && HasOnlyOwner(orbitTarget, 1, 0, 0);
        }

        static bool HasOnlyOwner(MethodBase method, int prefixes, int postfixes, int finalizers)
        {
            if (method == null) return false;
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Prefixes.Count != prefixes || patches.Postfixes.Count != postfixes ||
                patches.Finalizers.Count != finalizers || patches.Transpilers.Count != 0) return false;
            foreach (Patch patch in patches.Prefixes) if (patch.owner != Owner) return false;
            foreach (Patch patch in patches.Postfixes) if (patch.owner != Owner) return false;
            foreach (Patch patch in patches.Finalizers) if (patch.owner != Owner) return false;
            return true;
        }

        void Cleanup()
        {
            bool removed = false;
            try { if (harmony != null) harmony.UnpatchAll(Owner); removed = !HasOwner(driverTarget) && !HasOwner(orbitTarget); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
            finally
            {
                report.cleanupStatus = removed ? "removed-owned-patches" : "cleanup-error";
                harmony = null; token = null; if (ReferenceEquals(instance, this)) instance = null;
            }
        }

        static bool HasOwner(MethodBase method)
        {
            if (method == null) return false;
            Patches patches = Harmony.GetPatchInfo(method); if (patches == null) return false;
            foreach (Patch patch in patches.Prefixes) if (patch.owner == Owner) return true;
            foreach (Patch patch in patches.Postfixes) if (patch.owner == Owner) return true;
            foreach (Patch patch in patches.Transpilers) if (patch.owner == Owner) return true;
            foreach (Patch patch in patches.Finalizers) if (patch.owner == Owner) return true;
            return false;
        }

        void Export()
        {
            if (exported || report == null) return; exported = true;
            try
            {
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                Directory.CreateDirectory(directory);
                string identity = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(directory, "coasting-adapter-" + identity + ".json"), ReportJson.Encode(report));
                File.WriteAllText(Path.Combine(directory, "coasting-cadence-" + identity + ".txt"), CadenceReport());
            }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        static Vec V(Vector3d value) { return new Vec(value.x, value.y, value.z); }
        static Vector3d V(Vec value) { return new Vector3d(value.X, value.Y, value.Z); }
        static double Magnitude(Vec value) { return Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static string Number(double value)
        {
            if (double.IsPositiveInfinity(value)) return "positive-infinity";
            if (double.IsNegativeInfinity(value)) return "negative-infinity";
            if (double.IsNaN(value)) return "nan";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static double ParseCadence(string[] arguments)
        {
            foreach (string argument in arguments)
                if (argument.StartsWith(CadencePrefix, StringComparison.Ordinal))
                {
                    double value;
                    if (!double.TryParse(argument.Substring(CadencePrefix.Length), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Invalid coast publication cadence.");
                    return value;
                }
            return 2;
        }

        void ParseEventConfiguration(string[] arguments)
        {
            string radius = FindArgument(arguments, EventRadiusPrefix);
            if (radius == null)
            {
                if (FindArgument(arguments, EventDirectionPrefix) != null ||
                    FindArgument(arguments, EventHorizonPrefix) != null || FindArgument(arguments, EventGuardPrefix) != null)
                    throw new ArgumentException("Event radius is required when any event option is present.");
                return;
            }
            eventConfigured = true; eventRadius = ParsePositive(radius, "event radius");
            string direction = FindArgument(arguments, EventDirectionPrefix);
            if (direction == null || !Enum.TryParse(direction, true, out eventDirection) ||
                !Enum.IsDefined(typeof(RadiusCrossingDirection), eventDirection))
                throw new ArgumentException("Event direction must be inward or outward.");
            string horizon = FindArgument(arguments, EventHorizonPrefix);
            string guard = FindArgument(arguments, EventGuardPrefix);
            eventHorizon = horizon == null ? DefaultEventHorizonSeconds : ParsePositive(horizon, "event horizon");
            eventGuard = guard == null ? DefaultEventGuardSeconds : ParsePositive(guard, "event guard");
            if (eventGuard >= eventHorizon) throw new ArgumentException("Event guard must be shorter than the search horizon.");
        }

        static string FindArgument(string[] arguments, string prefix)
        {
            foreach (string argument in arguments)
                if (argument.StartsWith(prefix, StringComparison.Ordinal)) return argument.Substring(prefix.Length);
            return null;
        }

        static double ParsePositive(string text, string name)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                !Finite(value) || value <= 0) throw new ArgumentException("Invalid " + name + ".");
            return value;
        }

        CoastingBody SampleBody(double universalTime) { return engine.SampleAt(universalTime).Bodies[0]; }

        string CadenceReport()
        {
            string newline = Environment.NewLine;
            return "presentationStrategy=" + (cadence.IsDirect ? "direct" : "hermite") + newline +
                "publicationIntervalSeconds=" + cadence.IntervalSeconds.ToString("R", CultureInfo.InvariantCulture) + newline +
                "driverCallbacks=" + report.candidateDriverCalls + newline +
                "engineSamples=" + cadence.EngineSampleCount + newline +
                "stopwatchFrequency=" + Stopwatch.Frequency + newline +
                "callbackTicksTotal=" + callbackTicks + newline + "callbackTicksMaximum=" + maximumCallbackTicks + newline +
                "sampleTicksTotal=" + sampleTicks + newline + "sampleTicksMaximum=" + maximumSampleTicks + newline +
                "seedTicksTotal=" + seedTicks + newline + "seedTicksMaximum=" + maximumSeedTicks + newline +
                "residualTicksTotal=" + residualTicks + newline + "residualTicksMaximum=" + maximumResidualTicks + newline +
                "frontierUnchangedByPresentation=" + report.frontierUnchangedByPresentation + newline;
        }

        public void OnDestroy()
        {
            if (!requested || finished) return;
            if (active) { report.status = "interrupted"; report.reason = "flight-addon-destroyed"; ReleaseToStock(); }
            Finish();
        }
    }
}
