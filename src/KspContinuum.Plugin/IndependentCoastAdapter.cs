using System;
using System.IO;
using System.Globalization;
using System.Reflection;
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
        const string Owner = "continuum.independent-coast-adapter";
        const int MaximumCalls = 256;
        const double ForecastSeconds = 600;
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
            public double UniversalTime;
            public CoastingSnapshot Sample;
        }

        Harmony harmony;
        MethodInfo driverTarget, orbitTarget;
        CoastingAdapterReport report;
        CoastingEngine engine;
        Task<CoastingAdvanceResult> forecast;
        Orbit stockReference;
        Vessel vessel;
        CelestialBody referenceBody;
        bool requested, active, failed, stopRequested, finished, exported, quitAfter;

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, Flag) < 0) return;
            requested = true; quitAfter = Array.IndexOf(arguments, QuitFlag) >= 0;
            report = new CoastingAdapterReport { status = "waiting-for-packed-orbit" };
            try
            {
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
                if (failed) { Finish(); return; }
                if (stopRequested) { ReleaseToStock(); Finish(); return; }
                if (!active && engine == null) TrySeed();
                if (!active && forecast != null && forecast.IsCompleted) AdmitForecast();
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
                current.situation != Vessel.Situations.ORBITING) return;
            if (!current.packed)
            {
                if (TimeWarp.CurrentRateIndex == 0) TimeWarp.SetRate(1, true);
                return;
            }
            if (!Eligible(current)) return;
            double ut = Planetarium.GetUniversalTime();
            double atmosphere = current.mainBody.atmosphere ? current.mainBody.atmosphereDepth : 0;
            bool hasNextPatch = current.orbit.nextPatch != null;
            bool nextPatchAfterForecast = Finite(current.orbit.EndUT) && current.orbit.EndUT > ut + ForecastSeconds;
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
            stockReference = new Orbit();
            stockReference.UpdateFromStateVectors(position, velocity, referenceBody, ut);
            report.seedUniversalTime = ut; report.vesselId = vessel.id.ToString("D");
            report.referenceBody = referenceBody.bodyName; report.status = "forecasting";
            forecast = Task.Run(() => engine.AdvanceTo(ut + ForecastSeconds, 3600));
        }

        void AdmitForecast()
        {
            if (forecast.IsFaulted || forecast.IsCanceled)
            { Fail("forecast-failed"); forecast = null; return; }
            CoastingAdvanceResult result = forecast.Result;
            report.forecastUniversalTime = result.Final.TimeSeconds;
            report.forecastCompletedBeforePresentation = Planetarium.GetUniversalTime() < result.Final.TimeSeconds;
            if (!report.forecastCompletedBeforePresentation)
            { Fail("forecast-did-not-lead-presentation"); forecast = null; return; }
            report.authorityAdmissionAttested = TargetsOwned();
            if (!report.authorityAdmissionAttested) { Fail("authority-admission-failed"); forecast = null; return; }
            active = true; report.status = "active";
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
                CoastingSnapshot sample = owner.engine.SampleAt(ut);
                CoastingBody body = sample.Bodies[0];
                __instance.orbit.UpdateFromStateVectors(V(body.Position), V(body.Velocity), owner.referenceBody, ut);
                __state.Candidate = true; __state.UniversalTime = ut; __state.Sample = sample;
                token = new SuppressionToken { Orbit = __instance.orbit,
                    UniversalTimeBits = BitConverter.DoubleToInt64Bits(ut), Call = __state };
                return true;
            }
            catch (Exception error)
            {
                token = null; owner.report.errors++; owner.report.stockFallbacks++;
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
                CoastingBody expected = __state.Sample.Bodies[0];
                Vector3d position = owner.stockReference.getRelativePositionAtUT(__state.UniversalTime);
                Vector3d velocity = owner.stockReference.getOrbitalVelocityAtUT(__state.UniversalTime);
                owner.report.maximumPositionErrorMeters = Math.Max(owner.report.maximumPositionErrorMeters,
                    Magnitude(V(position) + expected.Position * -1));
                owner.report.maximumVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumVelocityErrorMetersPerSecond,
                    Magnitude(V(velocity) + expected.Velocity * -1));
                Vector3d injectedPosition = __instance.orbit.getRelativePositionAtUT(__state.UniversalTime);
                Vector3d injectedVelocity = __instance.orbit.getOrbitalVelocityAtUT(__state.UniversalTime);
                owner.report.maximumInjectedPositionErrorMeters = Math.Max(owner.report.maximumInjectedPositionErrorMeters,
                    Magnitude(V(injectedPosition) + expected.Position * -1));
                owner.report.maximumInjectedVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumInjectedVelocityErrorMetersPerSecond,
                    Magnitude(V(injectedVelocity) + expected.Velocity * -1));
                Vector3d expectedDriverPosition = __instance.orbit.pos; expectedDriverPosition.Swizzle();
                Vector3d expectedDriverVelocity = __instance.orbit.vel; expectedDriverVelocity.Swizzle();
                owner.report.maximumDriverPositionErrorMeters = Math.Max(owner.report.maximumDriverPositionErrorMeters,
                    Magnitude(V(__instance.pos) + V(expectedDriverPosition) * -1));
                owner.report.maximumDriverVelocityErrorMetersPerSecond = Math.Max(owner.report.maximumDriverVelocityErrorMetersPerSecond,
                    Magnitude(V(__instance.vel) + V(expectedDriverVelocity) * -1));
                owner.report.candidateDriverCalls++; owner.report.suppressedStockPropagations++;
                if (owner.report.candidateDriverCalls >= MaximumCalls)
                {
                    bool accurate = owner.report.maximumPositionErrorMeters <= PositionToleranceMeters &&
                        owner.report.maximumVelocityErrorMetersPerSecond <= VelocityToleranceMetersPerSecond &&
                        owner.report.maximumInjectedPositionErrorMeters <= 1e-6 &&
                        owner.report.maximumInjectedVelocityErrorMetersPerSecond <= 1e-6 &&
                        owner.report.maximumDriverPositionErrorMeters <= 1e-6 &&
                        owner.report.maximumDriverVelocityErrorMetersPerSecond <= 1e-6;
                    owner.Stop(accurate ? "bounded-call-limit-reached" : "comparison-outside-tolerance");
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
                !FlightDriver.Pause && driver != null && DriverReady(driver) &&
                driver.updateMode == OrbitDriver.UpdateMode.UPDATE && candidate.orbit != null;
        }

        void Stop(string reason)
        {
            if (!active) return;
            active = false; stopRequested = true; report.reason = reason;
            if (reason == "bounded-call-limit-reached") report.status = "complete";
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
                File.WriteAllText(Path.Combine(directory, "coasting-adapter-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") +
                    "-" + Guid.NewGuid().ToString("N") + ".json"), ReportJson.Encode(report));
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

        public void OnDestroy()
        {
            if (!requested || finished) return;
            if (active) { report.status = "interrupted"; report.reason = "flight-addon-destroyed"; ReleaseToStock(); }
            Finish();
        }
    }
}
