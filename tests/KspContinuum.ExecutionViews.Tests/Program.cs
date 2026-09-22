using System;
using KspContinuum;

static class Program
{
    static int checks;
    static readonly ExecutionComponent Moving = ExecutionComponent.Motion | ExecutionComponent.Mass | ExecutionComponent.Force;
    static WorkStamp Stamp(long tick = 1) { return new WorkStamp(tick, 2, 3); }
    static ExecutionEntity Entity(int slot, ExecutionComponent extra = ExecutionComponent.None)
    {
        return new ExecutionEntity(new EntityKey(slot, 4), Moving | extra, slot + 1,
            new Vec(slot, slot + 1, slot + 2), new Vec(1, 2, 3), new Vec(2, 0, -2));
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject(Action action, string message)
    {
        try { action(); throw new Exception(message); } catch (ArgumentException) { checks++; }
    }
    static int Main()
    {
        var source = new ExecutionSnapshot(Stamp(), 8, new[] {
            Entity(9), Entity(2, ExecutionComponent.Contact), Entity(40), Entity(7, ExecutionComponent.Structural)
        });
        var query = new ExecutionQuery("free-flight", Moving, ExecutionComponent.Contact, 4);
        MotionExecutionView view = ExecutionViewCompiler.CompileMotion(source, query);
        Check(view.Count == 3 && view.PaddedCount == 4 && view.LaneWidth == 4, "dense lane shape changed");
        Check(view.PositionX.Count == 4 && view.PositionX[0] == 9 && view.PositionX[1] == 40 && view.PositionX[2] == 7,
            "backend-facing contiguous position column changed");
        Check(view.GetKey(0).Slot == 9 && view.GetKey(1).Slot == 40 && view.GetKey(2).Slot == 7,
            "view confused dense lane order with stable identity");
        Check(view.GetPosition(1).X == 40 && view.GetMass(2) == 8, "SoA fields do not match selected identities");
        Check(source.Entities.Count == 4 && source.Entities[1].Key.Slot == 2, "compilation rewrote canonical membership");

        var positions = new Vec[view.Count]; var velocities = new Vec[view.Count];
        for (int i = 0; i < view.Count; i++) { positions[i] = view.GetPosition(i) + new Vec(10, 0, 0); velocities[i] = view.GetVelocity(i); }
        var result = new MotionExecutionResult(view, positions, velocities);
        positions[0].X = 999;
        ExecutionPublication published = ExecutionPublisher.Publish(source, result);
        Check(published.Status == ExecutionPublishStatus.Published && published.Snapshot.Revision == 9, "valid result not published atomically");
        Check(published.Snapshot.Entities[0].Position.X == 19 && published.Snapshot.Entities[1].Position.X == 2 &&
            published.Snapshot.Entities[2].Position.X == 50 && published.Snapshot.Entities[3].Position.X == 17,
            "publication changed excluded identity or lost dense-to-canonical mapping");
        Check(source.Entities[0].Position.X == 9, "publication mutated before-image");

        var staleRevision = new ExecutionSnapshot(Stamp(), 9, source.Entities);
        Check(ExecutionPublisher.Publish(staleRevision, result).Status == ExecutionPublishStatus.Stale, "revision change accepted stale result");
        var staleFrame = new ExecutionSnapshot(new WorkStamp(1, 2, 99), 8, source.Entities);
        Check(ExecutionPublisher.Publish(staleFrame, result).Status == ExecutionPublishStatus.Stale, "frame change accepted stale result");
        var replaced = new[] { new ExecutionEntity(new EntityKey(9, 5), Moving, 10, new Vec(), new Vec(), new Vec()), source.Entities[1], source.Entities[2], source.Entities[3] };
        Check(ExecutionPublisher.Publish(new ExecutionSnapshot(Stamp(), 8, replaced), result).Status == ExecutionPublishStatus.Stale,
            "recycled slot accepted an old generation");
        Check(ExecutionPublisher.Publish(null, result).Status == ExecutionPublishStatus.Invalid, "null current accepted");

        Reject(() => new ExecutionQuery("x", Moving, ExecutionComponent.Motion, 4), "conflicting query accepted");
        Reject(() => new ExecutionQuery("x", Moving, ExecutionComponent.None, 3), "unsupported lane width accepted");
        Reject(() => ExecutionViewCompiler.CompileMotion(source,
            new ExecutionQuery("bad", ExecutionComponent.Motion, ExecutionComponent.None, 1)), "incomplete motion view accepted");
        Reject(() => new MotionExecutionResult(view, new Vec[1], new Vec[1]), "partial result accepted");
        Reject(() => new ExecutionSnapshot(Stamp(), 0, new[] { Entity(1), Entity(1) }), "duplicate stable identity accepted");

        var workload = new ExecutionWorkload(1024, 4, WorkRegularity.Regular, DataResidency.Host,
            DependencyShape.Independent, 100, DeterminismRequirement.ExactOrder);
        var scalar = new ExecutionBackendReport("scalar", true, 4096, new[] { 1, 4, 8, 16 },
            WorkRegularity.Irregular, DependencyShape.SharedConstraints, DeterminismRequirement.ExactOrder,
            DataResidency.Host, 5, 30, 0);
        var simd = new ExecutionBackendReport("simd", false, 4096, new[] { 4, 8 },
            WorkRegularity.Conditional, DependencyShape.DisjointIslands, DeterminismRequirement.ExactOrder,
            DataResidency.Host, 8, 8, 0);
        var device = new ExecutionBackendReport("gpu", false, 1000000, new[] { 4, 8, 16 },
            WorkRegularity.Regular, DependencyShape.Independent, DeterminismRequirement.Repeatable,
            DataResidency.Device, 2, 1, 200);
        ExecutionRoute route = ExecutionBackendPolicy.Select(workload, new[] { device, scalar, simd });
        Check(route.BackendId == "simd" && route.MeetsLatencyBudget && !route.UsedScalarFallback,
            "policy ignored transfer or determinism when selecting backend");
        var irregular = new ExecutionWorkload(12, 1, WorkRegularity.Irregular, DataResidency.Host,
            DependencyShape.SharedConstraints, 1, DeterminismRequirement.ExactOrder);
        route = ExecutionBackendPolicy.Select(irregular, new[] { simd, scalar, device });
        Check(route.BackendId == "scalar" && !route.MeetsLatencyBudget, "scalar reference did not remain deterministic fallback");
        var tied = new ExecutionBackendReport("aaa", false, 4096, new[] { 4 }, WorkRegularity.Regular,
            DependencyShape.Independent, DeterminismRequirement.ExactOrder, DataResidency.Host, 5, 30, 0);
        route = ExecutionBackendPolicy.Select(workload, new[] { scalar, tied });
        Check(route.BackendId == "aaa", "equal-cost backend selection was not ID-deterministic");
        try { ExecutionBackendPolicy.Select(workload, new[] { simd }); throw new Exception("policy accepted no scalar fallback"); }
        catch (InvalidOperationException) { checks++; }
        Console.WriteLine("PASS " + checks + " execution-view assertions");
        return 0;
    }
}
