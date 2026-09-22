using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public sealed class StructuralPartFact
    {
        public StructuralPartFact(string logicalId, int nativeBodyId, PartBoundaryRole? observedBoundaryRoles)
        {
            if (String.IsNullOrWhiteSpace(logicalId)) throw new ArgumentException("A logical part ID is required.", "logicalId");
            if (nativeBodyId == 0) throw new ArgumentException("A native body ID cannot be zero.", "nativeBodyId");
            LogicalId = logicalId;
            NativeBodyId = nativeBodyId;
            ObservedBoundaryRoles = observedBoundaryRoles;
        }

        public string LogicalId { get; private set; }
        public int NativeBodyId { get; private set; }
        public PartBoundaryRole? ObservedBoundaryRoles { get; private set; }
    }

    public sealed class StructuralAttachmentFact
    {
        public StructuralAttachmentFact(string logicalId, string partA, string partB,
            int? nativeJointId, AttachmentBehavior? observedBehavior)
        {
            if (String.IsNullOrWhiteSpace(logicalId) || String.IsNullOrWhiteSpace(partA)
                || String.IsNullOrWhiteSpace(partB) || String.Equals(partA, partB, StringComparison.Ordinal))
                throw new ArgumentException("An attachment fact requires an ID and two different part IDs.");
            LogicalId = logicalId;
            PartA = partA;
            PartB = partB;
            if (nativeJointId.HasValue && nativeJointId.Value == 0)
                throw new ArgumentException("A native joint ID cannot be zero.", "nativeJointId");
            NativeJointId = nativeJointId;
            ObservedBehavior = observedBehavior;
        }

        public string LogicalId { get; private set; }
        public string PartA { get; private set; }
        public string PartB { get; private set; }
        public int? NativeJointId { get; private set; }
        public AttachmentBehavior? ObservedBehavior { get; private set; }
    }

    public sealed class StructuralClusterCandidate
    {
        internal StructuralClusterCandidate(StructuralCensusResult source, SemanticClusterPlan plan,
            int mappedBodies, int mappedJoints, int projectedBodies, int projectedJoints,
            IReadOnlyList<string> abstentions)
        {
            Source = source;
            Plan = plan;
            MappedSourceBodies = mappedBodies;
            MappedSourceJoints = mappedJoints;
            ProjectedBodies = projectedBodies;
            ProjectedJoints = projectedJoints;
            Abstentions = abstentions;
        }

        public StructuralCensusResult Source { get; private set; }
        public SemanticClusterPlan Plan { get; private set; }
        public int MappedSourceBodies { get; private set; }
        public int MappedSourceJoints { get; private set; }
        public int ProjectedBodies { get; private set; }
        public int ProjectedJoints { get; private set; }
        public int BodyReduction { get { return Source.rigidbodies - ProjectedBodies; } }
        public IReadOnlyList<string> Abstentions { get; private set; }
    }

    public static class StructuralClusterCandidateCompiler
    {
        public static StructuralClusterCandidate Compile(StructuralCensusResult census,
            IEnumerable<StructuralPartFact> parts, IEnumerable<StructuralAttachmentFact> attachments)
        {
            if (census == null || parts == null || attachments == null)
                throw new ArgumentException("Census, parts and attachments are required.");

            var semanticParts = new List<SemanticPart>();
            var bodyByPart = new Dictionary<string, int>(StringComparer.Ordinal);
            var sourceBodies = new HashSet<int>();
            var abstentions = new List<string>();
            foreach (StructuralPartFact part in parts)
            {
                if (part == null) throw new ArgumentException("Part facts must be nonnull.", "parts");
                if (bodyByPart.ContainsKey(part.LogicalId)) throw new ArgumentException("Logical part IDs must be unique.", "parts");
                PartBoundaryRole roles = part.ObservedBoundaryRoles ?? PartBoundaryRole.UnknownSemantics;
                if ((roles & PartBoundaryRole.UnknownSemantics) != 0)
                    abstentions.Add("part:" + part.LogicalId + ":unknown-boundary-semantics");
                semanticParts.Add(new SemanticPart(part.LogicalId, roles));
                bodyByPart.Add(part.LogicalId, part.NativeBodyId);
                sourceBodies.Add(part.NativeBodyId);
            }
            if (sourceBodies.Count > census.rigidbodies)
                throw new ArgumentException("Mapped native bodies exceed the observed structural census.", "parts");

            var semanticAttachments = new List<SemanticAttachment>();
            var jointByAttachment = new Dictionary<string, int?>(StringComparer.Ordinal);
            var sourceJoints = new HashSet<int>();
            foreach (StructuralAttachmentFact attachment in attachments)
            {
                if (attachment == null) throw new ArgumentException("Attachment facts must be nonnull.", "attachments");
                AttachmentBehavior behavior = attachment.ObservedBehavior ?? AttachmentBehavior.UnknownSemantics;
                if (behavior == AttachmentBehavior.UnknownSemantics)
                    abstentions.Add("attachment:" + attachment.LogicalId + ":unknown-attachment-semantics");
                int bodyA, bodyB;
                if (bodyByPart.TryGetValue(attachment.PartA, out bodyA)
                    && bodyByPart.TryGetValue(attachment.PartB, out bodyB)
                    && bodyA != bodyB && !attachment.NativeJointId.HasValue)
                {
                    behavior = AttachmentBehavior.UnknownSemantics;
                    abstentions.Add("attachment:" + attachment.LogicalId + ":cross-body-without-native-joint");
                }
                semanticAttachments.Add(new SemanticAttachment(attachment.LogicalId, attachment.PartA, attachment.PartB, behavior));
                if (jointByAttachment.ContainsKey(attachment.LogicalId))
                    throw new ArgumentException("Logical attachment IDs must be unique.", "attachments");
                jointByAttachment.Add(attachment.LogicalId, attachment.NativeJointId);
                if (attachment.NativeJointId.HasValue && !sourceJoints.Add(attachment.NativeJointId.Value))
                    throw new ArgumentException("Native joint IDs must be unique.", "attachments");
            }
            if (sourceJoints.Count > census.joints)
                throw new ArgumentException("Mapped native joints exceed the observed structural census.", "attachments");

            SemanticClusterPlan plan = SemanticClusterCompiler.Compile(semanticParts, semanticAttachments);
            var clusterByBody = new Dictionary<int, string>();
            foreach (KeyValuePair<string, int> item in bodyByPart)
            {
                string cluster = plan.ClusterByPartId[item.Key];
                string existing;
                if (clusterByBody.TryGetValue(item.Value, out existing) && existing != cluster)
                    throw new InvalidOperationException("One captured native body crosses proposed semantic seams.");
                clusterByBody[item.Value] = cluster;
            }

            int unmappedBodies = census.rigidbodies - sourceBodies.Count;
            int unmappedJoints = census.joints - sourceJoints.Count;
            if (unmappedBodies != 0) abstentions.Add("census:unmapped-native-bodies:" + unmappedBodies);
            if (unmappedJoints != 0) abstentions.Add("census:unmapped-native-joints:" + unmappedJoints);
            int projectedJoints = unmappedJoints;
            foreach (ClusterSplitSeam seam in plan.SplitSeams)
            {
                int? nativeJoint = jointByAttachment[seam.AttachmentId];
                if (nativeJoint.HasValue) projectedJoints++;
                else abstentions.Add("seam:" + seam.AttachmentId + ":missing-native-joint-fact");
            }
            abstentions.Sort(StringComparer.Ordinal);
            return new StructuralClusterCandidate(census, plan, sourceBodies.Count, sourceJoints.Count,
                plan.Clusters.Count + unmappedBodies, projectedJoints,
                new ReadOnlyCollection<string>(abstentions));
        }
    }
}
