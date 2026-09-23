using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class DryBuoyancyRuntime : IDisposable, IPlayerLoopBracketObserver
    {
        sealed class Entry { public Part part; public PartBuoyancy buoyancy; public bool disabledByOwner; }
        const string Owner = "continuum.dry-buoyancy-batch";
        readonly MethodInfo target = AccessTools.DeclaredMethod(typeof(PartBuoyancy), "FixedUpdate");
        readonly List<Entry> entries = new List<Entry>();
        Vessel vessel;
        int partCount;
        bool installed, disposed;
        public DryBuoyancyReport Report { get; private set; }
        public DryBuoyancyRuntime() { Report = new DryBuoyancyReport(); }

        public void Start()
        {
            if (disposed || installed) throw new InvalidOperationException("Dry buoyancy batch supports one lifetime.");
            if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
            { Report.status = "unavailable"; Report.detail = "KSP 1.12.5 is required."; return; }
            if (target == null) { Report.status = "unavailable"; Report.detail = "PartBuoyancy.FixedUpdate not found."; return; }
            Patches existing = Harmony.GetPatchInfo(target);
            string[] foreign = existing == null ? new string[0] : existing.Owners.Where(owner => owner != Owner).Distinct().OrderBy(owner => owner).ToArray();
            Report.foreignOwners = foreign;
            if (foreign.Length != 0) { Report.status = "abstained"; Report.detail = "Foreign Harmony ownership is present."; return; }
            vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || vessel.parts == null) { Report.status = "unavailable"; Report.detail = "Active vessel is unavailable."; return; }
            partCount = vessel.parts.Count;
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                PartBuoyancy buoyancy = part.GetComponent<PartBuoyancy>();
                if (buoyancy != null && buoyancy.enabled) entries.Add(new Entry { part = part, buoyancy = buoyancy });
            }
            if (entries.Count == 0) { Report.status = "unavailable"; Report.detail = "No initially enabled buoyancy components were found."; return; }
            installed = true; Report.status = "installed"; Report.installationStatus = "initial-enabled-component-census";
            Report.cleanupStatus = "installed"; Report.ownedComponents = entries.Count;
        }

        public void Before(string scope, int frame, double time)
        {
            if (!installed || scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            Report.fixedSteps++;
            try
            {
                if (!EligibleVessel() || !entries.All(EligiblePart))
                { Report.fallbacks += entries.Count; Restore(); return; }
                foreach (Entry entry in entries)
                {
                    PublishDry(entry);
                    if (!VerifyDry(entry)) { Report.errors++; Restore(); return; }
                    Report.dryPublications++; Report.verifiedPublications++;
                }
                foreach (Entry entry in entries)
                {
                    if (!entry.buoyancy.enabled) continue;
                    entry.buoyancy.enabled = false; entry.disabledByOwner = true;
                }
                if (entries.Any(entry => !entry.disabledByOwner || entry.buoyancy.enabled))
                { Report.errors++; Restore(); return; }
                Report.bypassed += entries.Count;
            }
            catch (Exception error)
            { Report.errors++; Report.detail = error.GetType().Name + ": " + error.Message; Restore(); }
        }

        public void After(string scope, int frame, double time) { }
        public void Fault(string scope, Exception error)
        {
            if (scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            Report.errors++; Report.detail = error == null ? "PlayerLoop fault." : error.GetType().Name + ": " + error.Message; Restore();
        }

        bool EligibleVessel()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || vessel == null || vessel.parts == null ||
                !ReferenceEquals(vessel, FlightGlobals.ActiveVessel) || vessel.parts.Count != partCount) return false;
            CelestialBody body = vessel.mainBody; Vector3 size = vessel.vesselSize;
            return DryBuoyancyAdmission.Decide(new DryBuoyancyVesselState {
                flightReady = true, active = true, loaded = vessel.loaded, packed = vessel.packed,
                orbiting = vessel.situation == Vessel.Situations.ORBITING, bodyPresent = body != null,
                bodyHasOcean = body != null && body.ocean, altitudeMeters = vessel.altitude, vesselBoundMeters = size.magnitude,
                radialSpeedMetersPerSecond = vessel.verticalSpeed, fixedDeltaSeconds = TimeWarp.fixedDeltaTime
            }, new DryBuoyancyPartState { bodyInitialized = true, bodyMatchesVessel = true, settledDry = true }) == DryBuoyancyDisposition.SkipStock;
        }

        bool EligiblePart(Entry entry)
        {
            if (entry == null || entry.part == null || entry.buoyancy == null || entry.part.vessel != vessel) return false;
            PartBuoyancy value = entry.buoyancy;
            bool available = entry.disabledByOwner ? !value.enabled : value.enabled;
            return available && DryBuoyancyAdmission.Decide(new DryBuoyancyVesselState {
                flightReady = true, active = true, loaded = true, orbiting = true, bodyPresent = true, bodyHasOcean = true,
                altitudeMeters = DryBuoyancyAdmission.MinimumClearanceMeters + 1, vesselBoundMeters = 0,
                radialSpeedMetersPerSecond = 0, fixedDeltaSeconds = TimeWarp.fixedDeltaTime
            }, new DryBuoyancyPartState {
                bodyInitialized = value.body != null, bodyMatchesVessel = ReferenceEquals(value.body, vessel.mainBody),
                splashed = value.splashed, depthMeters = value.depth,
                settledDry = !value.IsInvoking() && !value.wasSplashed && value.splashedCounter == 0 &&
                    !entry.part.WaterContact && value.submergedPortion == 0 && entry.part.submergedPortion == 0 &&
                    value.drag == 0 && value.lastBuoyantForce == Vector3.zero
            }) == DryBuoyancyDisposition.SkipStock;
        }

        static void PublishDry(Entry entry)
        {
            Part part = entry.part; PartBuoyancy value = entry.buoyancy;
            value.dead = false; value.body = part.vessel.mainBody;
            value.centerOfBuoyancy = part.partTransform.position + part.partTransform.rotation * part.CenterOfBuoyancy;
            value.centerOfDisplacement = part.partTransform.position + part.partTransform.rotation * part.CenterOfDisplacement;
            part.WaterContact = false; part.submergedPortion = value.submergedPortion = 0;
            value.drag = 0; value.splashedCounter = 0; value.lastBuoyantForce = Vector3.zero;
            value.lastForcePosition = value.centerOfBuoyancy;
            part.submergedDragScalar = value.dragScalar; part.submergedLiftScalar = value.liftScalar; value.wasSplashed = false;
        }

        static bool VerifyDry(Entry entry)
        {
            Part part = entry.part; PartBuoyancy value = entry.buoyancy;
            return !value.dead && ReferenceEquals(value.body, part.vessel.mainBody) && !part.WaterContact &&
                value.submergedPortion == 0 && part.submergedPortion == 0 && value.drag == 0 && value.splashedCounter == 0 &&
                value.lastBuoyantForce == Vector3.zero && !value.wasSplashed;
        }

        void Restore()
        {
            foreach (Entry entry in entries)
            {
                if (!entry.disabledByOwner) continue;
                if (entry.buoyancy == null) { Report.errors++; entry.disabledByOwner = false; continue; }
                entry.buoyancy.enabled = true; entry.disabledByOwner = false;
                if (!entry.buoyancy.enabled) Report.errors++;
            }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true; Restore();
            Report.cleanupStatus = entries.Any(entry => entry.disabledByOwner) ? "cleanup-error" : "restored-owned-enables";
            installed = false;
            if (Report.status == "installed") Report.status = Report.cleanupStatus == "restored-owned-enables" && Report.errors == 0 &&
                Report.bypassed > 0 && Report.dryPublications == Report.bypassed && Report.verifiedPublications == Report.bypassed
                ? "complete" : "invalid";
        }
    }
}
