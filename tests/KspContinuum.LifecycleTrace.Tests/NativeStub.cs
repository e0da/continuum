using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> Objects = new List<Object>();
        public bool Destroyed;
        public int Id;

        public int GetInstanceID()
        {
            return Id;
        }

        public static T[] FindObjectsOfType<T>()
            where T : Object
        {
            return Objects.OfType<T>().Where(x => !x.Destroyed).ToArray();
        }

        public static bool operator ==(Object a, Object b)
        {
            bool an = ReferenceEquals(a, null) || a.Destroyed,
                bn = ReferenceEquals(b, null) || b.Destroyed;
            return an || bn ? an == bn : ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b)
        {
            return !(a == b);
        }

        public override bool Equals(object value)
        {
            return ReferenceEquals(this, value);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }

    public class Rigidbody : Object
    {
        public bool isKinematic;
        public Vector3d worldCenterOfMass,
            position,
            velocity,
            angularVelocity;
        public Quaternion rotation = new Quaternion { w = 1 };
    }

    public struct Quaternion
    {
        public float x,
            y,
            z,
            w;
    }

    public static class Time
    {
        public static int frameCount = 1;
        public static float fixedTime = 2,
            fixedDeltaTime = .02f,
            timeScale = 1;
    }
}

public struct Vector3d
{
    public double x,
        y,
        z;

    public Vector3d(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static Vector3d operator -(Vector3d a, Vector3d b)
    {
        return new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
    }
}

public enum GameScenes
{
    FLIGHT,
    MAINMENU,
}

public static class HighLogic
{
    public static GameScenes LoadedScene = GameScenes.FLIGHT;
    public static bool LoadedSceneIsFlight => LoadedScene == GameScenes.FLIGHT;
}

public static class FlightGlobals
{
    public static bool ready = true;
    public static Vessel ActiveVessel;
}

public static class FlightDriver
{
    public static bool Pause;
}

public static class TimeWarp
{
    public static float CurrentRate = 1;
}

public static class Planetarium
{
    public static double GetUniversalTime()
    {
        return 100;
    }
}

public static class Krakensbane
{
    public static Vector3d Velocity;

    public static Vector3d GetFrameVelocity()
    {
        return Velocity;
    }
}

public class CelestialBody : UnityEngine.Object { }

public class Vessel : UnityEngine.Object
{
    public enum Situations
    {
        ORBITING,
        LANDED,
    }

    public Guid id = Guid.NewGuid();
    public bool loaded = true,
        packed,
        HoldPhysics;
    public List<Part> parts = new List<Part>();
    public CelestialBody mainBody = new CelestialBody { Id = 99 };
    public Situations situation = Situations.ORBITING;
}

public class Part : UnityEngine.Object
{
    public struct ForceHolder
    {
        public Vector3d force,
            pos;
    }

    public uint flightID;
    public Part parent,
        RigidBodyPart;
    public UnityEngine.Rigidbody rb;
    public Vector3d force,
        torque;
    public List<ForceHolder> forces = new List<ForceHolder>();
}

public class Timing3 : UnityEngine.Object
{
    public TimingManager.UpdateAction onFixedUpdate { get; set; }
}

public class TimingFI : UnityEngine.Object
{
    public TimingManager.UpdateAction onFixedUpdate { get; set; }
}

public class Timing5 : UnityEngine.Object
{
    public TimingManager.UpdateAction onFixedUpdate { get; set; }
}

public static class TimingManager
{
    public enum TimingStage
    {
        FashionablyLate,
        FlightIntegrator,
        BetterLateThanNever,
    }

    public delegate void UpdateAction();
    public static Timing3 Current;
    public static TimingFI FI;
    public static Timing5 Late;
    public static bool SkipAdd,
        ThrowAfterAdd;

    public static void FixedUpdateAdd(TimingStage stage, UpdateAction action)
    {
        if (!SkipAdd)
        {
            if (stage == TimingStage.FashionablyLate && Current != null)
                Current.onFixedUpdate += action;
            if (stage == TimingStage.FlightIntegrator && FI != null)
                FI.onFixedUpdate += action;
            if (stage == TimingStage.BetterLateThanNever && Late != null)
                Late.onFixedUpdate += action;
        }
        if (ThrowAfterAdd)
        {
            ThrowAfterAdd = false;
            throw new InvalidOperationException("setup failed");
        }
    }
}

public static class GameEvents
{
    public sealed class Shift
    {
        public Action<Vector3d, Vector3d> Handlers;

        public void Add(Action<Vector3d, Vector3d> action)
        {
            Handlers += action;
        }

        public void Remove(Action<Vector3d, Vector3d> action)
        {
            Handlers -= action;
        }

        public void Fire()
        {
            Handlers?.Invoke(new Vector3d(), new Vector3d());
        }
    }

    public static readonly Shift onFloatingOriginShift = new Shift();
}
