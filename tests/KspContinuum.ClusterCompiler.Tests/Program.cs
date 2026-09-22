using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }

    static string Signature(SemanticClusterPlan plan)
    {
        var clusters = new List<string>();
        foreach (SemanticCluster cluster in plan.Clusters) clusters.Add(string.Join(",", cluster.MemberIds));
        clusters.Sort(StringComparer.Ordinal);
        var seams = new List<string>();
        foreach (ClusterSplitSeam seam in plan.SplitSeams)
            seams.Add(seam.AttachmentId + ":" + seam.PartA + ":" + seam.PartB + ":" + seam.Reasons);
        return string.Join("|", clusters) + " / " + string.Join("|", seams);
    }

    static int Main()
    {
        var parts = new[]
        {
            new SemanticPart("tank-upper", PartBoundaryRole.None),
            new SemanticPart("engine", PartBoundaryRole.None),
            new SemanticPart("payload", PartBoundaryRole.None),
            new SemanticPart("tank-lower", PartBoundaryRole.None),
            new SemanticPart("decoupler", PartBoundaryRole.None),
            new SemanticPart("wheel", PartBoundaryRole.IndependentlySimulated),
            new SemanticPart("fin", PartBoundaryRole.IndependentlySimulated),
            new SemanticPart("port", PartBoundaryRole.ExternalInterface)
        };
        var attachments = new[]
        {
            new SemanticAttachment("a4", "tank-upper", "decoupler", AttachmentBehavior.Detachable),
            new SemanticAttachment("a1", "engine", "tank-lower", AttachmentBehavior.Rigid),
            new SemanticAttachment("a7", "tank-lower", "fin", AttachmentBehavior.Rigid),
            new SemanticAttachment("a3", "tank-lower", "tank-upper", AttachmentBehavior.Rigid),
            new SemanticAttachment("a6", "tank-lower", "wheel", AttachmentBehavior.Rigid),
            new SemanticAttachment("a5", "decoupler", "payload", AttachmentBehavior.Rigid),
            new SemanticAttachment("a8", "payload", "port", AttachmentBehavior.Rigid)
        };

        SemanticClusterPlan plan = SemanticClusterCompiler.Compile(parts, attachments);
        string propulsion = plan.ClusterByPartId["engine"];
        Check(plan.Clusters.Count == 5, "expected one baked propulsion cluster and four semantic boundary clusters");
        Check(plan.ClusterByPartId["tank-lower"] == propulsion && plan.ClusterByPartId["tank-upper"] == propulsion,
            "ordinary rigid stage parts did not compile together");
        Check(plan.ClusterByPartId["wheel"] != propulsion && plan.ClusterByPartId["fin"] != propulsion,
            "independent actuator was baked into its parent cluster");
        Check(plan.ClusterByPartId.Count == parts.Length, "logical part identity map is incomplete");
        Check(plan.SplitSeams.Count == 4, "expected decoupler, wheel, control-surface, and docking seams");
        Check((plan.SplitSeams[0].Reasons & ClusterSeamReason.Detachable) != 0, "detachable reason missing");
        Check((plan.SplitSeams[1].Reasons & ClusterSeamReason.IndependentlySimulated) != 0, "wheel boundary missing");
        Check((plan.SplitSeams[2].Reasons & ClusterSeamReason.IndependentlySimulated) != 0, "control-surface boundary missing");
        Check((plan.SplitSeams[3].Reasons & ClusterSeamReason.ExternalInterface) != 0
            && (plan.SplitSeams[3].Reasons & ClusterSeamReason.ExternalInterface) != 0,
            "combined docking/external-interface reasons missing");
        foreach (ClusterSplitSeam seam in plan.SplitSeams)
            Check(seam.ClusterA != seam.ClusterB, "split seam endpoints resolved to one cluster");

        var reversedParts = new List<SemanticPart>(parts); reversedParts.Reverse();
        var reversedAttachments = new List<SemanticAttachment>();
        for (int i = attachments.Length - 1; i >= 0; i--)
        {
            SemanticAttachment edge = attachments[i];
            reversedAttachments.Add(new SemanticAttachment(edge.LogicalId, edge.PartB, edge.PartA, edge.Behavior));
        }
        SemanticClusterPlan reordered = SemanticClusterCompiler.Compile(reversedParts, reversedAttachments);
        Check(Signature(plan) == Signature(reordered), "input order or endpoint orientation changed the compiled plan");
        foreach (SemanticPart part in parts)
            Check(plan.ClusterByPartId[part.LogicalId] == reordered.ClusterByPartId[part.LogicalId],
                "input order or edge orientation changed cluster identity for " + part.LogicalId);
        Check(plan.SplitSeams.Count == reordered.SplitSeams.Count, "input order changed seam count");
        for (int i = 0; i < plan.SplitSeams.Count; i++)
            Check(plan.SplitSeams[i].AttachmentId == reordered.SplitSeams[i].AttachmentId
                && plan.SplitSeams[i].Reasons == reordered.SplitSeams[i].Reasons,
                "input order changed seam semantics");

        var flexPlan = SemanticClusterCompiler.Compile(
            new[] { new SemanticPart("a", PartBoundaryRole.None), new SemanticPart("b", PartBoundaryRole.None) },
            new[] { new SemanticAttachment("flex", "a", "b", AttachmentBehavior.Compliant) });
        Check(flexPlan.Clusters.Count == 2 && flexPlan.SplitSeams[0].Reasons == ClusterSeamReason.Compliant,
            "explicit-flex edge was compiled rigidly");
        var articulatedPlan = SemanticClusterCompiler.Compile(
            new[] { new SemanticPart("arm", PartBoundaryRole.None), new SemanticPart("base", PartBoundaryRole.None) },
            new[] { new SemanticAttachment("hinge", "base", "arm", AttachmentBehavior.Articulated) });
        Check(articulatedPlan.Clusters.Count == 2
            && articulatedPlan.SplitSeams[0].Reasons == ClusterSeamReason.Articulated,
            "articulated edge was compiled rigidly");
        var embeddedSeparator = SemanticClusterCompiler.Compile(
            new[] { new SemanticPart("a\nb", PartBoundaryRole.None) }, new SemanticAttachment[0]);
        var twoMembers = SemanticClusterCompiler.Compile(
            new[] { new SemanticPart("a", PartBoundaryRole.None), new SemanticPart("b", PartBoundaryRole.None) },
            new[] { new SemanticAttachment("rigid", "a", "b", AttachmentBehavior.Rigid) });
        Check(embeddedSeparator.Clusters[0].Id != twoMembers.Clusters[0].Id,
            "cluster ID framing confused one member with two members");

        Reject<ArgumentException>(() => SemanticClusterCompiler.Compile(
            new[] { new SemanticPart("same", PartBoundaryRole.None), new SemanticPart("same", PartBoundaryRole.None) },
            new SemanticAttachment[0]), "duplicate part IDs accepted");
        Reject<ArgumentException>(() => SemanticClusterCompiler.Compile(parts,
            new[] { new SemanticAttachment("foreign", "engine", "missing", AttachmentBehavior.Rigid) }),
            "foreign attachment endpoint accepted");
        Reject<ArgumentException>(() => SemanticClusterCompiler.Compile(parts,
            new[] { attachments[0], new SemanticAttachment("a4", "engine", "payload", AttachmentBehavior.Rigid) }),
            "duplicate attachment IDs accepted");
        Reject<ArgumentOutOfRangeException>(() => new SemanticPart("invalid", (PartBoundaryRole)128),
            "unknown boundary role accepted");
        Reject<ArgumentOutOfRangeException>(() => new SemanticAttachment("invalid", "a", "b", (AttachmentBehavior)128),
            "unknown attachment behavior accepted");

        Console.WriteLine("PASS " + checks + " semantic cluster compiler assertions");
        return 0;
    }
}
