using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class FixedCallbackAttribution : IDisposable, IPlayerLoopBracketObserver
    {
        const string Owner = "continuum.fixed-callback-attribution";
        const int MaximumTargets = 4096;
        static readonly string[] StockProviderMethods = { "ThermoPrecalculate", "Integrate", "UpdateThermodynamics",
            "UpdateOcclusion", "UpdateOcclusionConvection", "UpdateOcclusionSolar", "UpdateOcclusionBody",
            "UpdateAerodynamics", "UpdateConvection" };
        static FixedCallbackAttribution activeOwner;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Dictionary<MethodBase, int> slots = new Dictionary<MethodBase, int>();
        readonly CallbackAttributionAccumulator accumulator = new CallbackAttributionAccumulator(Stopwatch.Frequency);
        long windowStart, activeWindowTicks;
        bool activeWindow, installed, disposed;
        public CallbackAttributionReport Report { get; private set; }

        public FixedCallbackAttribution()
        {
            Report = new CallbackAttributionReport { clockFrequency = Stopwatch.Frequency };
            Report.timerReadFloorTicks = MeasureFloor();
        }

        public void Start()
        {
            if (installed || disposed || activeOwner != null) throw new InvalidOperationException("Fixed callback attribution supports one owner.");
            activeOwner = this;
            try
            {
                MethodInfo prefix = AccessTools.DeclaredMethod(typeof(FixedCallbackAttribution), "Prefix");
                MethodInfo finalizer = AccessTools.DeclaredMethod(typeof(FixedCallbackAttribution), "Finalizer");
                foreach (MethodInfo target in Discover())
                {
                    int slot = accumulator.Register(Row(target)); slots.Add(target, slot);
                    harmony.Patch(target, new HarmonyMethod(prefix) { priority = Priority.First },
                        null, null,
                        new HarmonyMethod(finalizer) { priority = Priority.Last });
                    Report.patchedMethods++;
                }
                if (Report.patchedMethods == 0) throw new InvalidOperationException("No patchable managed FixedUpdate callbacks were found.");
                installed = true; Report.status = "installed"; Report.cleanupStatus = "installed";
            }
            catch (Exception error)
            {
                Report.status = "unavailable"; Report.detail = error.GetType().Name + ": " + error.Message;
                Remove();
            }
        }

        IEnumerable<MethodInfo> Discover()
        {
            var result = new List<MethodInfo>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                if (assembly.IsDynamic || !ReferencesUnity(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error)
                {
                    Report.scanErrors++;
                    types = error.Types.Where(type => type != null).ToArray();
                }
                catch { Report.scanErrors++; continue; }
                foreach (Type type in types.OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    if (type == null) continue;
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        bool fixedCallback = typeof(MonoBehaviour).IsAssignableFrom(type) && method.Name == "FixedUpdate" &&
                            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                        bool stockProvider = (type.FullName == "FlightIntegrator" && Array.IndexOf(StockProviderMethods, method.Name) >= 0) ||
                            (type.FullName == "VesselPrecalculate" && method.Name == "CalculatePhysicsStats");
                        if ((!fixedCallback && !stockProvider) || method.IsAbstract || method.IsGenericMethodDefinition ||
                            method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        result.Add(method);
                        if (result.Count > MaximumTargets) throw new InvalidOperationException("FixedUpdate target bound exceeded.");
                    }
                }
            }
            result.Sort((left, right) => string.CompareOrdinal(Identity(left), Identity(right)));
            Report.discoveredMethods = result.Count;
            return result;
        }

        static bool ReferencesUnity(Assembly assembly)
        {
            string own = assembly.GetName().Name;
            if (own == "Assembly-CSharp" || own.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
            try { return assembly.GetReferencedAssemblies().Any(reference => reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal)); }
            catch { return false; }
        }

        static string Identity(MethodInfo method) => method.Module.Assembly.GetName().Name + "|" + method.DeclaringType.FullName + "|" + method.MetadataToken;
        static CallbackAttributionRow Row(MethodInfo method)
        {
            AssemblyName name = method.Module.Assembly.GetName();
            bool callback = typeof(MonoBehaviour).IsAssignableFrom(method.DeclaringType) && method.Name == "FixedUpdate" && method.GetParameters().Length == 0;
            return new CallbackAttributionRow { category = callback ? "unity-fixed-callback" : "stock-provider-seam",
                assembly = name.Name, assemblyVersion = name.Version == null ? null : name.Version.ToString(),
                assemblyMvid = method.Module.ModuleVersionId.ToString("D"), declaringType = method.DeclaringType.FullName,
                method = method.Name, metadataToken = method.MetadataToken };
        }

        public void Before(string scope, int frame, double time)
        {
            if (!installed || scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            if (activeWindow) { Report.callbackErrors++; return; }
            activeWindow = true; windowStart = Stopwatch.GetTimestamp();
        }
        public void After(string scope, int frame, double time)
        {
            if (!installed || scope != typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) return;
            if (!activeWindow) { Report.callbackErrors++; return; }
            long end = Stopwatch.GetTimestamp(); activeWindow = false;
            if (end < windowStart) Report.callbackErrors++; else activeWindowTicks += end - windowStart;
        }
        public void Fault(string scope, Exception error) { if (scope == typeof(FixedLoop.ScriptRunBehaviourFixedUpdate).FullName) Report.callbackErrors++; }

        public static void Prefix(MethodBase __originalMethod, ref long __state)
        {
            FixedCallbackAttribution owner = activeOwner;
            __state = owner != null && owner.installed && owner.activeWindow ? Stopwatch.GetTimestamp() : 0;
        }
        public static Exception Finalizer(Exception __exception, MethodBase __originalMethod, ref long __state)
        {
            Complete(__originalMethod, ref __state); return __exception;
        }
        static void Complete(MethodBase method, ref long state)
        {
            if (state == 0) return;
            long end = Stopwatch.GetTimestamp(), start = state; state = 0;
            FixedCallbackAttribution owner = activeOwner;
            if (owner == null || end < start) { if (owner != null) owner.Report.callbackErrors++; return; }
            int slot;
            if (!owner.slots.TryGetValue(method, out slot)) { owner.Report.callbackErrors++; return; }
            try { owner.accumulator.Record(slot, end - start); } catch { owner.Report.callbackErrors++; }
        }

        static long MeasureFloor()
        {
            long floor = long.MaxValue;
            for (int i = 0; i < 128; i++) { long start = Stopwatch.GetTimestamp(); long elapsed = Stopwatch.GetTimestamp() - start; if (elapsed < floor) floor = elapsed; }
            return floor;
        }

        void Remove()
        {
            try
            {
                harmony.UnpatchAll(Owner);
                bool remains = slots.Keys.Any(method => { Patches patches = Harmony.GetPatchInfo(method); return patches != null &&
                    patches.Owners.Contains(Owner); });
                Report.cleanupStatus = remains ? "cleanup-error" : "removed-owned-patches";
            }
            catch (Exception error) { Report.cleanupStatus = "cleanup-error"; Report.detail = error.GetType().Name + ": " + error.Message; }
            if (ReferenceEquals(activeOwner, this)) activeOwner = null;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (activeWindow) { activeWindow = false; Report.callbackErrors++; }
            Remove();
            Report.activeWindowTicks = activeWindowTicks;
            Report.callbacks = accumulator.Finish(activeWindowTicks);
            if (Report.status != "unavailable") Report.status = Report.cleanupStatus == "removed-owned-patches" &&
                Report.callbackErrors == 0 && Report.scanErrors == 0 && Report.patchedMethods == Report.discoveredMethods &&
                Report.callbacks.Length > 0 ? "observed" : "invalid";
        }
    }
}
