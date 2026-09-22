using System;
using System.Collections.Generic;
using System.Text.Json;
using KspContinuum;

static class Program
{
    const string Physics = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
    const string Scripts = "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate";
    static int checks;
    static void Check(bool value) { checks++; if (!value) throw new Exception("Writer census assertion " + checks); }
    static WriterCensusSnapshot Snapshot(string vessel = "v", string topology = "1:a|2:b", long origin = 0,
        double firstX = 0, double secondX = 2, double secondVelocity = 3, double frameVelocity = 10,
        double rotationZ = 0, double rotationW = 1, double angularVelocity = 0)
    {
        return new WriterCensusSnapshot { vesselId = vessel, topologyKey = topology, originGeneration = origin,
            frameVelocity = new Vec(frameVelocity, 0, 0), bodies = new[] {
                new WriterCensusBody { id = "1:a", relativePosition = new Vec(firstX, 0, 0), normalizedVelocity = new Vec(10, 0, 0), orientation = new[] { 0d, 0, 0, 1 }, angularVelocity = new Vec(), internalPosition = new Vec(), internalVelocity = new Vec(), internalAngularVelocity = new Vec(), internalOrientation = new[] { 0d, 0, 0, 1 } },
                new WriterCensusBody { id = "2:b", relativePosition = new Vec(secondX, 0, 0), normalizedVelocity = new Vec(secondVelocity, 0, 0), orientation = new[] { 0d, 0, rotationZ, rotationW }, angularVelocity = new Vec(0, angularVelocity, 0), internalPosition = new Vec(secondX, 0, 0), internalVelocity = new Vec(secondVelocity - 10, 0, 0), internalAngularVelocity = new Vec(0, angularVelocity, 0), internalOrientation = new[] { 0d, 0, rotationZ, rotationW } }
            } };
    }
    static WriterCensus Run(params WriterCensusSnapshot[] snapshots)
    {
        var queue = new Queue<WriterCensusSnapshot>(snapshots); var census = new WriterCensus(() => queue.Dequeue(), 8);
        census.Before(Physics, 4, .08); census.After(Physics, 4, .08); census.Finish(); return census;
    }

    static void Main()
    {
        var census = Run(Snapshot(), Snapshot(secondX: 2.25, secondVelocity: 4));
        var row = census.Report.intervals[0];
        Check(census.Report.status == "observed" && row.status == "observed");
        Check(row.reason == "state-changed-within-interval" && row.changedPositions == 1 && row.changedVelocities == 1);
        Check(row.maximumPositionDelta == .25 && row.maximumVelocityDelta == 1);
        Check(row.changedInternalPositions == 1 && row.changedInternalVelocities == 1);
        Check(row.maximumInternalPositionDelta == .25 && row.maximumInternalVelocityDelta == 1);
        Check(census.Report.measurementScope.Contains("does not identify the writer"));
        using (var json = JsonDocument.Parse(ReportJson.Encode(census.Report)))
            Check(json.RootElement.GetProperty("intervals").GetArrayLength() == 1);

        // A common world translation has already cancelled in the adapter's reference-body frame.
        census = Run(Snapshot(), Snapshot());
        row = census.Report.intervals[0];
        Check(row.status == "observed" && row.reason == "no-observed-change");
        Check(row.changedPositions == 0 && row.maximumPositionDelta == 0);
        Check(row.changedInternalPositions == 0 && row.maximumInternalPositionDelta == 0);

        // A rigid quarter turn remains visible in world measurements but cancels in the co-moving frame.
        var rigidTurn = Snapshot();
        rigidTurn.bodies[0].orientation = new[] { 0d, 0, Math.Sqrt(.5), Math.Sqrt(.5) };
        rigidTurn.bodies[1].relativePosition = new Vec(0, 2, 0);
        rigidTurn.bodies[1].orientation = new[] { 0d, 0, Math.Sqrt(.5), Math.Sqrt(.5) };
        rigidTurn.bodies[0].normalizedVelocity = new Vec(0, 10, 0);
        rigidTurn.bodies[1].normalizedVelocity = new Vec(0, 3, 0);
        rigidTurn.bodies[0].angularVelocity = new Vec(0, 0, 1);
        rigidTurn.bodies[1].angularVelocity = new Vec(0, 0, 1);
        census = Run(Snapshot(), rigidTurn); row = census.Report.intervals[0];
        Check(row.changedPositions == 1 && row.changedOrientations == 2 && row.changedVelocities == 2);
        Check(row.changedInternalPositions == 0 && row.changedInternalOrientations == 0 && row.changedInternalVelocities == 0 && row.changedInternalAngularVelocities == 0);

        // Moving one body relative to the reference is retained as internal deformation.
        var deformed = Snapshot(); deformed.bodies[1].relativePosition = new Vec(2.1, 0, 0);
        deformed.bodies[1].internalPosition = new Vec(2.1, 0, 0);
        census = Run(Snapshot(), deformed); row = census.Report.intervals[0];
        Check(row.changedInternalPositions == 1 && Math.Abs(row.maximumInternalPositionDelta - .1) < 1e-12);

        census = Run(Snapshot(), Snapshot(rotationZ: 1, rotationW: 0, angularVelocity: 2));
        row = census.Report.intervals[0];
        Check(row.changedOrientations == 1 && Math.Abs(row.maximumOrientationDeltaRadians - Math.PI) < 1e-12);
        Check(row.changedAngularVelocities == 1 && row.maximumAngularVelocityDelta == 2);
        census = Run(Snapshot(), Snapshot(rotationW: -1));
        Check(census.Report.intervals[0].reason == "no-observed-change" && census.Report.intervals[0].changedOrientations == 0);
        census = Run(Snapshot(), Snapshot(rotationW: 2));
        Check(census.Report.intervals[0].reason == "no-observed-change");
        census = Run(Snapshot(), Snapshot(rotationW: 0));
        Check(census.Report.intervals[0].status == "invalid" && census.Report.intervals[0].reason == "invalid-orientation");
        census = Run(Snapshot(), Snapshot(rotationW: double.NaN));
        Check(census.Report.intervals[0].reason == "invalid-orientation");

        census = Run(Snapshot(), Snapshot(origin: 1));
        Check(census.Report.intervals[0].status == "observed" && census.Report.intervals[0].originTransforms == 1);
        census = Run(Snapshot(), Snapshot(frameVelocity: 11));
        Check(census.Report.intervals[0].status == "observed" && census.Report.intervals[0].frameVelocityDelta == 1);
        census = Run(Snapshot(origin: 1), Snapshot(origin: 0));
        Check(census.Report.intervals[0].reason == "floating-origin-generation-regressed");
        census = Run(Snapshot(), Snapshot(vessel: "other"));
        Check(census.Report.intervals[0].reason == "active-vessel-changed");
        census = Run(Snapshot(), Snapshot(topology: "changed"));
        Check(census.Report.intervals[0].reason == "topology-changed");

        var after = Snapshot(); after.bodies = new[] { after.bodies[0] };
        census = Run(Snapshot(), after);
        Check(census.Report.intervals[0].reason == "body-membership-changed");
        after = Snapshot(); Array.Reverse(after.bodies); census = Run(Snapshot(), after);
        Check(census.Report.intervals[0].reason == "body-membership-changed");

        int calls = 0; census = new WriterCensus(() => { calls++; return Snapshot(); }, 1);
        census.Before("UnityEngine.PlayerLoop.Update", 0, 0); census.After("UnityEngine.PlayerLoop.Update", 0, 0);
        Check(calls == 0);
        census.Before(Physics, 1, .02); census.After(Physics, 1, .02);
        census.Before(Scripts, 1, .02); census.After(Scripts, 1, .02); census.Finish();
        Check(census.Report.intervals.Length == 1 && census.Report.droppedIntervals == 1);

        census = new WriterCensus(() => throw new InvalidOperationException(), 2);
        census.Before(Physics, 1, .02); census.Finish();
        Check(census.Report.status == "invalid" && census.Report.invalidIntervals == 1);
        Check(census.Report.intervals[0].reason == "capture-failed:InvalidOperationException");

        var canaryReport = new PhysicsSubstitutionCanaryReport();
        var canary = new PhysicsSubstitutionCanary(canaryReport);
        var admitted = Snapshot(); canary.Admit(admitted); canary.Installed(); Check(canary.Enter(Snapshot(origin: 8, frameVelocity: 11)));
        var unchanged = Run(Snapshot(), Snapshot()).Report;
        Check(canary.CandidateCallback(10)); canary.Restored("native-node-restored");
        Check(canary.Observe(Snapshot(origin: 8, frameVelocity: 11), Snapshot(origin: 8, frameVelocity: 11), unchanged));
        Check(canaryReport.status == "observed-bounded-native-skip" && canaryReport.candidateCallbacks == 1 && canaryReport.candidateFrame == 10);
        Check(canary.CandidateCallback(10) && canaryReport.candidateCallbacks == 2 && canaryReport.status == "observed-bounded-native-skip");
        Check(canaryReport.limitation.Contains("deliberately skipped") && canaryReport.skippedInterval.intervals.Length == 1);
        using (var json = JsonDocument.Parse(ReportJson.Encode(canaryReport)))
        {
            Check(json.RootElement.GetProperty("restorationStatus").GetString() == "native-node-restored");
            Check(json.RootElement.GetProperty("before").GetProperty("bodies").GetArrayLength() == 2);
            Check(json.RootElement.GetProperty("after").GetProperty("topologyKey").GetString() == "1:a|2:b");
        }
        Check(!canary.CandidateCallback(11) && canaryReport.reason == "candidate-callback-outside-bounded-frame");

        canaryReport = new PhysicsSubstitutionCanaryReport(); canary = new PhysicsSubstitutionCanary(canaryReport);
        canary.Admit(Snapshot()); canary.Installed(); Check(!canary.Enter(Snapshot(topology: "changed")));
        canary.Restored("native-node-restored"); Check(canaryReport.status == "invalid" && canaryReport.reason == "admitted-membership-changed-before-bracket");

        canaryReport = new PhysicsSubstitutionCanaryReport(); canary = new PhysicsSubstitutionCanary(canaryReport);
        canary.Admit(Snapshot()); canary.Installed(); Check(canary.Enter(Snapshot()));
        Check(canary.CandidateCallback(10)); canary.Restored("native-node-restored");
        Check(!canary.Observe(Snapshot(), Snapshot(frameVelocity: 11), unchanged));
        Check(canaryReport.reason == "context-changed-across-physics-bracket");

        canaryReport = new PhysicsSubstitutionCanaryReport(); canary = new PhysicsSubstitutionCanary(canaryReport);
        canary.Admit(Snapshot()); canary.Installed(); Check(canary.Enter(Snapshot()));
        Check(canary.CandidateCallback(10)); Check(canary.Observe(Snapshot(), Snapshot(), unchanged)); canary.Restored("cleanup-error");
        Check(canaryReport.status == "invalid" && canaryReport.reason == "native-node-restoration-failed");

        var dynamicsReport = new PhysicsDynamicsCanaryReport();
        var dynamics = new PhysicsDynamicsCanary(dynamicsReport);
        var dynamicsBefore = Snapshot(); var dynamicsAfter = Snapshot(secondVelocity: 3.1);
        var changed = Run(dynamicsBefore, dynamicsAfter).Report;
        dynamics.Admit(dynamicsBefore); dynamics.Installed(); Check(dynamics.Enter(dynamicsBefore));
        Check(dynamics.CandidateCallback(12));
        Check(dynamics.CandidateContext(dynamicsBefore));
        dynamics.Published(2, .02, new Vec(0, -9.81, 0), .1, .2, .3, 0, 0);
        dynamics.Restored("native-node-restored");
        Check(dynamics.Observe(dynamicsBefore, dynamicsAfter, changed));
        Check(dynamicsReport.status == "observed-bounded-dynamics" && dynamicsReport.publicationStatus == "verified-readback");
        Check(dynamicsReport.bodiesWritten == 2 && dynamicsReport.callbackMilliseconds == .3);
        Check(dynamics.CandidateCallback(12) && dynamicsReport.candidateCallbacks == 2);
        using (var json = JsonDocument.Parse(ReportJson.Encode(dynamicsReport)))
            Check(json.RootElement.GetProperty("schema").GetString() == "ksp-continuum-physics-dynamics-canary/v1");

        dynamicsReport = new PhysicsDynamicsCanaryReport(); dynamics = new PhysicsDynamicsCanary(dynamicsReport);
        dynamics.Admit(Snapshot()); dynamics.Installed(); Check(dynamics.Enter(Snapshot())); Check(dynamics.CandidateCallback(1));
        Check(dynamics.CandidateContext(Snapshot()));
        dynamics.Published(2, .02, new Vec(0, -9.81, 0), 0, 0, 0, 0, 0);
        dynamics.Restored("native-node-restored"); Check(!dynamics.Observe(Snapshot(), Snapshot(), unchanged));
        Check(dynamicsReport.reason == "candidate-produced-no-observable-dynamics");

        Console.WriteLine("WriterCensus: " + checks + " assertions passed.");
    }
}
