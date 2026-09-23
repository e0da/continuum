using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class DryBuoyancyRuntime : IDisposable
    {
        const string Owner = "continuum.dry-buoyancy-admission";
        static DryBuoyancyRuntime activeOwner;
        readonly Harmony harmony = new Harmony(Owner);
        readonly MethodInfo target = AccessTools.DeclaredMethod(typeof(PartBuoyancy), "FixedUpdate");
        bool installed, disposed;

        public DryBuoyancyReport Report { get; private set; }

        public DryBuoyancyRuntime() { Report = new DryBuoyancyReport(); }

        public void Start()
        {
            if (disposed || installed || activeOwner != null) throw new InvalidOperationException("Dry buoyancy admission supports one owner.");
            if (target == null) { Report.status = "unavailable"; Report.detail = "PartBuoyancy.FixedUpdate not found."; return; }
            Patches existing = Harmony.GetPatchInfo(target);
            string[] foreign = existing == null ? new string[0] : existing.Owners.Where(owner => owner != Owner).Distinct().OrderBy(owner => owner).ToArray();
            Report.foreignOwners = foreign;
            if (foreign.Length != 0) { Report.status = "abstained"; Report.detail = "Foreign Harmony ownership is present."; return; }
            activeOwner = this;
            try
            {
                harmony.Patch(target, new HarmonyMethod(AccessTools.DeclaredMethod(typeof(DryBuoyancyRuntime), "Prefix")) { priority = Priority.First });
                Patches readback = Harmony.GetPatchInfo(target);
                if (readback == null || !readback.Owners.Contains(Owner)) throw new InvalidOperationException("Owned prefix missing after installation.");
                installed = true; Report.status = "installed"; Report.installationStatus = "owned-prefix-readback"; Report.cleanupStatus = "installed";
            }
            catch (Exception error)
            {
                Report.status = "unavailable"; Report.detail = error.GetType().Name + ": " + error.Message; Remove();
            }
        }

        static bool Prefix(PartBuoyancy __instance, Part ___part)
        {
            DryBuoyancyRuntime owner = activeOwner;
            if (owner == null || !owner.installed) return true;
            owner.Report.calls++;
            try
            {
                Vessel vessel = ___part == null ? null : ___part.vessel;
                CelestialBody mainBody = vessel == null ? null : vessel.mainBody;
                Vector3 size = vessel == null ? Vector3.zero : vessel.vesselSize;
                var vesselState = new DryBuoyancyVesselState {
                    flightReady = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready,
                    active = vessel != null && ReferenceEquals(vessel, FlightGlobals.ActiveVessel),
                    loaded = vessel != null && vessel.loaded,
                    packed = vessel == null || vessel.packed,
                    orbiting = vessel != null && vessel.situation == Vessel.Situations.ORBITING,
                    bodyPresent = mainBody != null,
                    altitudeMeters = vessel == null ? double.NaN : vessel.altitude,
                    vesselBoundMeters = size.magnitude,
                    radialSpeedMetersPerSecond = vessel == null ? double.NaN : vessel.verticalSpeed,
                    fixedDeltaSeconds = Time.fixedDeltaTime
                };
                var partState = new DryBuoyancyPartState {
                    bodyInitialized = __instance.body != null,
                    bodyMatchesVessel = ReferenceEquals(__instance.body, mainBody),
                    splashed = __instance.splashed,
                    depthMeters = __instance.depth
                };
                if (DryBuoyancyAdmission.Decide(vesselState, partState) == DryBuoyancyDisposition.SkipStock)
                {
                    // Preserve the stock dry-state publications consumed by force integration. Geometry/depth
                    // diagnostics deliberately remain at their last dry values in this experimental regime.
                    __instance.dead = false;
                    __instance.body = mainBody;
                    __instance.centerOfBuoyancy = ___part.partTransform.position + ___part.partTransform.rotation * ___part.CenterOfBuoyancy;
                    __instance.centerOfDisplacement = ___part.partTransform.position + ___part.partTransform.rotation * ___part.CenterOfDisplacement;
                    ___part.WaterContact = false;
                    ___part.submergedPortion = __instance.submergedPortion = 0;
                    __instance.drag = 0;
                    __instance.lastBuoyantForce = Vector3.zero;
                    __instance.lastForcePosition = __instance.centerOfBuoyancy;
                    ___part.submergedDragScalar = __instance.dragScalar;
                    ___part.submergedLiftScalar = __instance.liftScalar;
                    __instance.wasSplashed = false;
                    owner.Report.dryPublications++; owner.Report.bypassed++; return false;
                }
                owner.Report.fallbacks++; return true;
            }
            catch (Exception error)
            {
                owner.Report.errors++; owner.Report.detail = error.GetType().Name + ": " + error.Message;
                return true;
            }
        }

        void Remove()
        {
            try
            {
                harmony.UnpatchAll(Owner);
                Patches readback = target == null ? null : Harmony.GetPatchInfo(target);
                Report.cleanupStatus = readback != null && readback.Owners.Contains(Owner) ? "cleanup-error" : "removed-owned-prefix";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Report.detail = error.GetType().Name + ": " + error.Message; }
            finally { installed = false; if (ReferenceEquals(activeOwner, this)) activeOwner = null; }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true; Remove();
            if (Report.status == "installed") Report.status = Report.cleanupStatus == "removed-owned-prefix" && Report.errors == 0 && Report.bypassed > 0
                ? "complete" : "invalid";
        }
    }
}
