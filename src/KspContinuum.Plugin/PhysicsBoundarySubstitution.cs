using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine.LowLevel;
using FixedLoop = UnityEngine.PlayerLoop.FixedUpdate;

namespace KspContinuum
{
    // Owns exactly the native physics node. It never restores a saved whole loop.
    internal sealed class PhysicsBoundarySubstitution : IDisposable
    {
        sealed class CandidatePhysicsNode { }
        static PhysicsBoundarySubstitution owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly PlayerLoopSystem.UpdateFunction candidate;
        PlayerLoopSystem originalTarget;
        bool started, installed, disposed, valid = true;

        public PhysicsBoundarySubstitution(PlayerLoopSystem.UpdateFunction candidate)
        { this.candidate = candidate ?? throw new ArgumentNullException("candidate"); CleanupStatus = "not-installed"; }

        public bool IsValid { get { return valid && installed && !disposed; } }
        public string Detail { get; private set; }
        public string CleanupStatus { get; private set; }

        public void Start()
        {
            RequireThread();
            if (started || disposed) throw new InvalidOperationException("Physics substitution supports one installation.");
            if (owner != null) throw new InvalidOperationException("Another Continuum physics substitution owner is active.");
            started = true; owner = this;
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                if (Count(loop, typeof(FixedLoop.PhysicsFixedUpdate)) != 1 || Count(loop, typeof(CandidatePhysicsNode)) != 0)
                    throw new InvalidOperationException("Expected one unmodified native physics node.");
                originalTarget = Clone(Find(loop, typeof(FixedLoop.PhysicsFixedUpdate)));
                PlayerLoopSystem changed = Replace(loop, false);
                installed = true;
                PlayerLoop.SetPlayerLoop(changed);
                CleanupStatus = "candidate-installed";
                Audit();
                if (!IsValid) throw new InvalidOperationException(Detail);
            }
            catch
            {
                valid = false;
                Cleanup();
                throw;
            }
        }

        public void Audit()
        {
            RequireThread();
            if (!installed || disposed || !valid) return;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Count(loop, typeof(FixedLoop.PhysicsFixedUpdate)) != 0 || Count(loop, typeof(CandidatePhysicsNode)) != 1 ||
                CountCallback(loop, candidate) != 1 || !CandidateOwned(Find(loop, typeof(CandidatePhysicsNode))))
            { valid = false; Detail = "Candidate physics ownership changed."; }
        }

        public void Dispose()
        {
            RequireThread();
            if (disposed) return;
            disposed = true;
            Cleanup();
        }

        void Cleanup()
        {
            bool clean = !installed;
            if (installed)
            {
                try
                {
                    PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                    int candidates = Count(loop, typeof(CandidatePhysicsNode));
                    if (candidates == 1 && Count(loop, typeof(FixedLoop.PhysicsFixedUpdate)) == 0 &&
                        CountCallback(loop, candidate) == 1 && CandidateOwned(Find(loop, typeof(CandidatePhysicsNode))))
                    {
                        PlayerLoop.SetPlayerLoop(Restore(loop));
                        PlayerLoopSystem readback = PlayerLoop.GetCurrentPlayerLoop();
                        clean = Count(readback, typeof(CandidatePhysicsNode)) == 0 &&
                            Count(readback, typeof(FixedLoop.PhysicsFixedUpdate)) == 1 &&
                            SameTree(Find(readback, typeof(FixedLoop.PhysicsFixedUpdate)), originalTarget);
                    }
                    else
                    {
                        clean = false;
                        Detail = "Refused restoration because exclusive candidate ownership was lost.";
                    }
                }
                catch (Exception error) { clean = false; Detail = "Physics restoration failed: " + error.GetType().Name; }
            }
            if (clean && owner == this) owner = null;
            CleanupStatus = clean ? "native-node-restored" : "cleanup-error";
            if (!clean) valid = false;
        }

        PlayerLoopSystem Replace(PlayerLoopSystem node, bool insideFixed)
        {
            if (insideFixed && node.type == typeof(FixedLoop.PhysicsFixedUpdate))
                return new PlayerLoopSystem { type = typeof(CandidatePhysicsNode), updateDelegate = candidate };
            if (node.subSystemList != null)
            {
                var children = new PlayerLoopSystem[node.subSystemList.Length];
                for (int i = 0; i < children.Length; i++) children[i] = Replace(node.subSystemList[i], insideFixed || node.type == typeof(FixedLoop));
                node.subSystemList = children;
            }
            return node;
        }

        PlayerLoopSystem Restore(PlayerLoopSystem node)
        {
            if (node.type == typeof(CandidatePhysicsNode)) return Clone(originalTarget);
            if (node.subSystemList != null)
            {
                var children = new PlayerLoopSystem[node.subSystemList.Length];
                for (int i = 0; i < children.Length; i++) children[i] = Restore(node.subSystemList[i]);
                node.subSystemList = children;
            }
            return node;
        }

        static int Count(PlayerLoopSystem node, Type type)
        { int n = node.type == type ? 1 : 0; if (node.subSystemList != null) foreach (var child in node.subSystemList) n += Count(child, type); return n; }
        static int CountCallback(PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback)
        { int n = 0; if (node.updateDelegate != null) foreach (Delegate entry in node.updateDelegate.GetInvocationList()) if (entry.Equals(callback)) n++; if (node.subSystemList != null) foreach (var child in node.subSystemList) n += CountCallback(child, callback); return n; }
        static PlayerLoopSystem Find(PlayerLoopSystem node, Type type)
        { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) if (Count(child, type) > 0) return Find(child, type); throw new InvalidOperationException("Missing loop node."); }
        static PlayerLoopSystem Clone(PlayerLoopSystem node)
        { if (node.subSystemList != null) { var children = new PlayerLoopSystem[node.subSystemList.Length]; for (int i = 0; i < children.Length; i++) children[i] = Clone(node.subSystemList[i]); node.subSystemList = children; } return node; }
        static bool SameTree(PlayerLoopSystem a, PlayerLoopSystem b)
        { if (a.type != b.type || a.updateDelegate != b.updateDelegate || a.updateFunction != b.updateFunction || a.loopConditionFunction != b.loopConditionFunction) return false; int ac = a.subSystemList == null ? 0 : a.subSystemList.Length, bc = b.subSystemList == null ? 0 : b.subSystemList.Length; if (ac != bc) return false; for (int i = 0; i < ac; i++) if (!SameTree(a.subSystemList[i], b.subSystemList[i])) return false; return true; }
        bool CandidateOwned(PlayerLoopSystem node)
        { return node.type == typeof(CandidatePhysicsNode) && node.updateDelegate == candidate && node.updateFunction == IntPtr.Zero &&
            node.loopConditionFunction == IntPtr.Zero && (node.subSystemList == null || node.subSystemList.Length == 0); }
        void RequireThread() { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Physics substitution changed threads."); }
    }
}
