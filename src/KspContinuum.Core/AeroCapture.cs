using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum AeroPublicationKind { BodyDrag, BodyLift }
    public enum AeroApplicationMode { AtCenterOfMass, AtWorldPosition }
    public enum AeroCaptureDisposition { Valid, Abstained, Invalid }
    public enum AeroCaptureReason
    {
        None, UnsupportedProvider, ProviderFingerprintMismatch, PatchGraphMismatch,
        UnsupportedScene, PackedVessel, MissingRigidbody, UnsupportedRegime,
        BoundsExceeded, NonfiniteInput, ContextMismatch, HookFailure
    }
    public enum AeroCleanupOutcome { NotRegistered, RemovedOwnedPatches, OwnerDestroyed, Failed }

    public sealed class AeroProviderFingerprint
    {
        public readonly string provider, providerVersion, assemblyName, assemblySha256, assemblyMvid;
        public AeroProviderFingerprint(string provider, string version, string assembly, string sha256, string mvid)
        {
            AeroCaptureValidation.Text(provider, 128); AeroCaptureValidation.Text(version, 128);
            AeroCaptureValidation.Text(assembly, 256); AeroCaptureValidation.Sha256(sha256);
            Guid parsed; if (!Guid.TryParse(mvid, out parsed)) throw new ArgumentException("Invalid provider MVID.");
            this.provider = provider; providerVersion = version; assemblyName = assembly;
            assemblySha256 = sha256.ToLowerInvariant(); assemblyMvid = parsed.ToString("D");
        }
    }

    public sealed class AeroPatchProvenance
    {
        public const int MaximumPatches = 64;
        public readonly AeroProviderFingerprint provider;
        public readonly string targetMethod;
        public readonly ReadOnlyCollection<AeroPatchEntry> orderedPatches;
        public AeroPatchProvenance(AeroProviderFingerprint provider, string target, AeroPatchEntry[] patches)
        {
            if (provider == null || patches == null || patches.Length > MaximumPatches)
                throw new ArgumentException("Invalid patch provenance.");
            AeroCaptureValidation.Text(target, 512);
            var identities = new HashSet<string>();
            for (int index = 0; index < patches.Length; index++)
            {
                var patch = patches[index];
                if (patch == null || patch.executionIndex != index || !identities.Add(patch.owner + "\n" + patch.patchMethod))
                    throw new ArgumentException("Patch identities must be present and unique.");
            }
            this.provider = provider; targetMethod = target;
            orderedPatches = Array.AsReadOnly((AeroPatchEntry[])patches.Clone());
        }
    }

    public sealed class AeroPatchEntry
    {
        public readonly string owner, patchMethod, patchKind, assemblySha256;
        public readonly int executionIndex;
        public AeroPatchEntry(string owner, string method, string kind, int index, string sha256)
        {
            AeroCaptureValidation.Text(owner, 256); AeroCaptureValidation.Text(method, 512);
            if (kind != "prefix" && kind != "postfix" && kind != "transpiler" && kind != "finalizer")
                throw new ArgumentException("Unknown patch kind.");
            if (index < 0 || index >= AeroPatchProvenance.MaximumPatches) throw new ArgumentException("Invalid patch order.");
            AeroCaptureValidation.Sha256(sha256);
            this.owner = owner; patchMethod = method; patchKind = kind; executionIndex = index;
            assemblySha256 = sha256.ToLowerInvariant();
        }
    }

    public sealed class AeroCaptureContext
    {
        public readonly string sessionId, vesselId, frameKey, referenceFrame = "unity-world";
        public readonly long physicsEpoch, topologyGeneration, frameGeneration;
        public readonly int unityFrame, mainThreadId, callOrdinal;
        public readonly double universalTime, fixedTimeSeconds, stepSeconds;
        public AeroCaptureContext(string session, string vessel, string frameKey, long epoch, long topology, long frame,
            int unityFrame, int thread, int ordinal, double ut, double fixedTime, double step)
        {
            Guid parsed;
            if (!Guid.TryParse(session, out parsed) || !Guid.TryParse(vessel, out parsed))
                throw new ArgumentException("Invalid capture identity.");
            AeroCaptureValidation.Text(frameKey, 512);
            if (epoch < 1 || topology < 1 || frame < 1 || unityFrame < 0 || thread < 1 || ordinal < 0 || ordinal >= AeroCaptureReport.MaximumPartsPerSample)
                throw new ArgumentException("Invalid synchronous capture context.");
            AeroCaptureValidation.Number(ut); AeroCaptureValidation.Number(fixedTime); AeroCaptureValidation.Positive(step);
            sessionId = session; vesselId = vessel; this.frameKey = frameKey; physicsEpoch = epoch;
            topologyGeneration = topology; frameGeneration = frame; this.unityFrame = unityFrame;
            mainThreadId = thread; callOrdinal = ordinal; universalTime = ut; fixedTimeSeconds = fixedTime; stepSeconds = step;
        }
        public bool SameStep(AeroCaptureContext other) => other != null && sessionId == other.sessionId && vesselId == other.vesselId &&
            frameKey == other.frameKey && physicsEpoch == other.physicsEpoch && topologyGeneration == other.topologyGeneration &&
            frameGeneration == other.frameGeneration && unityFrame == other.unityFrame && mainThreadId == other.mainThreadId &&
            universalTime == other.universalTime && fixedTimeSeconds == other.fixedTimeSeconds && stepSeconds == other.stepSeconds;
    }

    public sealed class AeroPartContext
    {
        public readonly AeroCaptureContext step;
        public readonly long flightId;
        public readonly int nativePartInstanceId, nativeRigidbodyInstanceId;
        public readonly double massKilograms, densityKilogramsPerCubicMeter, mach;
        public readonly Vec worldCenterOfMass, worldVelocity, relativeAirVelocity;
        public AeroPartContext(AeroCaptureContext step, long flightId, int partId, int rigidbodyId, double mass, double density,
            double mach, Vec center, Vec velocity, Vec airVelocity)
        {
            if (step == null || flightId < 1 || flightId > uint.MaxValue || mass <= 0 || density < 0 || mach < 0)
                throw new ArgumentException("Invalid aero part context.");
            AeroCaptureValidation.Positive(mass); AeroCaptureValidation.Number(density); AeroCaptureValidation.Number(mach);
            AeroCaptureValidation.Vector(center); AeroCaptureValidation.Vector(velocity); AeroCaptureValidation.Vector(airVelocity);
            this.step = step; this.flightId = flightId; nativePartInstanceId = partId; nativeRigidbodyInstanceId = rigidbodyId;
            massKilograms = mass; densityKilogramsPerCubicMeter = density; this.mach = mach;
            worldCenterOfMass = center; worldVelocity = velocity; relativeAirVelocity = airVelocity;
        }
    }

    public sealed class AeroBodyPublication
    {
        public readonly AeroPartContext context;
        public readonly AeroPublicationKind kind;
        public readonly AeroApplicationMode applicationMode;
        public readonly Vec forceNewtons, worldApplicationPosition, torqueAboutPartCenterOfMassNewtonMeters;
        public AeroBodyPublication(AeroPartContext context, AeroPublicationKind kind, AeroApplicationMode mode,
            Vec force, Vec position, Vec torque)
        {
            if (context == null || !Enum.IsDefined(typeof(AeroPublicationKind), kind) || !Enum.IsDefined(typeof(AeroApplicationMode), mode))
                throw new ArgumentException("Invalid body aero publication.");
            AeroCaptureValidation.Vector(force); AeroCaptureValidation.Vector(position); AeroCaptureValidation.Vector(torque);
            if (mode == AeroApplicationMode.AtCenterOfMass && !AeroCaptureValidation.Equal(position, context.worldCenterOfMass))
                throw new ArgumentException("Center application must use the captured center of mass.");
            var expected = Vec.Cross(new Vec(position.X - context.worldCenterOfMass.X, position.Y - context.worldCenterOfMass.Y,
                position.Z - context.worldCenterOfMass.Z), force);
            if (!AeroCaptureValidation.Equal(expected, torque)) throw new ArgumentException("Published torque is inconsistent with force application.");
            this.context = context; this.kind = kind; applicationMode = mode; forceNewtons = force;
            worldApplicationPosition = position; torqueAboutPartCenterOfMassNewtonMeters = torque;
        }
    }

    public sealed class AeroCaptureSample
    {
        public readonly AeroCaptureContext context;
        public readonly ReadOnlyCollection<AeroBodyPublication> publications;
        public AeroCaptureSample(AeroCaptureContext context, AeroBodyPublication[] publications)
        {
            if (context == null || publications == null || publications.Length > AeroCaptureReport.MaximumPartsPerSample * 2)
                throw new ArgumentException("Invalid aero sample.");
            var keys = new HashSet<string>(); var parts = new HashSet<long>(); var ordinals = new HashSet<int>();
            foreach (var publication in publications)
            {
                if (publication == null || !context.SameStep(publication.context.step)) throw new ArgumentException("Publication context mismatch.");
                parts.Add(publication.context.flightId);
                if (!ordinals.Add(publication.context.step.callOrdinal)) throw new ArgumentException("Call ordinals must be unique.");
                string key = publication.context.flightId + ":" + publication.kind;
                if (!keys.Add(key)) throw new ArgumentException("Duplicate part publication kind.");
            }
            if (parts.Count > AeroCaptureReport.MaximumPartsPerSample) throw new ArgumentException("Too many parts in aero sample.");
            this.context = context; this.publications = Array.AsReadOnly((AeroBodyPublication[])publications.Clone());
        }
    }

    public sealed class AeroCaptureReport
    {
        public const int MaximumSamples = 64, MaximumPartsPerSample = 128;
        public readonly string schema = "ksp-continuum-aero-capture/v1";
        public readonly AeroPatchProvenance provenance;
        public readonly AeroCaptureDisposition disposition;
        public readonly AeroCaptureReason reason;
        public readonly AeroCleanupOutcome cleanup;
        public readonly ReadOnlyCollection<AeroCaptureSample> samples;
        public AeroCaptureReport(AeroPatchProvenance provenance, AeroCaptureDisposition disposition, AeroCaptureReason reason,
            AeroCleanupOutcome cleanup, AeroCaptureSample[] samples)
        {
            if (provenance == null || samples == null || samples.Length > MaximumSamples ||
                !Enum.IsDefined(typeof(AeroCaptureDisposition), disposition) || !Enum.IsDefined(typeof(AeroCaptureReason), reason) ||
                !Enum.IsDefined(typeof(AeroCleanupOutcome), cleanup)) throw new ArgumentException("Invalid aero report.");
            if ((disposition == AeroCaptureDisposition.Valid) != (reason == AeroCaptureReason.None))
                throw new ArgumentException("Only valid captures may have no failure reason.");
            if (disposition == AeroCaptureDisposition.Abstained && reason != AeroCaptureReason.UnsupportedProvider &&
                reason != AeroCaptureReason.UnsupportedScene && reason != AeroCaptureReason.PackedVessel &&
                reason != AeroCaptureReason.MissingRigidbody && reason != AeroCaptureReason.UnsupportedRegime)
                throw new ArgumentException("Abstention requires an unsupported-domain reason.");
            if (disposition == AeroCaptureDisposition.Invalid && (reason == AeroCaptureReason.None ||
                reason == AeroCaptureReason.UnsupportedProvider || reason == AeroCaptureReason.UnsupportedScene ||
                reason == AeroCaptureReason.PackedVessel || reason == AeroCaptureReason.MissingRigidbody ||
                reason == AeroCaptureReason.UnsupportedRegime))
                throw new ArgumentException("Invalid capture requires an integrity reason.");
            if (disposition != AeroCaptureDisposition.Valid && samples.Length != 0)
                throw new ArgumentException("Abstained or invalid captures cannot publish partial samples.");
            this.provenance = provenance; this.disposition = disposition; this.reason = reason; this.cleanup = cleanup;
            this.samples = Array.AsReadOnly((AeroCaptureSample[])samples.Clone());
        }
    }

    internal static class AeroCaptureValidation
    {
        internal static void Text(string value, int maximum) { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("Invalid text field."); }
        internal static void Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Nonfinite aero value."); }
        internal static void Positive(double value) { Number(value); if (value <= 0) throw new ArgumentException("Aero value must be positive."); }
        internal static void Vector(Vec value) { Number(value.X); Number(value.Y); Number(value.Z); }
        internal static void Sha256(string value)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("Invalid SHA-256.");
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) throw new ArgumentException("Invalid SHA-256.");
        }
        internal static bool Equal(Vec a, Vec b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    }
}
