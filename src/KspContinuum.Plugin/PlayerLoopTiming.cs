using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class PlayerLoopTiming : IDisposable
    {
        sealed class BeforeHook { }
        sealed class AfterHook { }
        sealed class Scope
        {
            public Type Type;
            public PlayerLoopSystem Target;
            public PlayerLoopSystem.UpdateFunction Before, After;
            public LoopTimingBuffer Buffer;
        }
        readonly Scope[] scopes;
        PlayerLoopSystem originalParent;
        List<PlayerLoopSystem> originalPath;
        bool active, installed, disposed, valid = true, started;
        public LoopTimingReport Report { get; private set; }

        public PlayerLoopTiming()
        {
            Report = new LoopTimingReport { status = "unavailable", integrityStatus = "not-installed", cleanupStatus = "not-installed",
                clockFrequency = Stopwatch.Frequency, timerReadFloorTicks = long.MaxValue };
            for (int i = 0; i < 128; i++)
            {
                long before = Stopwatch.GetTimestamp();
                Report.timerReadFloorTicks = Math.Min(Report.timerReadFloorTicks, Stopwatch.GetTimestamp() - before);
            }
            scopes = new[] { MakeScope(typeof(FixedLoop.PhysicsFixedUpdate)), MakeScope(typeof(FixedLoop.ScriptRunBehaviourFixedUpdate)) };
        }

        Scope MakeScope(Type type)
        {
            var scope = new Scope { Type = type, Buffer = new LoopTimingBuffer(type.FullName, 4096, Stopwatch.Frequency) };
            scope.Before = () => {
                if (!active) return;
                try
                {
                    int frame = UnityEngine.Time.frameCount;
                    double time = UnityEngine.Time.fixedTime, delta = UnityEngine.Time.fixedDeltaTime;
                    scope.Buffer.Begin(Stopwatch.GetTimestamp(), frame, time, delta);
                }
                catch (Exception) { scope.Buffer.Fault(); Invalidate("Timing callback failed."); }
            };
            scope.After = () => {
                if (!active) return;
                try { long end = Stopwatch.GetTimestamp(); scope.Buffer.End(end, UnityEngine.Time.frameCount); }
                catch (Exception) { scope.Buffer.Fault(); Invalidate("Timing callback failed."); }
            };
            return scope;
        }

        public void Start()
        {
            if (started || disposed) throw new InvalidOperationException("PlayerLoop capture supports one installation.");
            started = true;
            try
            {
                var current = PlayerLoop.GetCurrentPlayerLoop();
                if (Count(current, n => n.type == typeof(FixedLoop)) != 1)
                    throw new InvalidOperationException("Expected one FixedUpdate parent.");
                PlayerLoopSystem parent = Find(current, typeof(FixedLoop));
                originalParent = Clone(parent);
                originalPath = Path(Clone(current));
                foreach (Scope scope in scopes)
                {
                    if (Count(current, n => n.type == scope.Type) != 1 || DirectCount(parent, scope.Type) != 1)
                        throw new InvalidOperationException("Expected one direct native target under FixedUpdate.");
                    scope.Target = Clone(Find(parent, scope.Type));
                }
                var modified = Insert(current);
                // Set may fail after partial native work; cleanup must inspect the latest tree in either case.
                installed = true;
                PlayerLoop.SetPlayerLoop(modified);
                active = true;
                Report.status = "no-samples";
                Audit();
            }
            catch (Exception error)
            {
                valid = false; active = false;
                Report.detail = "Installation unavailable: " + error.GetType().Name + ". " + error.Message;
                Report.status = "unavailable";
            }
        }

        public void Audit()
        {
            if (!installed || disposed) return;
            try
            {
                var current = PlayerLoop.GetCurrentPlayerLoop();
                if (Count(current, n => n.type == typeof(FixedLoop)) != 1) { Invalidate("FixedUpdate parent changed."); return; }
                if (!PathIntact(current)) { Invalidate("FixedUpdate ancestor path or order changed."); return; }
                var parent = Find(current, typeof(FixedLoop));
                if (!PreservesSiblings(parent)) { Invalidate("FixedUpdate native order or parent changed."); return; }
                foreach (Scope scope in scopes)
                {
                    if (Count(current, n => n.type == scope.Type) != 1 ||
                        CountCallbacks(current, scope.Before) != 1 || CountCallbacks(current, scope.After) != 1 ||
                        !Intact(parent, scope))
                    { Invalidate("Native target or owned bracket changed, moved, duplicated or removed."); return; }
                }
                if (valid) Report.integrityStatus = "verified-at-boundaries";
            }
            catch (Exception error) { Invalidate("Loop audit failed: " + error.GetType().Name); }
        }

        void Invalidate(string detail)
        {
            valid = false; active = false;
            Report.integrityStatus = "invalidated";
            Report.detail = detail;
        }

        static List<PlayerLoopSystem> Path(PlayerLoopSystem node)
        {
            var result = new List<PlayerLoopSystem> { node };
            if (node.type == typeof(FixedLoop)) return result;
            if (node.subSystemList != null) foreach (var child in node.subSystemList)
                if (Count(child, n => n.type == typeof(FixedLoop)) > 0) { result.AddRange(Path(child)); return result; }
            throw new InvalidOperationException("Missing FixedUpdate path.");
        }
        bool PathIntact(PlayerLoopSystem current)
        {
            var path = Path(current);
            if (path.Count != originalPath.Count) return false;
            for (int i = 0; i < path.Count; i++)
            {
                if (!SameHeader(path[i], originalPath[i])) return false;
                if (i == path.Count - 1) continue;
                int matched = 0;
                var original = originalPath[i].subSystemList;
                if (path[i].subSystemList != null) foreach (var child in path[i].subSystemList)
                    if (matched < original.Length && SameHeader(child, original[matched])) matched++;
                if (matched != original.Length) return false;
            }
            return true;
        }
        static bool SameHeader(PlayerLoopSystem a, PlayerLoopSystem b)
        {
            return a.type == b.type && a.updateDelegate == b.updateDelegate && a.updateFunction == b.updateFunction &&
                a.loopConditionFunction == b.loopConditionFunction;
        }

        bool PreservesSiblings(PlayerLoopSystem parent)
        {
            if (parent.updateFunction != originalParent.updateFunction || parent.loopConditionFunction != originalParent.loopConditionFunction ||
                parent.updateDelegate != originalParent.updateDelegate) return false;
            int matched = 0;
            var original = originalParent.subSystemList;
            if (parent.subSystemList != null) foreach (var child in parent.subSystemList)
                if (matched < original.Length && Equal(child, original[matched])) matched++;
            return matched == original.Length;
        }

        bool Intact(PlayerLoopSystem parent, Scope scope)
        {
            var children = parent.subSystemList;
            if (children == null) return false;
            for (int i = 1; i + 1 < children.Length; i++)
                if (children[i].type == scope.Type)
                    return Equal(children[i], scope.Target) && Hook(children[i - 1], scope.Before, typeof(BeforeHook)) &&
                        Hook(children[i + 1], scope.After, typeof(AfterHook));
            return false;
        }
        static bool Hook(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback, Type type)
        {
            return node.type == type && node.updateDelegate == callback && node.updateFunction == IntPtr.Zero &&
                node.loopConditionFunction == IntPtr.Zero && (node.subSystemList == null || node.subSystemList.Length == 0);
        }
        PlayerLoopSystem Insert(PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return node;
            var children = new List<PlayerLoopSystem>();
            foreach (var child in node.subSystemList)
            {
                Scope selected = null;
                if (node.type == typeof(FixedLoop)) foreach (var scope in scopes) if (child.type == scope.Type) selected = scope;
                if (selected != null) children.Add(new PlayerLoopSystem { type = typeof(BeforeHook), updateDelegate = selected.Before });
                children.Add(Insert(child));
                if (selected != null) children.Add(new PlayerLoopSystem { type = typeof(AfterHook), updateDelegate = selected.After });
            }
            node.subSystemList = children.ToArray(); return node;
        }

        public void Dispose()
        {
            if (disposed) return;
            Audit(); active = false; disposed = true;
            if (installed)
            {
                try
                {
                    int removed = 0;
                    var latest = Strip(PlayerLoop.GetCurrentPlayerLoop(), ref removed);
                    if (removed != 0) PlayerLoop.SetPlayerLoop(latest);
                    var verified = PlayerLoop.GetCurrentPlayerLoop();
                    foreach (var scope in scopes)
                        if (CountCallbacks(verified, scope.Before) != 0 || CountCallbacks(verified, scope.After) != 0)
                            throw new InvalidOperationException("Owned callbacks remain.");
                    Report.cleanupStatus = "removed-owned-hooks";
                }
                catch (Exception error) { Invalidate("Loop cleanup failed: " + error.GetType().Name); Report.cleanupStatus = "cleanup-error"; }
            }
            Report.scopes = new LoopTimingScope[scopes.Length];
            bool observed = false, invalid = !valid;
            for (int i = 0; i < scopes.Length; i++)
            {
                Report.scopes[i] = scopes[i].Buffer.Finish(valid && installed);
                observed |= Report.scopes[i].status == "observed";
                invalid |= Report.scopes[i].status == "invalid";
            }
            if (Report.status != "unavailable") Report.status = invalid ? "invalid" : observed ? "observed" : "no-samples";
            if (invalid && installed) Report.integrityStatus = "invalidated";
            if (invalid) foreach (var scope in Report.scopes) { scope.status = "invalid"; scope.milliseconds = null; }
        }

        PlayerLoopSystem Strip(PlayerLoopSystem node, ref int removed)
        {
            if (node.updateDelegate != null)
                foreach (PlayerLoopSystem.UpdateFunction callback in node.updateDelegate.GetInvocationList())
                    if (Owned(callback)) { node.updateDelegate -= callback; removed++; }
            if (node.subSystemList != null)
            {
                var children = new List<PlayerLoopSystem>();
                foreach (var child in node.subSystemList)
                {
                    bool ours = child.updateDelegate != null && HasOwned(child.updateDelegate);
                    var stripped = Strip(child, ref removed);
                    // If another owner attached state/children to our node, retain that node and its foreign work.
                    bool empty = stripped.updateDelegate == null && stripped.updateFunction == IntPtr.Zero &&
                        stripped.loopConditionFunction == IntPtr.Zero && (stripped.subSystemList == null || stripped.subSystemList.Length == 0);
                    if (!(ours && empty && (child.type == typeof(BeforeHook) || child.type == typeof(AfterHook)))) children.Add(stripped);
                }
                node.subSystemList = children.ToArray();
            }
            return node;
        }
        bool Owned(PlayerLoopSystem.UpdateFunction callback)
        {
            foreach (var scope in scopes) if (callback == scope.Before || callback == scope.After) return true;
            return false;
        }
        bool HasOwned(PlayerLoopSystem.UpdateFunction callbacks)
        {
            foreach (PlayerLoopSystem.UpdateFunction callback in callbacks.GetInvocationList()) if (Owned(callback)) return true;
            return false;
        }
        static int CountCallbacks(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback)
        {
            int result = 0;
            if (node.updateDelegate != null) foreach (Delegate entry in node.updateDelegate.GetInvocationList()) if (entry.Equals(callback)) result++;
            if (node.subSystemList != null) foreach (var child in node.subSystemList) result += CountCallbacks(child, callback);
            return result;
        }
        static int Count(PlayerLoopSystem node, Predicate<PlayerLoopSystem> predicate)
        {
            int result = predicate(node) ? 1 : 0;
            if (node.subSystemList != null) foreach (var child in node.subSystemList) result += Count(child, predicate);
            return result;
        }
        static int DirectCount(PlayerLoopSystem node, Type type)
        {
            int count = 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (child.type == type) count++;
            return count;
        }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type)
        {
            if (node.type == type) return node;
            if (node.subSystemList != null) foreach (var child in node.subSystemList)
                if (Count(child, n => n.type == type) > 0) return Find(child, type);
            throw new InvalidOperationException("Missing loop node.");
        }
        static PlayerLoopSystem Clone(PlayerLoopSystem node)
        {
            if (node.subSystemList != null)
            {
                var children = new PlayerLoopSystem[node.subSystemList.Length];
                for (int i = 0; i < children.Length; i++) children[i] = Clone(node.subSystemList[i]);
                node.subSystemList = children;
            }
            return node;
        }
        static bool Equal(PlayerLoopSystem a, PlayerLoopSystem b)
        {
            if (a.type != b.type || a.updateDelegate != b.updateDelegate || a.updateFunction != b.updateFunction || a.loopConditionFunction != b.loopConditionFunction) return false;
            int ac = a.subSystemList == null ? 0 : a.subSystemList.Length, bc = b.subSystemList == null ? 0 : b.subSystemList.Length;
            if (ac != bc) return false;
            for (int i = 0; i < ac; i++) if (!Equal(a.subSystemList[i], b.subSystemList[i])) return false;
            return true;
        }
    }
}
