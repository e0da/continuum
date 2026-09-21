using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class ForceObservationReport
    {
        public const int MaximumBatches = 16, MaximumPartsPerBatch = 512, MaximumHoldersPerPart = 64,
            MaximumRetainedParts = 2048, MaximumRetainedHolders = 4096;
        public string schema = "ksp-continuum-part-force-observation/v1";
        public string provider = "continuum-part-census", providerVersion = "1";
        public string sessionId, nativeAssemblyMvid;
        public string status = "not-started", cleanupStatus = "not-registered", detail;
        public string timingStage = "TimingManager.FashionablyLate";
        public string partCensusStatus = "not-observed";
        public string stockAerodynamics = "unavailable-not-observed", gravity = "unavailable-not-observed",
            contactsAndConstraints = "unavailable-not-observed", directRigidbodyWrites = "unavailable-not-observed";
        public string units = "raw KSP part census units; no SI rescaling or total-force reconstruction";
        public string epochScope = "provider-session-local FashionablyLate callbacks; not joinable by counter equality with flight-shadow epochs";
        public int maxBatches = MaximumBatches, maxPartsPerBatch = MaximumPartsPerBatch,
            maxHoldersPerPart = MaximumHoldersPerPart, maxRetainedParts = MaximumRetainedParts, maxRetainedHolders = MaximumRetainedHolders;
        public int completedBatches, retainedParts, retainedHolders, skippedCallbacks;
        public long physicsEpochs, originEvents;
        public ForceObservationBatch[] batches = new ForceObservationBatch[0];
    }

    public sealed class ForceObservationContext
    {
        public readonly string providerSession, vesselId, scene, referenceFrame = "unity-world-at-FashionablyLate", frameKey;
        public readonly int unityFrame, mainBodyInstanceId;
        public readonly long physicsEpoch, topologyGeneration, frameGeneration, floatingOriginEvents;
        public readonly double universalTime, fixedTimeSeconds, stepSeconds;
        public readonly Vec rawKrakensbaneFrameVelocity;
        public ForceObservationContext(string session, string vessel, string scene, string frameKey, int unityFrame, int body,
            long epoch, long topology, long frame, long originEvents, double ut, double fixedTime, double step, Vec frameVelocity)
        {
            Guid parsed;
            if (!Guid.TryParse(session, out parsed) || !Guid.TryParse(vessel, out parsed) || scene != "FLIGHT" ||
                string.IsNullOrEmpty(frameKey) || frameKey.Length > 512 || unityFrame < 0 || epoch < 1 || topology < 1 || frame < 1 || originEvents < 0)
                throw new ArgumentException("Invalid force observation context.");
            ForceObservationValidation.Number(ut); ForceObservationValidation.Number(fixedTime); ForceObservationValidation.Number(step);
            if (step <= 0) throw new ArgumentException("Step must be positive.");
            ForceObservationValidation.Vector(frameVelocity);
            providerSession = session; vesselId = vessel; this.scene = scene; this.frameKey = frameKey;
            this.unityFrame = unityFrame; mainBodyInstanceId = body; physicsEpoch = epoch; topologyGeneration = topology;
            frameGeneration = frame; floatingOriginEvents = originEvents; universalTime = ut; fixedTimeSeconds = fixedTime;
            stepSeconds = step; rawKrakensbaneFrameVelocity = frameVelocity;
        }
        public bool Matches(ForceObservationContext other)
        {
            return other != null && providerSession == other.providerSession && vesselId == other.vesselId &&
                scene == other.scene && referenceFrame == other.referenceFrame && frameKey == other.frameKey &&
                unityFrame == other.unityFrame && mainBodyInstanceId == other.mainBodyInstanceId &&
                physicsEpoch == other.physicsEpoch && topologyGeneration == other.topologyGeneration &&
                frameGeneration == other.frameGeneration && floatingOriginEvents == other.floatingOriginEvents &&
                universalTime == other.universalTime && fixedTimeSeconds == other.fixedTimeSeconds && stepSeconds == other.stepSeconds &&
                rawKrakensbaneFrameVelocity.X == other.rawKrakensbaneFrameVelocity.X &&
                rawKrakensbaneFrameVelocity.Y == other.rawKrakensbaneFrameVelocity.Y &&
                rawKrakensbaneFrameVelocity.Z == other.rawKrakensbaneFrameVelocity.Z;
        }
    }

    public sealed class ForceAtPositionObservation
    {
        public readonly Vec force, worldPosition;
        public readonly Vec? worldLeverArm;
        public ForceAtPositionObservation(Vec force, Vec position, Vec? leverArm)
        {
            ForceObservationValidation.Vector(force); ForceObservationValidation.Vector(position);
            if (leverArm.HasValue) ForceObservationValidation.Vector(leverArm.Value);
            this.force = force; worldPosition = position; worldLeverArm = leverArm;
        }
    }

    public sealed class ForcePartObservation
    {
        public readonly long flightId, parentFlightId;
        public readonly long? rigidBodyPartFlightId;
        public readonly int nativePartInstanceId;
        public readonly int? nativeRigidbodyInstanceId;
        public readonly Vec force, torque;
        public readonly Vec? worldCenterOfMass;
        public readonly ReadOnlyCollection<ForceAtPositionObservation> forces;
        public ForcePartObservation(long id, long parent, int nativeId, int? bodyId, Vec force, Vec torque, Vec? center,
            ForceAtPositionObservation[] holders, long? rigidBodyPartId = null)
        {
            if (id < 1 || id > uint.MaxValue || parent < 0 || parent > uint.MaxValue || parent == id ||
                (rigidBodyPartId.HasValue && (rigidBodyPartId.Value < 1 || rigidBodyPartId.Value > uint.MaxValue)) ||
                bodyId.HasValue != center.HasValue || holders == null || holders.Length > ForceObservationReport.MaximumHoldersPerPart)
                throw new ArgumentException("Invalid part census shape or identity.");
            ForceObservationValidation.Vector(force); ForceObservationValidation.Vector(torque);
            if (center.HasValue) ForceObservationValidation.Vector(center.Value);
            foreach (var holder in holders) if (holder == null || holder.worldLeverArm.HasValue != center.HasValue)
                throw new ArgumentException("Holder availability does not match part center of mass.");
            rigidBodyPartFlightId = rigidBodyPartId;
            flightId = id; parentFlightId = parent; nativePartInstanceId = nativeId; nativeRigidbodyInstanceId = bodyId;
            this.force = force; this.torque = torque; worldCenterOfMass = center;
            forces = Array.AsReadOnly((ForceAtPositionObservation[])holders.Clone());
        }
    }

    public sealed class ForceObservationBatch
    {
        public readonly ForceObservationContext context;
        public readonly ReadOnlyCollection<ForcePartObservation> parts;
        public ForceObservationBatch(ForceObservationContext context, ForcePartObservation[] parts)
        {
            if (context == null || parts == null || parts.Length < 1 || parts.Length > ForceObservationReport.MaximumPartsPerBatch)
                throw new ArgumentException("Invalid force observation batch.");
            var ids = new HashSet<long>(); var nativeIds = new HashSet<int>(); int holders = 0;
            foreach (var part in parts)
            {
                if (part == null || !ids.Add(part.flightId) || !nativeIds.Add(part.nativePartInstanceId))
                    throw new ArgumentException("Part identities must be present and unique.");
                holders += part.forces.Count;
            }
            if (holders > ForceObservationReport.MaximumRetainedHolders) throw new ArgumentException("Too many force holders.");
            foreach (var part in parts)
                if ((part.parentFlightId != 0 && !ids.Contains(part.parentFlightId)) ||
                    (part.rigidBodyPartFlightId.HasValue && !ids.Contains(part.rigidBodyPartFlightId.Value)))
                    throw new ArgumentException("Part ownership refers outside the captured topology.");
            this.context = context; this.parts = Array.AsReadOnly((ForcePartObservation[])parts.Clone());
        }
    }
    internal static class ForceObservationValidation
    {
        internal static void Number(double value)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Nonfinite observation."); }
        internal static void Vector(Vec vector) { Number(vector.X); Number(vector.Y); Number(vector.Z); }
    }
}
