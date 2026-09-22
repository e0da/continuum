using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace KspContinuum
{
    [Flags]
    public enum PartBoundaryRole
    {
        None = 0,
        IndependentlySimulated = 1,
        ExternalInterface = 2
    }

    public enum AttachmentBehavior
    {
        Rigid,
        Detachable,
        Articulated,
        Compliant,
        ExternalInterface
    }

    [Flags]
    public enum ClusterSeamReason
    {
        None = 0,
        Detachable = 1,
        Articulated = 2,
        Compliant = 4,
        IndependentlySimulated = 8,
        ExternalInterface = 16
    }

    public sealed class SemanticPart
    {
        public SemanticPart(string logicalId, PartBoundaryRole boundaryRoles)
        {
            if (string.IsNullOrWhiteSpace(logicalId))
                throw new ArgumentException("A nonempty logical part ID is required.", "logicalId");
            const PartBoundaryRole allowed = PartBoundaryRole.IndependentlySimulated | PartBoundaryRole.ExternalInterface;
            if ((boundaryRoles & ~allowed) != 0)
                throw new ArgumentOutOfRangeException("boundaryRoles");
            LogicalId = logicalId;
            BoundaryRoles = boundaryRoles;
        }

        public string LogicalId { get; private set; }
        public PartBoundaryRole BoundaryRoles { get; private set; }
    }

    public sealed class SemanticAttachment
    {
        public SemanticAttachment(string logicalId, string partA, string partB, AttachmentBehavior behavior)
        {
            if (string.IsNullOrWhiteSpace(logicalId) || string.IsNullOrWhiteSpace(partA)
                || string.IsNullOrWhiteSpace(partB) || string.Equals(partA, partB, StringComparison.Ordinal))
                throw new ArgumentException("An attachment requires a nonempty ID and two different part IDs.");
            if (!Enum.IsDefined(typeof(AttachmentBehavior), behavior))
                throw new ArgumentOutOfRangeException("behavior");
            LogicalId = logicalId;
            PartA = partA;
            PartB = partB;
            Behavior = behavior;
        }

        public string LogicalId { get; private set; }
        public string PartA { get; private set; }
        public string PartB { get; private set; }
        public AttachmentBehavior Behavior { get; private set; }
    }

    public sealed class SemanticCluster
    {
        internal SemanticCluster(string id, IReadOnlyList<string> memberIds)
        {
            Id = id;
            MemberIds = memberIds;
        }

        public string Id { get; private set; }
        public IReadOnlyList<string> MemberIds { get; private set; }
    }

    public sealed class ClusterSplitSeam
    {
        internal ClusterSplitSeam(string attachmentId, string partA, string partB, string clusterA,
            string clusterB, ClusterSeamReason reasons)
        {
            AttachmentId = attachmentId;
            PartA = partA;
            PartB = partB;
            ClusterA = clusterA;
            ClusterB = clusterB;
            Reasons = reasons;
        }

        public string AttachmentId { get; private set; }
        public string PartA { get; private set; }
        public string PartB { get; private set; }
        public string ClusterA { get; private set; }
        public string ClusterB { get; private set; }
        public ClusterSeamReason Reasons { get; private set; }
    }

    public sealed class SemanticClusterPlan
    {
        internal SemanticClusterPlan(IReadOnlyList<SemanticCluster> clusters,
            IReadOnlyDictionary<string, string> clusterByPartId, IReadOnlyList<ClusterSplitSeam> seams)
        {
            Clusters = clusters;
            ClusterByPartId = clusterByPartId;
            SplitSeams = seams;
        }

        public IReadOnlyList<SemanticCluster> Clusters { get; private set; }
        public IReadOnlyDictionary<string, string> ClusterByPartId { get; private set; }
        public IReadOnlyList<ClusterSplitSeam> SplitSeams { get; private set; }
    }

    public static class SemanticClusterCompiler
    {
        public static SemanticClusterPlan Compile(IEnumerable<SemanticPart> parts,
            IEnumerable<SemanticAttachment> attachments)
        {
            if (parts == null || attachments == null)
                throw new ArgumentException("Parts and attachments are required.");

            var orderedParts = new List<SemanticPart>(parts);
            for (int i = 0; i < orderedParts.Count; i++)
                if (orderedParts[i] == null) throw new ArgumentException("Parts must be nonnull.", "parts");
            orderedParts.Sort(delegate(SemanticPart a, SemanticPart b)
                { return StringComparer.Ordinal.Compare(a.LogicalId, b.LogicalId); });
            if (orderedParts.Count == 0) throw new ArgumentException("At least one part is required.", "parts");

            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < orderedParts.Count; i++)
            {
                if (!indexById.TryAdd(orderedParts[i].LogicalId, i))
                    throw new ArgumentException("Logical part IDs must be unique.", "parts");
            }

            var orderedAttachments = new List<SemanticAttachment>(attachments);
            for (int i = 0; i < orderedAttachments.Count; i++)
                if (orderedAttachments[i] == null)
                    throw new ArgumentException("Attachments must be nonnull.", "attachments");
            orderedAttachments.Sort(delegate(SemanticAttachment a, SemanticAttachment b)
                { return StringComparer.Ordinal.Compare(a.LogicalId, b.LogicalId); });
            var attachmentIds = new HashSet<string>(StringComparer.Ordinal);
            var parent = new int[orderedParts.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            foreach (SemanticAttachment attachment in orderedAttachments)
            {
                if (!attachmentIds.Add(attachment.LogicalId))
                    throw new ArgumentException("Logical attachment IDs must be unique.", "attachments");
                int a, b;
                if (!indexById.TryGetValue(attachment.PartA, out a) || !indexById.TryGetValue(attachment.PartB, out b))
                    throw new ArgumentException("Every attachment endpoint must name a supplied part.", "attachments");
                if (Reasons(attachment, orderedParts[a], orderedParts[b]) == ClusterSeamReason.None)
                    Union(parent, a, b);
            }

            var memberIdsByRoot = new Dictionary<int, List<string>>();
            for (int i = 0; i < orderedParts.Count; i++)
            {
                int root = Find(parent, i);
                List<string> members;
                if (!memberIdsByRoot.TryGetValue(root, out members))
                {
                    members = new List<string>();
                    memberIdsByRoot.Add(root, members);
                }
                members.Add(orderedParts[i].LogicalId);
            }

            var clusters = new List<SemanticCluster>();
            var clusterByPartId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (List<string> members in memberIdsByRoot.Values)
            {
                members.Sort(StringComparer.Ordinal);
                string clusterId = StableClusterId(members);
                var frozenMembers = new ReadOnlyCollection<string>(members);
                clusters.Add(new SemanticCluster(clusterId, frozenMembers));
                foreach (string member in members) clusterByPartId.Add(member, clusterId);
            }
            clusters.Sort(delegate(SemanticCluster a, SemanticCluster b)
                { return StringComparer.Ordinal.Compare(a.Id, b.Id); });

            var seams = new List<ClusterSplitSeam>();
            foreach (SemanticAttachment attachment in orderedAttachments)
            {
                SemanticPart partA = orderedParts[indexById[attachment.PartA]];
                SemanticPart partB = orderedParts[indexById[attachment.PartB]];
                ClusterSeamReason reasons = Reasons(attachment, partA, partB);
                if (reasons != ClusterSeamReason.None)
                {
                    bool canonical = StringComparer.Ordinal.Compare(attachment.PartA, attachment.PartB) < 0;
                    string first = canonical ? attachment.PartA : attachment.PartB;
                    string second = canonical ? attachment.PartB : attachment.PartA;
                    seams.Add(new ClusterSplitSeam(attachment.LogicalId, first, second,
                        clusterByPartId[first], clusterByPartId[second], reasons));
                }
            }

            return new SemanticClusterPlan(new ReadOnlyCollection<SemanticCluster>(clusters),
                new ReadOnlyDictionary<string, string>(clusterByPartId),
                new ReadOnlyCollection<ClusterSplitSeam>(seams));
        }

        static ClusterSeamReason Reasons(SemanticAttachment attachment, SemanticPart partA, SemanticPart partB)
        {
            ClusterSeamReason result = ClusterSeamReason.None;
            switch (attachment.Behavior)
            {
                case AttachmentBehavior.Detachable: result |= ClusterSeamReason.Detachable; break;
                case AttachmentBehavior.Articulated: result |= ClusterSeamReason.Articulated; break;
                case AttachmentBehavior.Compliant: result |= ClusterSeamReason.Compliant; break;
                case AttachmentBehavior.ExternalInterface: result |= ClusterSeamReason.ExternalInterface; break;
            }
            result |= PartReasons(partA.BoundaryRoles);
            result |= PartReasons(partB.BoundaryRoles);
            return result;
        }

        static ClusterSeamReason PartReasons(PartBoundaryRole roles)
        {
            ClusterSeamReason result = ClusterSeamReason.None;
            if ((roles & PartBoundaryRole.IndependentlySimulated) != 0)
                result |= ClusterSeamReason.IndependentlySimulated;
            if ((roles & PartBoundaryRole.ExternalInterface) != 0) result |= ClusterSeamReason.ExternalInterface;
            return result;
        }

        static string StableClusterId(IReadOnlyList<string> members)
        {
            var canonicalBuilder = new StringBuilder();
            for (int i = 0; i < members.Count; i++)
                canonicalBuilder.Append(members[i].Length).Append(':').Append(members[i]);
            string canonical = canonicalBuilder.ToString();
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var hex = new StringBuilder(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++) hex.Append(digest[i].ToString("x2"));
            return "cluster-" + hex;
        }

        static int Find(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a), rootB = Find(parent, b);
            if (rootA == rootB) return;
            if (rootA < rootB) parent[rootB] = rootA; else parent[rootA] = rootB;
        }
    }
}
