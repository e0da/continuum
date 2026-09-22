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

        var capturedParts = new[]
        {
            new StructuralPartFact("engine", 1, PartBoundaryRole.None),
            new StructuralPartFact("tank-a", 2, PartBoundaryRole.None),
            new StructuralPartFact("tank-b", 3, PartBoundaryRole.None),
            new StructuralPartFact("tank-c", 4, PartBoundaryRole.None),
            new StructuralPartFact("command", 4, PartBoundaryRole.None),
            new StructuralPartFact("adapter", 4, PartBoundaryRole.None),
            new StructuralPartFact("decoupler", 5, PartBoundaryRole.None),
            new StructuralPartFact("payload-tank", 6, PartBoundaryRole.None),
            new StructuralPartFact("payload-probe", 6, PartBoundaryRole.None),
            new StructuralPartFact("wheel-left", 7, PartBoundaryRole.IndependentlySimulated),
            new StructuralPartFact("wheel-right", 8, PartBoundaryRole.IndependentlySimulated),
            new StructuralPartFact("docking-port", 9, PartBoundaryRole.ExternalInterface),
            new StructuralPartFact("science", 10, (PartBoundaryRole?)null),
            new StructuralPartFact("solar-left", 6, PartBoundaryRole.None),
            new StructuralPartFact("solar-right", 6, PartBoundaryRole.None),
            new StructuralPartFact("antenna", 6, PartBoundaryRole.None),
            new StructuralPartFact("battery", 6, PartBoundaryRole.None)
        };
        var capturedAttachments = new[]
        {
            new StructuralAttachmentFact("j01", "engine", "tank-a", 101, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j02", "tank-a", "tank-b", 102, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j03", "tank-b", "tank-c", 103, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-command", "tank-c", "command", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-adapter", "command", "adapter", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j04", "command", "decoupler", 104, AttachmentBehavior.Detachable),
            new StructuralAttachmentFact("j05", "decoupler", "payload-tank", 105, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-probe", "payload-tank", "payload-probe", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j06", "payload-tank", "wheel-left", 106, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j07", "payload-tank", "wheel-right", 107, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j08", "payload-probe", "docking-port", 108, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("j09", "payload-probe", "science", 109, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-solar-left", "payload-tank", "solar-left", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-solar-right", "payload-tank", "solar-right", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-antenna", "payload-probe", "antenna", null, AttachmentBehavior.Rigid),
            new StructuralAttachmentFact("logical-battery", "payload-probe", "battery", null, AttachmentBehavior.Rigid)
        };
        StructuralClusterCandidate candidate = StructuralClusterCandidateCompiler.Compile(
            new StructuralCensusResult(10, 9, 17), capturedParts, capturedAttachments);
        Check(candidate.MappedSourceBodies == 10 && candidate.MappedSourceJoints == 9,
            "representative capture did not account for every observed body and joint");
        Check(candidate.Plan.Clusters.Count == 6 && candidate.ProjectedBodies == 6 && candidate.BodyReduction == 4,
            "17-part/10-body capture did not compile to the expected six-body candidate");
        Check(candidate.Plan.SplitSeams.Count == 5 && candidate.ProjectedJoints == 5,
            "candidate did not preserve five observed semantic seams");
        Check(candidate.Abstentions.Count == 1
            && candidate.Abstentions[0] == "part:science:unknown-boundary-semantics",
            "unknown part semantics were not retained as an explicit abstention");
        ClusterSplitSeam unknownSeam = null;
        foreach (ClusterSplitSeam seam in candidate.Plan.SplitSeams)
            if (seam.AttachmentId == "j09") unknownSeam = seam;
        Check(unknownSeam != null && (unknownSeam.Reasons & ClusterSeamReason.UnknownSemantics) != 0,
            "unknown science-part semantics were silently compiled as rigid");

        var incomplete = StructuralClusterCandidateCompiler.Compile(new StructuralCensusResult(11, 10, 17),
            capturedParts, capturedAttachments);
        Check(incomplete.ProjectedBodies == 7 && incomplete.ProjectedJoints == 6
            && incomplete.Abstentions.Count == 3,
            "unmapped census bodies or joints were discarded instead of retained");
        var unknownAttachment = StructuralClusterCandidateCompiler.Compile(new StructuralCensusResult(2, 1, 0),
            new[] { new StructuralPartFact("a", 1, PartBoundaryRole.None),
                new StructuralPartFact("b", 2, PartBoundaryRole.None) },
            new[] { new StructuralAttachmentFact("edge", "a", "b", 1, null) });
        Check(unknownAttachment.ProjectedBodies == 2 && unknownAttachment.ProjectedJoints == 1
            && unknownAttachment.Abstentions.Count == 1
            && unknownAttachment.Plan.SplitSeams[0].Reasons == ClusterSeamReason.UnknownSemantics,
            "unknown attachment behavior was silently compiled as rigid");
        Reject<InvalidOperationException>(() => StructuralClusterCandidateCompiler.Compile(
            new StructuralCensusResult(1, 0, 0),
            new[] { new StructuralPartFact("a", 1, PartBoundaryRole.None),
                new StructuralPartFact("b", 1, PartBoundaryRole.IndependentlySimulated) },
            new[] { new StructuralAttachmentFact("edge", "a", "b", null, AttachmentBehavior.Rigid) }),
            "one captured body was allowed to cross a proposed seam");

        var captureContext = new StructuralVesselCaptureContext("11111111-1111-1111-1111-111111111111",
            "FLIGHT", 123, 45, .9, 123456.75, "continuum-qualification", "checkpoint-007");
        var censusReport = StructuralVesselCensus.Build(captureContext, new StructuralCensusResult(3, 2, 7),
            new[] {
                new StructuralPartObservation("z", 3, null, null),
                new StructuralPartObservation("a", 1, null, null),
                new StructuralPartObservation("middle", 2, null, null),
                new StructuralPartObservation("physicsless", null, null, "missing-part-rigidbody") },
            new[] {
                new StructuralAttachmentObservation("edge-z", "middle", "z", 22, null, null),
                new StructuralAttachmentObservation("edge-a", "a", "middle", 11, null, null),
                new StructuralAttachmentObservation("edge-omitted", "middle", "physicsless", null, null, null) });
        Check(censusReport.parts[0].logicalId == "a" && censusReport.parts[3].logicalId == "z"
            && censusReport.attachments[0].logicalId == "edge-a", "vessel census rows are not deterministically ordered");
        Check(censusReport.candidate.status == "compiled-read-only-candidate"
            && censusReport.candidate.mappedBodies == 3 && censusReport.candidate.mappedJoints == 2,
            "vessel census did not account for observed bodies and joints");
        Check(censusReport.candidate.projectedBodies == 3 && censusReport.candidate.bodyReduction == 0
            && censusReport.candidate.abstentions.Length == 5,
            "unknown live semantics were not retained as abstentions");
        Check(censusReport.context.vesselId == "11111111-1111-1111-1111-111111111111"
            && censusReport.context.scene == "FLIGHT" && censusReport.context.frame == 123
            && censusReport.context.physicsTick == 45 && censusReport.context.fixedTimeSeconds == .9
            && censusReport.context.universalTimeSeconds == 123456.75
            && censusReport.context.saveIdentity == "continuum-qualification"
            && censusReport.context.checkpointIdentity == "checkpoint-007",
            "vessel census lost durable capture attribution");
        Check(censusReport.attachments[1].omissionReason == "endpoint-without-native-body",
            "attachment with an unmapped endpoint was not explicitly omitted");
        string firstEncoding = ReportJson.Encode(censusReport);
        string secondEncoding = ReportJson.Encode(StructuralVesselCensus.Build(captureContext, new StructuralCensusResult(3, 2, 7),
            new[] { new StructuralPartObservation("physicsless", null, null, "missing-part-rigidbody"),
                new StructuralPartObservation("middle", 2, null, null), new StructuralPartObservation("a", 1, null, null),
                new StructuralPartObservation("z", 3, null, null) },
            new[] { new StructuralAttachmentObservation("edge-omitted", "middle", "physicsless", null, null, null),
                new StructuralAttachmentObservation("edge-a", "a", "middle", 11, null, null),
                new StructuralAttachmentObservation("edge-z", "middle", "z", 22, null, null) }));
        Check(firstEncoding == secondEncoding, "vessel census encoding changed with source enumeration order");

        var crossingReport = StructuralVesselCensus.Build(captureContext, new StructuralCensusResult(1, 0, 0),
            new[] { new StructuralPartObservation("a", 1, null, null),
                new StructuralPartObservation("b", 1, null, null) },
            new[] { new StructuralAttachmentObservation("edge", "a", "b", null, null, null) });
        Check(crossingReport.candidate.status == "rejected-read-only-candidate"
            && crossingReport.candidate.rejectionReason.Contains("crosses proposed semantic seams"),
            "same-body semantic conflict was not retained as a rejected observation");
        Reject<ArgumentException>(() => new StructuralVesselCaptureContext(Guid.Empty.ToString(), "FLIGHT", 1,
            1, .02, 0, "save", null), "empty vessel GUID was accepted");
        Reject<ArgumentException>(() => new StructuralVesselCaptureContext(Guid.NewGuid().ToString(), "FLIGHT", 1,
            1, .02, 0, new string('s', 129), null), "unbounded save identity was accepted");

        Console.WriteLine("PASS " + checks + " semantic cluster compiler assertions");
        return 0;
    }
}
