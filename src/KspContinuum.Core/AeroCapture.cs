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
            Guid parsed; if (!Guid.TryParse(mvid, out parsed) || parsed == Guid.Empty) throw new ArgumentException("Invalid provider MVID.");
            this.provider = provider; providerVersion = version; assemblyName = assembly;
            assemblySha256 = sha256.ToLowerInvariant(); assemblyMvid = parsed.ToString("D");
        }
    }

    public sealed class AeroPatchProvenance
    {
        public const int MaximumPatches = 64;
        public readonly AeroProviderFingerprint provider;
        public readonly string captureOwner;
        public readonly ReadOnlyCollection<AeroPatchTarget> targets;
        public AeroPatchProvenance(AeroProviderFingerprint provider, string owner, AeroPatchTarget[] targets)
        {
            if (provider == null || targets == null || targets.Length != 3)
                throw new ArgumentException("Invalid patch provenance.");
            AeroCaptureValidation.Text(owner, 256);
            var required = new HashSet<string> { "FlightIntegrator.UpdateAerodynamics", "FlightIntegrator.ApplyAeroDrag", "FlightIntegrator.ApplyAeroLift" };
            int patchCount = 0;
            foreach (var target in targets)
            {
                if (target == null || !required.Remove(target.targetMethod)) throw new ArgumentException("Patch provenance must cover each stock aero target exactly once.");
                patchCount += target.orderedPatches.Count;
            }
            if (patchCount > MaximumPatches) throw new ArgumentException("Patch graph exceeds its bound.");
            this.provider = provider; captureOwner = owner; this.targets = Array.AsReadOnly((AeroPatchTarget[])targets.Clone());
        }
        public bool HasExpectedCapturePatches()
        {
            foreach (var target in targets)
            {
                string method = target.targetMethod == "FlightIntegrator.UpdateAerodynamics" ? "KspContinuum.AeroCapture.UpdateFinalizer" :
                    target.targetMethod == "FlightIntegrator.ApplyAeroDrag" ? "KspContinuum.AeroCapture.DragPostfix" : "KspContinuum.AeroCapture.LiftPostfix";
                string kind = target.targetMethod == "FlightIntegrator.UpdateAerodynamics" ? "finalizer" : "postfix";
                bool found = false;
                foreach (var patch in target.orderedPatches)
                    if (patch.owner == captureOwner && patch.patchMethod == method && patch.patchKind == kind) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
    }

    public sealed class AeroPatchTarget
    {
        public readonly string targetMethod;
        public readonly ReadOnlyCollection<AeroPatchEntry> orderedPatches;
        public AeroPatchTarget(string target, AeroPatchEntry[] patches)
        {
            AeroCaptureValidation.Text(target, 512);
            if (patches == null || patches.Length > AeroPatchProvenance.MaximumPatches) throw new ArgumentException("Invalid patch list.");
            var identities = new HashSet<string>();
            for (int index = 0; index < patches.Length; index++)
            {
                var patch = patches[index];
                if (patch == null || patch.executionIndex != index || !identities.Add(patch.owner + "\n" + patch.patchMethod))
                    throw new ArgumentException("Patch identities and order must be unique and contiguous.");
            }
            targetMethod = target;
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
            Guid sessionGuid, vesselGuid;
            if (!Guid.TryParse(session, out sessionGuid) || sessionGuid == Guid.Empty || !Guid.TryParse(vessel, out vesselGuid) || vesselGuid == Guid.Empty)
                throw new ArgumentException("Invalid capture identity.");
            AeroCaptureValidation.Text(frameKey, 512);
            if (epoch < 1 || topology < 1 || frame < 1 || unityFrame < 0 || thread < 1 || ordinal < 0 || ordinal >= AeroCaptureReport.MaximumPartsPerSample * 2)
                throw new ArgumentException("Invalid synchronous capture context.");
            AeroCaptureValidation.Number(ut); AeroCaptureValidation.Number(fixedTime); AeroCaptureValidation.Positive(step);
            sessionId = sessionGuid.ToString("D"); vesselId = vesselGuid.ToString("D"); this.frameKey = frameKey; physicsEpoch = epoch;
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
        public readonly double massKilograms, densityKilogramsPerCubicMeter, staticPressurePascals, temperatureKelvin,
            speedOfSoundMetersPerSecond, mach, aerodynamicAreaSquareMeters, exposedAreaSquareMeters;
        public readonly bool shielded;
        public readonly Vec worldCenterOfMass, worldVelocity, relativeAirVelocity, worldAngularVelocity,
            worldAttitudeXYZ;
        public readonly double worldAttitudeW;
        public readonly ReadOnlyCollection<AeroDragCubeState> dragCubes;
        public AeroPartContext(AeroCaptureContext step, long flightId, int partId, int rigidbodyId, double mass, double density,
            double pressure, double temperature, double speedOfSound, double mach, double aerodynamicArea, double exposedArea,
            bool shielded, Vec center, Vec velocity, Vec airVelocity, Vec angularVelocity, Vec attitudeXYZ, double attitudeW,
            AeroDragCubeState[] dragCubes)
        {
            if (step == null || flightId < 1 || flightId > uint.MaxValue || mass <= 0 || density < 0 || pressure < 0 ||
                temperature < 0 || speedOfSound < 0 || mach < 0 || aerodynamicArea < 0 || exposedArea < 0)
                throw new ArgumentException("Invalid aero part context.");
            foreach (double value in new[] { mass, density, pressure, temperature, speedOfSound, mach, aerodynamicArea, exposedArea, attitudeW }) AeroCaptureValidation.Number(value);
            foreach (var value in new[] { center, velocity, airVelocity, angularVelocity, attitudeXYZ }) AeroCaptureValidation.Vector(value);
            double norm = attitudeXYZ.X * attitudeXYZ.X + attitudeXYZ.Y * attitudeXYZ.Y + attitudeXYZ.Z * attitudeXYZ.Z + attitudeW * attitudeW;
            if (Math.Abs(norm - 1) > 1e-9 || dragCubes == null || dragCubes.Length > AeroDragCubeState.MaximumBlendedCubes)
                throw new ArgumentException("Invalid stock geometry state.");
            foreach (var cube in dragCubes) if (cube == null) throw new ArgumentException("Null drag cube.");
            this.step = step; this.flightId = flightId; nativePartInstanceId = partId; nativeRigidbodyInstanceId = rigidbodyId;
            massKilograms = mass; densityKilogramsPerCubicMeter = density; staticPressurePascals = pressure;
            temperatureKelvin = temperature; speedOfSoundMetersPerSecond = speedOfSound; this.mach = mach;
            aerodynamicAreaSquareMeters = aerodynamicArea; exposedAreaSquareMeters = exposedArea; this.shielded = shielded;
            worldCenterOfMass = center; worldVelocity = velocity; relativeAirVelocity = airVelocity; worldAngularVelocity = angularVelocity;
            worldAttitudeXYZ = attitudeXYZ; worldAttitudeW = attitudeW;
            this.dragCubes = Array.AsReadOnly((AeroDragCubeState[])dragCubes.Clone());
        }
        public bool SameState(AeroPartContext other) => other != null && flightId == other.flightId && nativePartInstanceId == other.nativePartInstanceId &&
            nativeRigidbodyInstanceId == other.nativeRigidbodyInstanceId && massKilograms == other.massKilograms && densityKilogramsPerCubicMeter == other.densityKilogramsPerCubicMeter &&
            staticPressurePascals == other.staticPressurePascals && temperatureKelvin == other.temperatureKelvin && speedOfSoundMetersPerSecond == other.speedOfSoundMetersPerSecond &&
            mach == other.mach && aerodynamicAreaSquareMeters == other.aerodynamicAreaSquareMeters && exposedAreaSquareMeters == other.exposedAreaSquareMeters && shielded == other.shielded &&
            AeroCaptureValidation.Equal(worldCenterOfMass, other.worldCenterOfMass) && AeroCaptureValidation.Equal(worldVelocity, other.worldVelocity) &&
            AeroCaptureValidation.Equal(relativeAirVelocity, other.relativeAirVelocity) && AeroCaptureValidation.Equal(worldAngularVelocity, other.worldAngularVelocity) &&
            AeroCaptureValidation.Equal(worldAttitudeXYZ, other.worldAttitudeXYZ) && worldAttitudeW == other.worldAttitudeW && SameCubes(other.dragCubes);
        bool SameCubes(ReadOnlyCollection<AeroDragCubeState> other)
        {
            if (dragCubes.Count != other.Count) return false;
            for (int i = 0; i < dragCubes.Count; i++) if (!dragCubes[i].SameState(other[i])) return false;
            return true;
        }
    }

    public sealed class AeroDragCubeState
    {
        public const int FaceCount = 6, MaximumBlendedCubes = 16;
        public readonly string name;
        public readonly double weight;
        public readonly Vec center, size;
        public readonly ReadOnlyCollection<double> area, drag, depth, dragModifiers;
        public AeroDragCubeState(string name, double weight, Vec center, Vec size, double[] area, double[] drag,
            double[] depth, double[] modifiers)
        {
            AeroCaptureValidation.Text(name, 128); AeroCaptureValidation.Number(weight);
            AeroCaptureValidation.Vector(center); AeroCaptureValidation.Vector(size);
            if (weight < 0 || size.X < 0 || size.Y < 0 || size.Z < 0) throw new ArgumentException("Invalid drag cube shape or weight.");
            this.area = Faces(area); this.drag = Faces(drag); this.depth = Faces(depth); dragModifiers = Faces(modifiers);
            this.name = name; this.weight = weight; this.center = center; this.size = size;
        }
        static ReadOnlyCollection<double> Faces(double[] values)
        {
            if (values == null || values.Length != FaceCount) throw new ArgumentException("Drag cube data requires six faces.");
            var copy = (double[])values.Clone();
            foreach (double value in copy) { AeroCaptureValidation.Number(value); if (value < 0) throw new ArgumentException("Drag cube face values cannot be negative."); }
            return Array.AsReadOnly(copy);
        }
        internal bool SameState(AeroDragCubeState other) => other != null && name == other.name && weight == other.weight &&
            AeroCaptureValidation.Equal(center, other.center) && AeroCaptureValidation.Equal(size, other.size) &&
            Same(area, other.area) && Same(drag, other.drag) && Same(depth, other.depth) && Same(dragModifiers, other.dragModifiers);
        static bool Same(ReadOnlyCollection<double> left, ReadOnlyCollection<double> right)
        { for (int i = 0; i < FaceCount; i++) if (left[i] != right[i]) return false; return true; }
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
            var keys = new HashSet<string>(); var parts = new Dictionary<long, AeroPartContext>(); var ordinals = new HashSet<int>();
            foreach (var publication in publications)
            {
                if (publication == null || !context.SameStep(publication.context.step)) throw new ArgumentException("Publication context mismatch.");
                AeroPartContext prior;
                if (parts.TryGetValue(publication.context.flightId, out prior) && !prior.SameState(publication.context)) throw new ArgumentException("Drag and lift must share identical per-part state.");
                parts[publication.context.flightId] = publication.context;
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
            if (cleanup == AeroCleanupOutcome.Failed && disposition != AeroCaptureDisposition.Invalid)
                throw new ArgumentException("Cleanup failure invalidates the capture.");
            if (disposition == AeroCaptureDisposition.Valid && (cleanup != AeroCleanupOutcome.RemovedOwnedPatches || !provenance.HasExpectedCapturePatches()))
                throw new ArgumentException("Valid capture requires expected owned patches and confirmed removal.");
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
