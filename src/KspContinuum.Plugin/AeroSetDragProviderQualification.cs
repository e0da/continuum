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
        const int RequiredWarmupMatches = 32;
        const int RequiredMeasuredMatches = 256;
        const int MaximumSubstitutions = 256;
        static readonly AccessTools.FieldRef<DragCubeList, DragCubeList.CubeData> CubeData =
            AccessTools.FieldRefAccess<DragCubeList, DragCubeList.CubeData>("cubeData");
        static AeroSetDragProviderQualification instance;
        Harmony harmony;
        MethodInfo target;
        AeroSetDragSubstitutionReport report;
        bool requested, active, exported, quitAfterQualification;
        float patchGraphFixedTime = float.NaN;

        struct CallState
        {
            public bool Shadow;
            public bool Measured;
            public DragCubeList.CubeData Candidate;
            public long StockStarted;
        }

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Flag) < 0) return;
            requested = true;
            quitAfterQualification = Array.IndexOf(Environment.GetCommandLineArgs(), QuitFlag) >= 0;
            report = new AeroSetDragSubstitutionReport {
                requiredWarmupMatches = RequiredWarmupMatches, requiredMeasuredMatches = RequiredMeasuredMatches,
                maximumSubstitutions = MaximumSubstitutions, stopwatchFrequency = Stopwatch.Frequency, status = "active"
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
            try
            {
                int requiredShadow = RequiredWarmupMatches + RequiredMeasuredMatches;
                bool shadow = owner.report.matchedCompleteOutputs < requiredShadow;
                bool measured = owner.report.matchedCompleteOutputs >= RequiredWarmupMatches &&
                    owner.report.matchedCompleteOutputs < requiredShadow;
                long graphBefore = owner.report.measuredPatchGraphStopwatchTicks;
                if (shadow && !owner.TargetOwnedForStep(measured))
                { owner.Stop("patch-graph-changed"); owner.report.stockFallbacks++; return true; }
                if (!shadow && owner.report.suppressedOriginalCalls == 0)
                {
                    owner.report.authorityAdmissionAttested = TargetStillOwned(owner.target);
                    if (!owner.report.authorityAdmissionAttested)
                    { owner.Stop("authority-admission-patch-graph-changed"); owner.report.stockFallbacks++; return true; }
                }
                if (measured)
                {
                    long admissionTicks, guardedTicks;
                    DragCubeList.CubeData admission, guarded;
                    if ((owner.report.shadowMeasuredComparisons & 1) == 0)
                    {
                        admission = TimedCalculateAndPublish(__instance, vector, machNumber, out admissionTicks);
                        guarded = TimedCalculateAndPublish(__instance, vector, machNumber, out guardedTicks);
                    }
                    else
                    {
                        guarded = TimedCalculateAndPublish(__instance, vector, machNumber, out guardedTicks);
                        admission = TimedCalculateAndPublish(__instance, vector, machNumber, out admissionTicks);
                    }
                    double strategyError;
                    if (!Equivalent(admission, guarded, out strategyError))
                    { owner.Stop("strategy-output-mismatch"); owner.report.stockFallbacks++; return true; }
                    __state.Candidate = admission;
                    CubeData(__instance) = admission;
                    owner.report.admissionStrategyStopwatchTicks += admissionTicks;
                    owner.report.candidateStopwatchTicks += guardedTicks +
                        (owner.report.measuredPatchGraphStopwatchTicks - graphBefore);
                }
                else
                {
                    __state.Candidate = Calculate(__instance, vector, machNumber);
                    CubeData(__instance) = __state.Candidate;
                }
                if (shadow)
                {
                    __state.Shadow = true;
                    __state.Measured = measured;
                    __state.StockStarted = Stopwatch.GetTimestamp(); return true;
                }
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

        static DragCubeList.CubeData TimedCalculateAndPublish(DragCubeList cubes, Vector3 vector, float mach, out long elapsed)
        {
            long started = Stopwatch.GetTimestamp();
            DragCubeList.CubeData candidate = Calculate(cubes, vector, mach);
            CubeData(cubes) = candidate;
            elapsed = Stopwatch.GetTimestamp() - started;
            return candidate;
        }

        static void Postfix(DragCubeList __instance, CallState __state)
        {
            AeroSetDragProviderQualification owner = instance;
            if (owner == null || !__state.Shadow) return;
            if (__state.Measured) owner.report.stockStopwatchTicks += Stopwatch.GetTimestamp() - __state.StockStarted;
            owner.report.shadowComparisons++;
            double error;
            if (!Equivalent(__state.Candidate, Snapshot(__instance), out error))
            { owner.report.maximumRelativeError = Math.Max(owner.report.maximumRelativeError, error); owner.Stop("complete-output-mismatch"); return; }
            owner.report.maximumRelativeError = Math.Max(owner.report.maximumRelativeError, error);
            owner.report.matchedCompleteOutputs++;
            if (__state.Measured) owner.report.shadowMeasuredComparisons++;
            else owner.report.shadowWarmupComparisons++;
        }

        static DragCubeList.CubeData Calculate(DragCubeList cubes, Vector3 input, float mach)
        {
            if (cubes.None) return Snapshot(cubes);
            Vector3 direction = -input;
            if (cubes.RotateDragVector) direction = cubes.DragVectorRotation * direction;
            double magnitudeSquared = direction.sqrMagnitude;
            if (!Finite(magnitudeSquared) || (magnitudeSquared != 0 && Math.Abs(magnitudeSquared - 1) > 1e-4))
                throw new ArgumentException("Direction must be finite and unit length or zero.");
            PhysicsGlobals.SurfaceCurvesList curves = cubes.SurfaceCurves;
            float[] areas = cubes.AreaOccluded, drags = cubes.WeightedDrag, depths = cubes.WeightedDepth;
            double tail = curves.dragCurveTail.Evaluate(mach), surface = curves.dragCurveSurface.Evaluate(mach);
            double multiplier = curves.dragCurveMultiplier.Evaluate(mach), tip = curves.dragCurveTip.Evaluate(mach);
            double power = cubes.DragCurveCdPower.Evaluate(mach);
            if (!Finite(tail) || !Finite(surface) || !Finite(multiplier) || !Finite(tip) || !Finite(power) || multiplier == 0)
                throw new ArgumentException("Curve samples must be finite and the surface multiplier must be nonzero.");
            double area = 0, areaDrag = 0, section = 0, exposure = 0, dotSum = 0;
            double depth = 0, taper = 0, liftX = 0, liftY = 0, liftZ = 0;
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1;
                double sign = (face & 1) == 0 ? 1 : -1;
                double dot = (axis == 0 ? direction.x : axis == 1 ? direction.y : direction.z) * sign;
                double faceArea = areas[face], drag = drags[face];
                double directionalArea = faceArea * (dot <= 0
                    ? surface + (tail - surface) * Math.Max(0, Math.Min(1, -dot))
                    : surface + (tip - surface) * Math.Max(0, Math.Min(1, dot))) * multiplier;
                area += directionalArea;
                double dragCd = drag < 1 ? Math.Pow(cubes.DragCurveCd.Evaluate((float)drag), power) : drag;
                areaDrag += directionalArea * dragCd;
                section += faceArea * Math.Max(0, Math.Min(1, dot));
                double inverseDrag = drag > .01 && drag < 1 ? 1 / drag : 1;
                exposure += directionalArea / multiplier * inverseDrag;
                if (dot <= 0) continue;
                dotSum += dot;
                double weightedLift = -dot * faceArea * drag * cubes.BodyLiftCurve.liftCurve.Evaluate((float)dot) * sign;
                if (!double.IsNaN(weightedLift))
                {
                    if (axis == 0) liftX += weightedLift;
                    else if (axis == 1) liftY += weightedLift;
                    else liftZ += weightedLift;
                }
                depth += dot * depths[face]; taper += dot * inverseDrag;
            }
            if (dotSum > 0) { depth /= dotSum; taper /= dotSum; }
            double coefficient = area > 0 ? areaDrag / area : 0;
            if (area <= 0) areaDrag = 0;
            var candidate = new DragCubeList.CubeData {
                dragVector = direction, liftForce = new Vector3((float)liftX, (float)liftY, (float)liftZ),
                area = (float)area, areaDrag = (float)areaDrag, depth = (float)depth,
                crossSectionalArea = (float)section, exposedArea = (float)exposure,
                dragCoeff = (float)coefficient, taperDot = (float)taper
            };
            if (!Finite(candidate.dragVector.x) || !Finite(candidate.dragVector.y) || !Finite(candidate.dragVector.z) ||
                !Finite(candidate.liftForce.x) || !Finite(candidate.liftForce.y) || !Finite(candidate.liftForce.z) ||
                !Finite(candidate.area) || !Finite(candidate.areaDrag) || !Finite(candidate.depth) ||
                !Finite(candidate.crossSectionalArea) || !Finite(candidate.exposedArea) ||
                !Finite(candidate.dragCoeff) || !Finite(candidate.taperDot))
                throw new ArithmeticException("SetDrag produced a nonfinite output.");
            return candidate;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
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
        bool TargetOwnedForStep(bool measured)
        {
            float fixedTime = Time.fixedTime;
            if (fixedTime == patchGraphFixedTime) return true;
            report.patchGraphInspections++;
            long started = Stopwatch.GetTimestamp();
            bool owned = TargetStillOwned(target);
            long elapsed = Stopwatch.GetTimestamp() - started;
            report.patchGraphStopwatchTicks += elapsed;
            if (measured)
            {
                report.measuredPatchGraphInspections++;
                report.measuredPatchGraphStopwatchTicks += elapsed;
            }
            if (!owned) return false;
            patchGraphFixedTime = fixedTime; return true;
        }
        void Stop(string reason)
        {
            if (!active) return; active = false; report.reason = reason;
            report.status = reason == "bounded-substitution-limit-reached" ? "complete" : "abstained";
        }
        public void Update() { if (requested && !active && !exported) Finish(); }
        void Finish()
        {
            if (report != null && report.status == "complete" && target != null)
            {
                report.authorityExitAttested = TargetStillOwned(target);
                if (!report.authorityExitAttested)
                { report.status = "abstained"; report.reason = "authority-exit-patch-graph-changed"; }
            }
            Cleanup(); Export();
            if (quitAfterQualification)
                Application.Quit(report != null && report.status == "complete" &&
                    report.cleanupStatus == "removed-owned-patches" ? 0 : 2);
        }
        void Cleanup()
        {
            bool registered = harmony != null;
            bool removed = false;
            try
            {
                if (harmony != null) harmony.UnpatchAll(Owner);
                removed = target == null || !HasOwner(target);
            }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
            finally
            {
                if (report != null) report.RecordCleanup(registered, removed);
                harmony = null;
                if (ReferenceEquals(instance, this)) instance = null;
            }
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
