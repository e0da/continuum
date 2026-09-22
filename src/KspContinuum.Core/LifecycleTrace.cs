using System;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public sealed class LifecycleTraceReport
    {
        public const int MaximumHostFixedObservations = 120,
            MinimumStableArmingHostFixedObservations = 3,
            MaximumPartsPerEvent = 512,
            MaximumEvents = 8192,
            MaximumRetainedParts = 8192,
            MaximumRetainedHolders = 16384;
        public const double MaximumWallSeconds = 30;
        public const int MaximumEncodedEventBytes = 3 * 1024 * 1024;
        public string schema = "ksp-continuum-lifecycle-trace/v1";
        public string sessionId,
            nativeAssemblyMvid,
            unityAssemblyMvid,
            pluginAssemblyMvid;
        public string registrationStatus = "not-registered";
        public string evidence = "native-adapter-observation",
            status = "not-started",
            detail,
            cleanupStatus = "not-registered";
        public string forceScope =
            "raw part component census; gravity, stock aerodynamics, contact impulses and direct Rigidbody writes not reconstructed";
        public string orderingScope =
            "observed session sequence; host FixedUpdate counter is not a certified physics-step identity; terminal cycle may be partial";
        public bool physicsWrites = false,
            installedOrderQualified = false;
        public int skippedArmingCallbacks;
        public int stableArmingHostFixedObservations;
        public int requiredStableArmingHostFixedObservations =
            MinimumStableArmingHostFixedObservations;
        public int retainedParts,
            retainedHolders,
            completedEvents,
            hostFixedObservations,
            encodedEventBytes;
        public int maxEncodedEventBytes = MaximumEncodedEventBytes;
        public double elapsedWallSeconds;
        public long topologyGeneration,
            frameGeneration,
            originEvents;
        public LifecycleTraceEvent[] events = new LifecycleTraceEvent[0];
    }

    public sealed class LifecycleTraceContext
    {
        public readonly string sessionId,
            stage,
            vesselId,
            scene,
            frameKey;
        public readonly long sequence,
            topologyGeneration,
            frameGeneration,
            originEvents;
        public readonly int threadId,
            unityFrame,
            hostFixedObservations,
            mainBodyInstanceId;
        public readonly double wallSeconds,
            universalTime,
            fixedTimeSeconds,
            stepSeconds,
            warpRate,
            timeScale;
        public readonly bool loaded,
            packed,
            holdPhysics,
            paused,
            eligible;
        public readonly Vec frameVelocity,
            originTranslation;

        public LifecycleTraceContext(
            string session,
            string stage,
            string vessel,
            string scene,
            string frameKey,
            long sequence,
            long topology,
            long frame,
            long origin,
            int thread,
            int unityFrame,
            int hostFixed,
            int mainBody,
            double wall,
            double ut,
            double fixedTime,
            double step,
            double warp,
            double scale,
            bool loaded,
            bool packed,
            bool held,
            bool paused,
            bool eligible,
            Vec velocity,
            Vec originTranslation
        )
        {
            if (
                string.IsNullOrEmpty(session)
                || string.IsNullOrEmpty(stage)
                || string.IsNullOrEmpty(scene)
                || string.IsNullOrEmpty(frameKey)
                || sequence < 1
                || topology < 1
                || frame < 1
                || origin < 0
                || thread < 1
                || unityFrame < 0
                || hostFixed < 0
            )
                throw new ArgumentException("Invalid lifecycle context.");
            foreach (double value in new[] { wall, ut, fixedTime, step, warp, scale })
                ForceObservationValidation.Number(value);
            if (wall < 0 || step <= 0 || warp < 0 || scale < 0)
                throw new ArgumentException("Invalid lifecycle clocks.");
            ForceObservationValidation.Vector(velocity);
            ForceObservationValidation.Vector(originTranslation);
            sessionId = session;
            this.stage = stage;
            vesselId = vessel;
            this.scene = scene;
            this.frameKey = frameKey;
            this.sequence = sequence;
            topologyGeneration = topology;
            frameGeneration = frame;
            originEvents = origin;
            threadId = thread;
            this.unityFrame = unityFrame;
            hostFixedObservations = hostFixed;
            mainBodyInstanceId = mainBody;
            wallSeconds = wall;
            universalTime = ut;
            fixedTimeSeconds = fixedTime;
            stepSeconds = step;
            warpRate = warp;
            timeScale = scale;
            this.loaded = loaded;
            this.packed = packed;
            holdPhysics = held;
            this.paused = paused;
            this.eligible = eligible;
            frameVelocity = velocity;
            this.originTranslation = originTranslation;
        }
    }

    public sealed class LifecyclePartSample
    {
        public readonly ForcePartObservation census;
        public readonly Vec? position,
            velocity,
            angularVelocity,
            rotationXYZ;
        public readonly double? rotationW;

        public LifecyclePartSample(
            ForcePartObservation census,
            Vec? position,
            Vec? velocity,
            Vec? angular,
            Vec? rotationXYZ,
            double? rotationW
        )
        {
            if (
                census == null
                || census.nativeRigidbodyInstanceId.HasValue != position.HasValue
                || position.HasValue != velocity.HasValue
                || position.HasValue != angular.HasValue
                || position.HasValue != rotationXYZ.HasValue
                || position.HasValue != rotationW.HasValue
            )
                throw new ArgumentException("Inconsistent pose availability.");
            foreach (var value in new[] { position, velocity, angular, rotationXYZ })
                if (value.HasValue)
                    ForceObservationValidation.Vector(value.Value);
            if (rotationW.HasValue)
                ForceObservationValidation.Number(rotationW.Value);
            this.census = census;
            this.position = position;
            this.velocity = velocity;
            angularVelocity = angular;
            this.rotationXYZ = rotationXYZ;
            this.rotationW = rotationW;
        }
    }

    public sealed class LifecycleTraceEvent
    {
        public readonly LifecycleTraceContext context;
        public readonly string sampleStatus;
        public readonly ReadOnlyCollection<LifecyclePartSample> parts;

        public LifecycleTraceEvent(
            LifecycleTraceContext context,
            string status,
            LifecyclePartSample[] parts
        )
        {
            if (
                context == null
                || parts == null
                || parts.Length > LifecycleTraceReport.MaximumPartsPerEvent
                || string.IsNullOrEmpty(status)
            )
                throw new ArgumentException("Invalid event.");
            foreach (var part in parts)
                if (part == null)
                    throw new ArgumentException("Null sample.");
            this.context = context;
            sampleStatus = status;
            this.parts = Array.AsReadOnly((LifecyclePartSample[])parts.Clone());
        }
    }
}
