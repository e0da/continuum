using System;
namespace UnityEngine
{
    public static class Time { public static int frameCount; public static float fixedTime, fixedDeltaTime = .02f, time, deltaTime = .016f; }
}
namespace UnityEngine.PlayerLoop
{
    public struct FixedUpdate { public struct PhysicsFixedUpdate {} public struct ScriptRunBehaviourFixedUpdate {} }
    public struct Update { public struct ScriptRunBehaviourUpdate {} }
    public struct PreLateUpdate { public struct ScriptRunBehaviourLateUpdate {} }
}
namespace UnityEngine.LowLevel
{
    public struct PlayerLoopSystem
    {
        public delegate void UpdateFunction();
        public Type type;
        public PlayerLoopSystem[] subSystemList;
        public UpdateFunction updateDelegate;
        public IntPtr updateFunction, loopConditionFunction;
    }
    public static class PlayerLoop
    {
        public static PlayerLoopSystem Current;
        public static int Writes;
        public static bool FailAfterWrite;
        public static PlayerLoopSystem GetCurrentPlayerLoop() { return Current; }
        public static void SetPlayerLoop(PlayerLoopSystem value) { Current = value; Writes++; if (FailAfterWrite) { FailAfterWrite = false; throw new InvalidOperationException("Native setter failure"); } }
    }
}
