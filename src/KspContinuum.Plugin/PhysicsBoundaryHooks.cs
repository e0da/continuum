using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    internal sealed class PhysicsBoundaryHooks : IDisposable
    {
        sealed class BeforePhysicsHook { }
        sealed class AfterPhysicsHook { }
        static PhysicsBoundaryHooks owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly PlayerLoopSystem.UpdateFunction before;
        readonly PlayerLoopSystem.UpdateFunction after;
        PlayerLoopSystem original;
        PlayerLoopSystem target;
        bool started, installed, disposed, valid = true;
        public bool IsValid { get { return valid && installed && !disposed; } }
        public string Detail { get; private set; }
        public string CleanupStatus { get; private set; }

        public PhysicsBoundaryHooks(PlayerLoopSystem.UpdateFunction before, PlayerLoopSystem.UpdateFunction after)
        {
            if (before == null || after == null) throw new ArgumentNullException(before == null ? "before" : "after");
            this.before = before; this.after = after; CleanupStatus = "not-installed";
        }

        public void Start()
        {
            RequireThread();
            if (started || disposed) throw new InvalidOperationException("Physics boundary hooks support one installation.");
            if (owner != null) throw new InvalidOperationException("Another Continuum physics boundary owner is active.");
            started = true; owner = this;
            try
            {
                original = Clone(PlayerLoop.GetCurrentPlayerLoop());
                if (Count(original, n => n.type == typeof(FixedLoop.PhysicsFixedUpdate)) != 1)
                    throw new InvalidOperationException("Expected one native physics target.");
                if (FindDirectParent(original, typeof(FixedLoop.PhysicsFixedUpdate)).type != typeof(FixedLoop))
                    throw new InvalidOperationException("Native physics target has an unexpected parent.");
                var modified = Insert(original); target = Clone(Find(modified, typeof(FixedLoop.PhysicsFixedUpdate)));
                installed = true; PlayerLoop.SetPlayerLoop(modified); CleanupStatus = "installed"; Audit();
                if (!IsValid) throw new InvalidOperationException(Detail);
            }
            catch
            {
                valid = false; Cleanup(); throw;
            }
        }

        public void Audit()
        {
            RequireThread();
            if (!installed || disposed || !valid) return;
            try
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                if (!PreservesOriginal(loop, original) || CountCallbacks(loop, before) != 1
                    || CountCallbacks(loop, after) != 1 || !Intact(loop))
                    Invalidate("Native physics target or owned bracket changed, moved, duplicated or removed.");
            }
            catch (Exception error) { Invalidate("Loop audit failed: " + error.GetType().Name); }
        }

        void Invalidate(string detail) { valid = false; Detail = detail; }

        public void Dispose()
        {
            if (disposed) return; RequireThread(); if (installed && valid) Audit(); Cleanup(); disposed = true;
        }

        void Cleanup()
        {
            bool clean = true;
            if (installed)
            {
                try
                {
                    int removed = 0; var latest = Strip(PlayerLoop.GetCurrentPlayerLoop(), ref removed);
                    if (removed > 0) PlayerLoop.SetPlayerLoop(latest);
                    var verified = PlayerLoop.GetCurrentPlayerLoop();
                    clean = CountCallbacks(verified, before) == 0 && CountCallbacks(verified, after) == 0;
                }
                catch (Exception error) { clean = false; Detail = "Loop cleanup failed: " + error.GetType().Name; }
            }
            if (clean && owner == this) owner = null;
            CleanupStatus = clean ? "removed-owned-hooks" : "cleanup-error";
            if (!clean) valid = false;
        }

        bool Intact(PlayerLoopSystem root)
        {
            var parent = FindDirectParent(root, typeof(FixedLoop.PhysicsFixedUpdate)); var children = parent.subSystemList;
            for (int i = 1; i + 1 < children.Length; i++) if (children[i].type == typeof(FixedLoop.PhysicsFixedUpdate))
                return PreservesOriginal(children[i], target) && Hook(children[i - 1], before, typeof(BeforePhysicsHook))
                    && Hook(children[i + 1], after, typeof(AfterPhysicsHook));
            return false;
        }
        static bool Hook(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback, Type type) { return node.type == type && node.updateDelegate == callback && node.updateFunction == IntPtr.Zero && node.loopConditionFunction == IntPtr.Zero && (node.subSystemList == null || node.subSystemList.Length == 0); }
        PlayerLoopSystem Insert(PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return node; var children = new List<PlayerLoopSystem>();
            foreach (var raw in node.subSystemList) { var child = Insert(raw); if (node.type == typeof(FixedLoop) && raw.type == typeof(FixedLoop.PhysicsFixedUpdate)) children.Add(new PlayerLoopSystem { type = typeof(BeforePhysicsHook), updateDelegate = before }); children.Add(child); if (node.type == typeof(FixedLoop) && raw.type == typeof(FixedLoop.PhysicsFixedUpdate)) children.Add(new PlayerLoopSystem { type = typeof(AfterPhysicsHook), updateDelegate = after }); }
            node.subSystemList = children.ToArray(); return node;
        }
        PlayerLoopSystem Strip(PlayerLoopSystem node, ref int removed)
        {
            if (node.updateDelegate != null) foreach (PlayerLoopSystem.UpdateFunction callback in node.updateDelegate.GetInvocationList())
                if (callback == before || callback == after) { node.updateDelegate -= callback; removed++; }
            if (node.subSystemList != null)
            {
                var children = new List<PlayerLoopSystem>();
                foreach (var child in node.subSystemList)
                {
                    var stripped = Strip(child, ref removed);
                    bool empty = (stripped.type == typeof(BeforePhysicsHook) || stripped.type == typeof(AfterPhysicsHook))
                        && stripped.updateDelegate == null && stripped.updateFunction == IntPtr.Zero
                        && stripped.loopConditionFunction == IntPtr.Zero
                        && (stripped.subSystemList == null || stripped.subSystemList.Length == 0);
                    if (!empty) children.Add(stripped);
                }
                node.subSystemList = children.ToArray();
            }
            return node;
        }
        static bool PreservesOriginal(PlayerLoopSystem current, PlayerLoopSystem expected) { if (!SameHeader(current, expected)) return false; var wanted = expected.subSystemList; if (wanted == null || wanted.Length == 0) return true; var actual = current.subSystemList; if (actual == null) return false; int matched = 0; for (int i = 0; i < actual.Length && matched < wanted.Length; i++) if (actual[i].type == wanted[matched].type && PreservesOriginal(actual[i], wanted[matched])) matched++; return matched == wanted.Length; }
        static bool SameHeader(PlayerLoopSystem a, PlayerLoopSystem b) { return a.type == b.type && a.updateDelegate == b.updateDelegate && a.updateFunction == b.updateFunction && a.loopConditionFunction == b.loopConditionFunction; }
        static int CountCallbacks(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback) { int result = 0; if (node.updateDelegate != null) foreach (Delegate entry in node.updateDelegate.GetInvocationList()) if (entry.Equals(callback)) result++; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += CountCallbacks(child, callback); return result; }
        static int Count(PlayerLoopSystem node, Predicate<PlayerLoopSystem> predicate) { int result = predicate(node) ? 1 : 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) result += Count(child, predicate); return result; }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return Find(child, type); throw new InvalidOperationException("Missing loop node."); }
        static PlayerLoopSystem FindDirectParent(PlayerLoopSystem node, Type type) { if (node.subSystemList != null) { foreach (var child in node.subSystemList) if (child.type == type) return node; foreach (var child in node.subSystemList) if (Count(child, n => n.type == type) > 0) return FindDirectParent(child, type); } throw new InvalidOperationException("Missing loop parent."); }
        static PlayerLoopSystem Clone(PlayerLoopSystem node) { if (node.subSystemList != null) { var children = new PlayerLoopSystem[node.subSystemList.Length]; for (int i = 0; i < children.Length; i++) children[i] = Clone(node.subSystemList[i]); node.subSystemList = children; } return node; }
        void RequireThread() { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Physics boundary hooks changed threads."); }
    }
}
