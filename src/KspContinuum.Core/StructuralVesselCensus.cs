using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class StructuralVesselCaptureContext
    {
        public StructuralVesselCaptureContext(string vesselId, string scene, int frame, long physicsTick,
            double fixedTimeSeconds, double universalTimeSeconds, string saveIdentity, string checkpointIdentity)
        {
            if (!Guid.TryParse(vesselId, out Guid parsed) || parsed == Guid.Empty)
                throw new ArgumentException("A nonempty vessel GUID is required.", "vesselId");
            ValidateText(scene, "scene", 64, false); ValidateText(saveIdentity, "saveIdentity", 128, false);
            ValidateText(checkpointIdentity, "checkpointIdentity", 128, true);
            if (frame < 0 || physicsTick < 0 || !Finite(fixedTimeSeconds) || fixedTimeSeconds < 0
                || !Finite(universalTimeSeconds)) throw new ArgumentException("Capture time is invalid.");
            VesselId = parsed.ToString("D"); Scene = scene; Frame = frame; PhysicsTick = physicsTick;
            FixedTimeSeconds = fixedTimeSeconds; UniversalTimeSeconds = universalTimeSeconds;
            SaveIdentity = saveIdentity; CheckpointIdentity = checkpointIdentity;
        }

        public string VesselId { get; private set; }
        public string Scene { get; private set; }
        public int Frame { get; private set; }
        public long PhysicsTick { get; private set; }
        public double FixedTimeSeconds { get; private set; }
        public double UniversalTimeSeconds { get; private set; }
        public string SaveIdentity { get; private set; }
        public string CheckpointIdentity { get; private set; }

        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static void ValidateText(string value, string name, int maximum, bool optional)
        {
            if (value == null ? !optional : value.Length == 0 || value.Length > maximum || HasControl(value))
                throw new ArgumentException("Capture identity is invalid.", name);
        }
        static bool HasControl(string value)
        { foreach (char character in value) if (Char.IsControl(character)) return true; return false; }
    }

    [Serializable] public sealed class StructuralVesselCaptureContextReport
    {
        public string vesselId, scene, saveIdentity, checkpointIdentity;
        public int frame;
        public long physicsTick;
        public double fixedTimeSeconds, universalTimeSeconds;
    }

    public sealed class StructuralPartObservation
    {
        public StructuralPartObservation(string logicalId, int? nativeBodyId,
            PartBoundaryRole? observedBoundaryRoles, string omissionReason)
        {
            if (String.IsNullOrWhiteSpace(logicalId)) throw new ArgumentException("A logical part ID is required.", "logicalId");
            if (nativeBodyId.HasValue && nativeBodyId.Value == 0) throw new ArgumentException("A native body ID cannot be zero.", "nativeBodyId");
            LogicalId = logicalId; NativeBodyId = nativeBodyId;
            ObservedBoundaryRoles = observedBoundaryRoles; OmissionReason = omissionReason;
        }

        public string LogicalId { get; private set; }
        public int? NativeBodyId { get; private set; }
        public PartBoundaryRole? ObservedBoundaryRoles { get; private set; }
        public string OmissionReason { get; private set; }
    }

    public sealed class StructuralAttachmentObservation
    {
        public StructuralAttachmentObservation(string logicalId, string partA, string partB,
            int? nativeJointId, AttachmentBehavior? observedBehavior, string omissionReason)
        {
            if (String.IsNullOrWhiteSpace(logicalId) || String.IsNullOrWhiteSpace(partA)
                || String.IsNullOrWhiteSpace(partB) || String.Equals(partA, partB, StringComparison.Ordinal))
                throw new ArgumentException("An attachment observation requires an ID and two different part IDs.");
            if (nativeJointId.HasValue && nativeJointId.Value == 0) throw new ArgumentException("A native joint ID cannot be zero.", "nativeJointId");
            LogicalId = logicalId; PartA = partA; PartB = partB; NativeJointId = nativeJointId;
            ObservedBehavior = observedBehavior; OmissionReason = omissionReason;
        }

        public string LogicalId { get; private set; }
        public string PartA { get; private set; }
        public string PartB { get; private set; }
        public int? NativeJointId { get; private set; }
        public AttachmentBehavior? ObservedBehavior { get; private set; }
        public string OmissionReason { get; private set; }
    }

    [Serializable] public sealed class StructuralVesselPartRow
    {
        public string logicalId, boundarySemantics, omissionReason;
        public int? nativeBodyId;
    }

    [Serializable] public sealed class StructuralVesselAttachmentRow
    {
        public string logicalId, partA, partB, attachmentSemantics, omissionReason;
        public int? nativeJointId;
    }

    [Serializable] public sealed class StructuralVesselCandidateSummary
    {
        public string status, rejectionReason;
        public int mappedBodies, mappedJoints, clusters, splitSeams, projectedBodies, projectedJoints, bodyReduction;
        public string[] abstentions;
    }

    [Serializable] public sealed class StructuralVesselCensusReport
    {
        public string schema = "continuum-structural-vessel-census/v1";
        public string status = "read-only-observation";
        public string semantics = "unclassified-observations-remain-explicit-abstentions";
        public int sourceRigidbodies, sourceJoints, sourceColliders, observedParts, observedAttachments;
        public StructuralVesselCaptureContextReport context;
        public StructuralVesselPartRow[] parts;
        public StructuralVesselAttachmentRow[] attachments;
        public StructuralVesselCandidateSummary candidate;
    }

    public static class StructuralVesselCensus
    {
        public static StructuralVesselCensusReport Build(StructuralVesselCaptureContext context,
            StructuralCensusResult census,
            IEnumerable<StructuralPartObservation> parts, IEnumerable<StructuralAttachmentObservation> attachments)
        {
            if (context == null || census == null || parts == null || attachments == null)
                throw new ArgumentException("Context, census, parts and attachments are required.");
            var orderedParts = new List<StructuralPartObservation>(parts);
            var orderedAttachments = new List<StructuralAttachmentObservation>(attachments);
            orderedParts.Sort(delegate(StructuralPartObservation a, StructuralPartObservation b)
                { return StringComparer.Ordinal.Compare(a.LogicalId, b.LogicalId); });
            orderedAttachments.Sort(delegate(StructuralAttachmentObservation a, StructuralAttachmentObservation b)
                { return StringComparer.Ordinal.Compare(a.LogicalId, b.LogicalId); });

            var rows = new List<StructuralVesselPartRow>();
            var facts = new List<StructuralPartFact>();
            var admitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuralPartObservation part in orderedParts)
            {
                if (part == null) throw new ArgumentException("Part observations must be nonnull.", "parts");
                if (!admitted.Add(part.LogicalId)) throw new ArgumentException("Logical part IDs must be unique.", "parts");
                rows.Add(new StructuralVesselPartRow { logicalId = part.LogicalId, nativeBodyId = part.NativeBodyId,
                    boundarySemantics = part.ObservedBoundaryRoles.HasValue ? part.ObservedBoundaryRoles.Value.ToString() : "UnknownSemantics",
                    omissionReason = part.NativeBodyId.HasValue ? part.OmissionReason : part.OmissionReason ?? "missing-native-body" });
                if (part.NativeBodyId.HasValue)
                    facts.Add(new StructuralPartFact(part.LogicalId, part.NativeBodyId.Value, part.ObservedBoundaryRoles));
            }

            var attachmentRows = new List<StructuralVesselAttachmentRow>();
            var attachmentFacts = new List<StructuralAttachmentFact>();
            var attachmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuralAttachmentObservation attachment in orderedAttachments)
            {
                if (attachment == null) throw new ArgumentException("Attachment observations must be nonnull.", "attachments");
                if (!attachmentIds.Add(attachment.LogicalId)) throw new ArgumentException("Logical attachment IDs must be unique.", "attachments");
                bool endpointsMapped = admitted.Contains(attachment.PartA) && admitted.Contains(attachment.PartB)
                    && HasBody(orderedParts, attachment.PartA) && HasBody(orderedParts, attachment.PartB);
                string omission = endpointsMapped ? attachment.OmissionReason : attachment.OmissionReason ?? "endpoint-without-native-body";
                attachmentRows.Add(new StructuralVesselAttachmentRow { logicalId = attachment.LogicalId,
                    partA = attachment.PartA, partB = attachment.PartB, nativeJointId = attachment.NativeJointId,
                    attachmentSemantics = attachment.ObservedBehavior.HasValue ? attachment.ObservedBehavior.Value.ToString() : "UnknownSemantics",
                    omissionReason = omission });
                if (endpointsMapped)
                    attachmentFacts.Add(new StructuralAttachmentFact(attachment.LogicalId, attachment.PartA,
                        attachment.PartB, attachment.NativeJointId, attachment.ObservedBehavior));
            }

            var report = new StructuralVesselCensusReport { context = new StructuralVesselCaptureContextReport {
                    vesselId = context.VesselId, scene = context.Scene, frame = context.Frame,
                    physicsTick = context.PhysicsTick, fixedTimeSeconds = context.FixedTimeSeconds,
                    universalTimeSeconds = context.UniversalTimeSeconds, saveIdentity = context.SaveIdentity,
                    checkpointIdentity = context.CheckpointIdentity }, sourceRigidbodies = census.rigidbodies,
                sourceJoints = census.joints, sourceColliders = census.colliders, observedParts = rows.Count,
                observedAttachments = attachmentRows.Count, parts = rows.ToArray(), attachments = attachmentRows.ToArray() };
            if (facts.Count == 0)
            {
                report.candidate = new StructuralVesselCandidateSummary { status = "rejected-read-only-candidate",
                    rejectionReason = "No parts mapped to an observed native body.", abstentions = new string[0] };
                return report;
            }
            try
            {
                StructuralClusterCandidate candidate = StructuralClusterCandidateCompiler.Compile(census, facts, attachmentFacts);
                report.candidate = new StructuralVesselCandidateSummary { status = "compiled-read-only-candidate",
                    mappedBodies = candidate.MappedSourceBodies, mappedJoints = candidate.MappedSourceJoints,
                    clusters = candidate.Plan.Clusters.Count, splitSeams = candidate.Plan.SplitSeams.Count,
                    projectedBodies = candidate.ProjectedBodies, projectedJoints = candidate.ProjectedJoints,
                    bodyReduction = candidate.BodyReduction, abstentions = Copy(candidate.Abstentions) };
            }
            catch (InvalidOperationException error)
            {
                report.candidate = new StructuralVesselCandidateSummary { status = "rejected-read-only-candidate",
                    rejectionReason = error.Message, abstentions = new string[0] };
            }
            return report;
        }

        static bool HasBody(List<StructuralPartObservation> parts, string id)
        {
            foreach (StructuralPartObservation part in parts)
                if (String.Equals(part.LogicalId, id, StringComparison.Ordinal)) return part.NativeBodyId.HasValue;
            return false;
        }

        static string[] Copy(IReadOnlyList<string> values)
        {
            var result = new string[values.Count];
            for (int i = 0; i < values.Count; i++) result[i] = values[i];
            return result;
        }
    }
}
