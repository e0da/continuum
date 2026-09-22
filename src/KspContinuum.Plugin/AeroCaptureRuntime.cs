using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    public sealed class HarmonyAeroPatchRuntime : IAeroCapturePatchRuntime
    {
        readonly Harmony harmony = new Harmony(AeroCaptureRun.Owner);
        readonly MethodInfo update = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateAerodynamics", new[] { typeof(Part) });
        readonly MethodInfo drag = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "ApplyAeroDrag", new[] { typeof(Part), typeof(Rigidbody), typeof(ForceMode) });
        readonly MethodInfo lift = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "ApplyAeroLift", new[] { typeof(Part), typeof(Rigidbody), typeof(ForceMode) });
        public AeroProviderFingerprint Provider { get; private set; }

        public HarmonyAeroPatchRuntime()
        {
            Assembly assembly = typeof(FlightIntegrator).Assembly;
            Provider = new AeroProviderFingerprint("stock-flight-integrator", Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision,
                assembly.GetName().Name, HashFile(assembly.Location), assembly.ManifestModule.ModuleVersionId.ToString("D"));
            if (update == null || drag == null || lift == null) throw new MissingMethodException("Pinned stock aerodynamic seam is unavailable.");
        }

        public AeroPatchProvenance Install(string owner)
        {
            if (owner != AeroCaptureRun.Owner) throw new InvalidOperationException("Unexpected Harmony owner.");
            harmony.Patch(update,
                prefix: Patch("UpdatePrefix"),
                postfix: Patch("UpdatePostfix", Priority.Last),
                finalizer: Patch("UpdateFinalizer", Priority.Last));
            harmony.Patch(drag, prefix: Patch("DragPrefix"));
            harmony.Patch(lift, prefix: Patch("LiftPrefix"));
            return Inspect(owner);
        }

        static HarmonyMethod Patch(string name, int priority = Priority.Normal)
        {
            var patch = new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroCapture), name));
            patch.priority = priority;
            return patch;
        }

        public AeroPatchProvenance Inspect(string owner)
        {
            return new AeroPatchProvenance(Provider, owner, new[] { Target(update), Target(drag), Target(lift) });
        }

        static AeroPatchTarget Target(MethodBase method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            var entries = new List<AeroPatchEntry>();
            if (info != null)
            {
                Add(entries, info.Prefixes, "prefix"); Add(entries, info.Postfixes, "postfix");
                Add(entries, info.Transpilers, "transpiler"); Add(entries, info.Finalizers, "finalizer");
            }
            return new AeroPatchTarget("FlightIntegrator." + method.Name, entries.ToArray());
        }

        static void Add(List<AeroPatchEntry> entries, IEnumerable<Patch> patches, string kind)
        {
            foreach (Patch patch in patches)
            {
                MethodInfo method = patch.PatchMethod;
                entries.Add(new AeroPatchEntry(patch.owner, method.DeclaringType.FullName + "." + method.Name, kind, entries.Count,
                    HashFile(method.Module.Assembly.Location), patch.priority, patch.before, patch.after));
            }
        }

        public AeroCleanupOutcome Remove(string owner)
        {
            harmony.UnpatchAll(owner);
            foreach (MethodBase method in new MethodBase[] { update, drag, lift })
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info != null && HasOwner(info, owner)) return AeroCleanupOutcome.Failed;
            }
            return AeroCleanupOutcome.RemovedOwnedPatches;
        }

        static bool HasOwner(Patches patches, string owner)
        {
            foreach (Patch patch in patches.Prefixes) if (patch.owner == owner) return true;
            foreach (Patch patch in patches.Postfixes) if (patch.owner == owner) return true;
            foreach (Patch patch in patches.Transpilers) if (patch.owner == owner) return true;
            foreach (Patch patch in patches.Finalizers) if (patch.owner == owner) return true;
            return false;
        }

        static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    public static class AeroCapture
    {
        static AeroCaptureSession owner;
        public static void Attach(AeroCaptureSession session)
        {
            if (owner != null) throw new InvalidOperationException("An aerodynamic capture is already attached.");
            owner = session ?? throw new ArgumentNullException(nameof(session));
        }
        public static void Detach(AeroCaptureSession session) { if (ReferenceEquals(owner, session)) owner = null; }
        public static void UpdatePrefix(Part part) { if (owner != null) owner.Begin(part); }
        public static void DragPrefix(FlightIntegrator __instance, Part part, Rigidbody rbPossible, ForceMode mode) { if (owner != null) owner.Publish(__instance, part, rbPossible, false); }
        public static void LiftPrefix(FlightIntegrator __instance, Part part, Rigidbody rbPossible, ForceMode mode) { if (owner != null) owner.Publish(__instance, part, rbPossible, true); }
        public static void UpdatePostfix(Part part) { if (owner != null) owner.End(part); }
        public static Exception UpdateFinalizer(Exception __exception, Part part)
        {
            if (__exception != null && owner != null) owner.Failed();
            return __exception;
        }
    }

    public sealed class AeroCaptureSession : IDisposable
    {
        static readonly AccessTools.FieldRef<FlightIntegrator, float> CacheDragCubeMultiplier =
            AccessTools.FieldRefAccess<FlightIntegrator, float>("cacheDragCubeMultiplier");
        static readonly AccessTools.FieldRef<FlightIntegrator, float> CacheDragMultiplier =
            AccessTools.FieldRefAccess<FlightIntegrator, float>("cacheDragMultiplier");
        readonly AeroCaptureRun run;
        readonly string sessionId = Guid.NewGuid().ToString("D");
        readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        readonly List<AeroBodyPublication> pending = new List<AeroBodyPublication>();
        AeroCaptureContext sampleContext;
        Part current;
        double fixedTime = double.NaN;
        long epoch;
        int ordinal;
        bool disposed;
        public AeroCaptureReport Report { get { return run.Report; } }
        public string FailureDetail { get; private set; }

        public AeroCaptureSession()
        {
            run = new AeroCaptureRun(new HarmonyAeroPatchRuntime());
        }

        public void Start()
        {
            RequireThread(); AeroCapture.Attach(this); run.Start();
            if (run.Report != null) AeroCapture.Detach(this);
        }

        internal void Begin(Part part)
        {
            RequireThread();
            if (part == null || part.vessel == null || !ReferenceEquals(part.vessel, FlightGlobals.ActiveVessel)) return;
            if (!HighLogic.LoadedSceneIsFlight || part.vessel.packed || TimeWarp.CurrentRate != 1 || Time.timeScale != 1) return;
            if (fixedTime != Time.fixedTime)
            {
                Flush();
                if (run.PublishedSamples >= AeroCaptureReport.MaximumSamples) { AeroCapture.Detach(this); return; }
                fixedTime = Time.fixedTime; epoch++; ordinal = 0;
            }
            current = part;
        }

        internal void Publish(FlightIntegrator integrator, Part part, Rigidbody body, bool lift)
        {
            if (!ReferenceEquals(part, current) || body == null || integrator == null) return;
            if (pending.Count >= AeroCaptureReport.MaximumPartsPerSample * 2)
            { run.Invalidate(AeroCaptureReason.BoundsExceeded); AeroCapture.Detach(this); return; }
            try
            {
                AeroPartContext context = CapturePart(part, body, ordinal++);
                Vector3 force, position; bool atCenter;
                if (lift)
                {
                    force = Vector3.ProjectOnPlane(part.transform.rotation * (part.bodyLiftScalar * part.DragCubes.LiftForce), -part.dragVectorDir);
                    position = ApplicationPosition(part, body, part.CoLOffset, out atCenter);
                }
                else
                {
                    force = -part.dragVectorDir * part.dragScalar;
                    position = ApplicationPosition(part, body, part.CoPOffset, out atCenter);
                }
                Vec forceSi = Vector(force * 1000f), positionWorld = Vector(position);
                Vec arm = new Vec(positionWorld.X - context.worldCenterOfMass.X, positionWorld.Y - context.worldCenterOfMass.Y,
                    positionWorld.Z - context.worldCenterOfMass.Z);
                AeroStockDragScalars dragScalars = lift ? null : new AeroStockDragScalars(part.DragCubes.AreaDrag,
                    part.dynamicPressurekPa * 1000d, integrator.pseudoReDragMult,
                    CacheDragCubeMultiplier(integrator), CacheDragMultiplier(integrator), part.dragScalar);
                pending.Add(new AeroBodyPublication(context, lift ? AeroPublicationKind.BodyLift : AeroPublicationKind.BodyDrag,
                    atCenter ? AeroApplicationMode.AtCenterOfMass : AeroApplicationMode.AtWorldPosition,
                    forceSi, positionWorld, Vec.Cross(arm, forceSi), dragScalars));
            }
            catch (Exception error)
            {
                FailureDetail = error.GetType().Name + ": " + error.Message;
                Debug.LogError("[KspContinuum] Aerodynamic capture hook failed: " + error);
                run.Invalidate(AeroCaptureReason.HookFailure); AeroCapture.Detach(this);
            }
        }

        static Vector3 ApplicationPosition(Part part, Rigidbody body, Vector3 offset, out bool atCenter)
        {
            atCenter = body != part.rb && PhysicsGlobals.ApplyDragToNonPhysicsPartsAtParentCoM;
            return atCenter ? body.worldCenterOfMass : part.partTransform.TransformPoint(offset);
        }

        AeroPartContext CapturePart(Part part, Rigidbody body, int callOrdinal)
        {
            Vessel vessel = part.vessel;
            var step = new AeroCaptureContext(sessionId, vessel.id.ToString("D"), HighLogic.LoadedScene + ":" + vessel.mainBody.GetInstanceID(),
                epoch, 1, 1, Time.frameCount, threadId, callOrdinal, Planetarium.GetUniversalTime(), Time.fixedTime, Time.fixedDeltaTime);
            if (sampleContext == null) sampleContext = step;
            var cubes = new List<AeroDragCubeState>();
            foreach (DragCube cube in part.DragCubes.Cubes)
            {
                if (cube.Weight <= 0) continue;
                cubes.Add(new AeroDragCubeState(cube.Name, cube.Weight, Vector(cube.Center), Vector(cube.Size),
                    Doubles(cube.Area), Doubles(cube.Drag), Doubles(cube.Depth), Doubles(cube.DragModifiers)));
            }
            Quaternion attitude = part.transform.rotation;
            DragCubeList dragCubes = part.DragCubes;
            PhysicsGlobals.SurfaceCurvesList surfaceCurves = dragCubes.SurfaceCurves;
            var setDragInputs = new AeroSetDragInputs(Doubles(dragCubes.AreaOccluded), Doubles(dragCubes.WeightedDrag),
                new AeroSurfaceCurveDefinitions(Curve(surfaceCurves.dragCurveTail), Curve(surfaceCurves.dragCurveSurface),
                    Curve(surfaceCurves.dragCurveMultiplier), Curve(surfaceCurves.dragCurveTip)),
                Curve(dragCubes.DragCurveCd), Curve(dragCubes.DragCurveCdPower));
            return new AeroPartContext(step, part.flightID, part.GetInstanceID(), body.GetInstanceID(), body.mass * 1000d,
                FlightGlobals.ActiveVessel.atmDensity, part.staticPressureAtm * 101325d, part.temperature,
                FlightGlobals.ActiveVessel.speedOfSound, part.machNumber, part.aerodynamicArea, part.exposedArea,
                part.ShieldedFromAirstream, Vector(body.worldCenterOfMass), Vector(body.velocity), Vector(part.dragVector),
                Vector(body.angularVelocity), new Vec(attitude.x, attitude.y, attitude.z), attitude.w, cubes.ToArray(), setDragInputs);
        }

        static AeroFloatCurveDefinition Curve(FloatCurve source)
        {
            if (source == null || source.Curve == null) throw new InvalidOperationException("Stock drag curve is unavailable.");
            Keyframe[] sourceKeys = source.Curve.keys;
            if (sourceKeys.Length > AeroFloatCurveDefinition.MaximumKeys) throw new InvalidOperationException("Stock drag curve exceeds capture bound.");
            var keys = new AeroCurveKey[sourceKeys.Length];
            for (int index = 0; index < sourceKeys.Length; index++)
            {
                Keyframe key = sourceKeys[index];
                keys[index] = new AeroCurveKey(key.time, key.value, key.inTangent, key.outTangent,
                    key.inWeight, key.outWeight, (int)key.weightedMode);
            }
            return new AeroFloatCurveDefinition((int)source.Curve.preWrapMode, (int)source.Curve.postWrapMode, keys);
        }

        static double[] Doubles(float[] source)
        {
            var result = new double[source.Length]; for (int i = 0; i < source.Length; i++) result[i] = source[i]; return result;
        }
        static Vec Vector(Vector3 value) { return new Vec(value.x, value.y, value.z); }
        internal void End(Part part) { if (ReferenceEquals(part, current)) current = null; }
        internal void Failed() { run.Invalidate(AeroCaptureReason.HookFailure); AeroCapture.Detach(this); }
        public bool ReachedBound { get { return run.PublishedSamples >= AeroCaptureReport.MaximumSamples; } }
        void Flush()
        {
            if (sampleContext != null && pending.Count != 0) run.TryPublish(new AeroCaptureSample(sampleContext, pending.ToArray()));
            sampleContext = null; pending.Clear();
        }
        void RequireThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId) throw new InvalidOperationException("Aero capture is main-thread owned.");
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; RequireThread(); Flush(); AeroCapture.Detach(this); run.Dispose();
        }
    }
}
