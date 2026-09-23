using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AeroSetDragProviderQualification : MonoBehaviour
    {
        const string Flag = "--continuum-live-setdrag-provider";
        const string QuitFlag = "--continuum-setdrag-quit-after-qualification";
        const string Owner = "continuum.live-setdrag-provider";
        const int RequiredShadowMatches = 128;
        const int MaximumSubstitutions = 256;
        static readonly AccessTools.FieldRef<DragCubeList, DragCubeList.CubeData> CubeData =
            AccessTools.FieldRefAccess<DragCubeList, DragCubeList.CubeData>("cubeData");
        static AeroSetDragProviderQualification instance;
        Harmony harmony;
        MethodInfo target;
        AeroSetDragSubstitutionReport report;
        bool requested, active, exported, quitAfterQualification;

        struct CallState
        {
            public bool Shadow;
            public DragCubeList.CubeData Candidate;
            public long StockStarted;
        }

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Flag) < 0) return;
            requested = true;
            quitAfterQualification = Array.IndexOf(Environment.GetCommandLineArgs(), QuitFlag) >= 0;
            report = new AeroSetDragSubstitutionReport {
                requiredShadowMatches = RequiredShadowMatches, maximumSubstitutions = MaximumSubstitutions,
                status = "active"
            };
            active = true;
            try
            {
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("KSP 1.12.5 is required.");
                target = AccessTools.DeclaredMethod(typeof(DragCubeList), "SetDrag", new[] { typeof(Vector3), typeof(float) });
                if (target == null) throw new MissingMethodException("Pinned stock SetDrag seam is unavailable.");
                RequireUncontested(target);
                instance = this; harmony = new Harmony(Owner);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroSetDragProviderQualification), "Prefix"), Priority.First),
                    postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroSetDragProviderQualification), "Postfix"), Priority.Last));
            }
            catch (Exception error) { Stop("installation-failed:" + error.GetType().Name); Finish(); }
        }

        static bool Prefix(DragCubeList __instance, Vector3 vector, float machNumber, out CallState __state)
        {
            __state = new CallState();
            AeroSetDragProviderQualification owner = instance;
            if (owner == null || !owner.active) return true;
            if (!TargetStillOwned(owner.target)) { owner.Stop("patch-graph-changed"); owner.report.stockFallbacks++; return true; }
            try
            {
                long started = Stopwatch.GetTimestamp();
                __state.Candidate = Calculate(__instance, vector, machNumber);
                owner.report.candidateStopwatchTicks += Stopwatch.GetTimestamp() - started;
                if (owner.report.matchedCompleteOutputs < RequiredShadowMatches)
                {
                    __state.Shadow = true; __state.StockStarted = Stopwatch.GetTimestamp(); return true;
                }
                CubeData(__instance) = __state.Candidate;
                owner.report.suppressedOriginalCalls++;
                if (owner.report.suppressedOriginalCalls >= MaximumSubstitutions)
                    owner.Stop("bounded-substitution-limit-reached");
                return false;
            }
            catch (Exception error)
            {
                owner.report.stockFallbacks++; owner.Stop("candidate-failed:" + error.GetType().Name); return true;
            }
        }

        static void Postfix(DragCubeList __instance, CallState __state)
        {
            AeroSetDragProviderQualification owner = instance;
            if (owner == null || !__state.Shadow) return;
            owner.report.stockStopwatchTicks += Stopwatch.GetTimestamp() - __state.StockStarted;
            owner.report.shadowComparisons++;
            double error;
            if (!Equivalent(__state.Candidate, Snapshot(__instance), out error))
            { owner.report.maximumRelativeError = Math.Max(owner.report.maximumRelativeError, error); owner.Stop("complete-output-mismatch"); return; }
            owner.report.maximumRelativeError = Math.Max(owner.report.maximumRelativeError, error);
            owner.report.matchedCompleteOutputs++;
        }

        static DragCubeList.CubeData Calculate(DragCubeList cubes, Vector3 input, float mach)
        {
            Vector3 direction = -input;
            if (cubes.RotateDragVector) direction = cubes.DragVectorRotation * direction;
            PhysicsGlobals.SurfaceCurvesList curves = cubes.SurfaceCurves;
            float[] weightedDrag = cubes.WeightedDrag;
            var areas = Faces(cubes.AreaOccluded); var drags = Faces(weightedDrag); var depths = Faces(cubes.WeightedDepth);
            var dragCd = new AeroFaceValues(
                cubes.DragCurveCd.Evaluate(weightedDrag[0]), cubes.DragCurveCd.Evaluate(weightedDrag[1]),
                cubes.DragCurveCd.Evaluate(weightedDrag[2]), cubes.DragCurveCd.Evaluate(weightedDrag[3]),
                cubes.DragCurveCd.Evaluate(weightedDrag[4]), cubes.DragCurveCd.Evaluate(weightedDrag[5]));
            var bodyLift = new AeroFaceValues(
                cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, direction.x)), cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, -direction.x)),
                cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, direction.y)), cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, -direction.y)),
                cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, direction.z)), cubes.BodyLiftCurve.liftCurve.Evaluate(Math.Max(0, -direction.z)));
            AeroCompleteSetDragResult result = AeroCompleteSetDrag.Evaluate(new Vec(direction.x, direction.y, direction.z),
                areas, drags, depths, dragCd, bodyLift, curves.dragCurveTail.Evaluate(mach),
                curves.dragCurveSurface.Evaluate(mach), curves.dragCurveMultiplier.Evaluate(mach),
                curves.dragCurveTip.Evaluate(mach), cubes.DragCurveCdPower.Evaluate(mach));
            return new DragCubeList.CubeData {
                dragVector = Vector(result.DragVector), liftForce = Vector(result.LiftForce),
                area = (float)result.AreaSquareMeters, areaDrag = (float)result.AreaDragSquareMeters,
                depth = (float)result.DepthMeters, crossSectionalArea = (float)result.CrossSectionalAreaSquareMeters,
                exposedArea = (float)result.ExposedAreaSquareMeters, dragCoeff = (float)result.DragCoefficient,
                taperDot = (float)result.TaperDot
            };
        }

        static AeroFaceValues Faces(float[] values) => new AeroFaceValues(values[0], values[1], values[2], values[3], values[4], values[5]);
        static Vector3 Vector(Vec value) => new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        static DragCubeList.CubeData Snapshot(DragCubeList cubes) => new DragCubeList.CubeData {
            dragVector = cubes.DragVector, liftForce = cubes.LiftForce, area = cubes.Area,
            areaDrag = cubes.AreaDrag, depth = cubes.Depth, crossSectionalArea = cubes.CrossSectionalArea,
            exposedArea = cubes.ExposedArea, dragCoeff = cubes.DragCoeff, taperDot = cubes.TaperDot
        };
        static bool Equivalent(DragCubeList.CubeData candidate, DragCubeList.CubeData stock, out double maximum)
        {
            maximum = 0;
            foreach (double error in new[] {
                Error(candidate.dragVector.x, stock.dragVector.x), Error(candidate.dragVector.y, stock.dragVector.y), Error(candidate.dragVector.z, stock.dragVector.z),
                Error(candidate.liftForce.x, stock.liftForce.x), Error(candidate.liftForce.y, stock.liftForce.y), Error(candidate.liftForce.z, stock.liftForce.z),
                Error(candidate.area, stock.area), Error(candidate.areaDrag, stock.areaDrag), Error(candidate.depth, stock.depth),
                Error(candidate.crossSectionalArea, stock.crossSectionalArea), Error(candidate.exposedArea, stock.exposedArea),
                Error(candidate.dragCoeff, stock.dragCoeff), Error(candidate.taperDot, stock.taperDot) }) maximum = Math.Max(maximum, error);
            return maximum <= 2e-5;
        }
        static double Error(float candidate, float stock)
        {
            if (float.IsNaN(candidate) || float.IsInfinity(candidate) || float.IsNaN(stock) || float.IsInfinity(stock)) return 1;
            return Math.Abs(candidate - stock) / Math.Max(1, Math.Abs(stock));
        }
        static void RequireUncontested(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches != null && (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 ||
                patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0))
                throw new InvalidOperationException("SetDrag already has Harmony patches.");
        }
        static bool TargetStillOwned(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            return patches != null && patches.Prefixes.Count == 1 && patches.Postfixes.Count == 1 &&
                patches.Transpilers.Count == 0 && patches.Finalizers.Count == 0 &&
                patches.Prefixes[0].owner == Owner && patches.Postfixes[0].owner == Owner;
        }
        void Stop(string reason)
        {
            if (!active) return; active = false; report.reason = reason;
            report.status = reason == "bounded-substitution-limit-reached" ? "complete" : "abstained";
        }
        public void Update() { if (requested && !active && !exported) Finish(); }
        void Finish()
        {
            Cleanup(); Export();
            if (quitAfterQualification)
                Application.Quit(report != null && report.status == "complete" &&
                    report.cleanupStatus == "removed-owned-patches" ? 0 : 2);
        }
        void Cleanup()
        {
            bool registered = harmony != null;
            if (harmony != null) harmony.UnpatchAll(Owner);
            bool removed = target == null || !HasOwner(target);
            if (report != null) report.RecordCleanup(registered, removed);
            harmony = null;
            if (ReferenceEquals(instance, this)) instance = null;
        }
        static bool HasOwner(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return false;
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
                File.WriteAllText(Path.Combine(directory, "setdrag-substitution-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") +
                    "-" + Guid.NewGuid().ToString("N") + ".json"), ReportJson.Encode(report));
            }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }
        public void OnDestroy()
        {
            if (!requested) return; if (active) Stop("flight-addon-destroyed"); Cleanup(); Export();
        }
    }
}
