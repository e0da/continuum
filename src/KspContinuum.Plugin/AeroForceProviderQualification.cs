using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AeroForceProviderQualification : MonoBehaviour
    {
        const string Flag = "--continuum-live-aero-provider";
        const string Owner = "continuum.live-aero-provider";
        const int MaximumPublications = 256;
        static AeroForceProviderQualification instance;
        Harmony harmony;
        MethodInfo target;
        AeroForceSubstitution state;
        AeroForceSubstitutionReport report;
        bool requested, exported;

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Flag) < 0) return;
            requested = true;
            report = new AeroForceSubstitutionReport();
            state = new AeroForceSubstitution(report, MaximumPublications);
            try
            {
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("KSP 1.12.5 is required.");
                target = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "ApplyAeroDrag",
                    new[] { typeof(Part), typeof(Rigidbody), typeof(ForceMode) });
                if (target == null) throw new MissingMethodException("Pinned stock body-drag seam is unavailable.");
                RequireUncontestedTarget(target);
                instance = this;
                harmony = new Harmony(Owner);
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.DeclaredMethod(typeof(AeroForceProviderQualification), "ApplyDragPrefix"), Priority.First));
                state.Start();
            }
            catch (Exception error)
            {
                state.Start(); state.Stop("installation-failed:" + error.GetType().Name);
                Cleanup(); Export();
            }
        }

        static void RequireUncontestedTarget(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return;
            if (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 ||
                patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0)
                throw new InvalidOperationException("Body-drag target already has Harmony patches.");
        }

        static bool TargetStillOwnedExclusively(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Prefixes.Count != 1 || patches.Postfixes.Count != 0 ||
                patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0) return false;
            return patches.Prefixes[0].owner == Owner &&
                patches.Prefixes[0].PatchMethod.DeclaringType == typeof(AeroForceProviderQualification);
        }

        static bool ApplyDragPrefix(Part part, Rigidbody rbPossible, ForceMode mode)
        {
            AeroForceProviderQualification owner = instance;
            if (owner == null || owner.state == null || !owner.state.IsActive) return true;
            return owner.Apply(part, rbPossible, mode);
        }

        bool Apply(Part part, Rigidbody body, ForceMode mode)
        {
            if (!Eligible(part, body) || !TargetStillOwnedExclusively(target))
            { state.TryBeginPublication(false, true); return true; }
            try
            {
                Vector3 force = -part.dragVectorDir * part.dragScalar;
                Vector3 position = body != part.rb && PhysicsGlobals.ApplyDragToNonPhysicsPartsAtParentCoM
                    ? body.worldCenterOfMass : part.partTransform.TransformPoint(part.CoPOffset);
                if (!state.TryBeginPublication(true, Finite(force) && Finite(position))) return true;
                body.AddForceAtPosition(force, position, mode);
                state.Published();
                return false;
            }
            catch (Exception error)
            {
                state.PublicationFailed(error.GetType().Name);
                return true;
            }
        }

        public void Update()
        {
            if (requested && state != null && !state.IsActive && !exported) { Cleanup(); Export(); }
        }

        static bool Eligible(Part part, Rigidbody body)
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            return part != null && body != null && vessel != null && ReferenceEquals(part.vessel, vessel) &&
                vessel.loaded && !vessel.packed && !FlightDriver.Pause && TimeWarp.CurrentRate == 1 && Time.timeScale == 1;
        }

        static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        void Cleanup()
        {
            if (harmony != null) { harmony.UnpatchAll(Owner); harmony = null; }
            if (ReferenceEquals(instance, this)) instance = null;
        }

        void Export()
        {
            if (exported || report == null) return;
            exported = true;
            try
            {
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "aero-force-substitution-" +
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, ReportJson.Encode(report));
            }
            catch (Exception error) { Debug.LogException(error); }
        }

        public void OnDestroy()
        {
            if (!requested) return;
            if (state != null && state.IsActive) state.Stop("flight-addon-destroyed");
            Cleanup(); Export();
        }
    }
}
