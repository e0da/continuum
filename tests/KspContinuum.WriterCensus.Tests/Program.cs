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
                new WriterCensusBody { id = "1:a", relativePosition = new Vec(firstX, 0, 0), normalizedVelocity = new Vec(10, 0, 0), orientation = new[] { 0d, 0, 0, 1 }, angularVelocity = new Vec() },
                new WriterCensusBody { id = "2:b", relativePosition = new Vec(secondX, 0, 0), normalizedVelocity = new Vec(secondVelocity, 0, 0), orientation = new[] { 0d, 0, rotationZ, rotationW }, angularVelocity = new Vec(0, angularVelocity, 0) }
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
        Check(census.Report.measurementScope.Contains("does not identify the writer"));
        using (var json = JsonDocument.Parse(ReportJson.Encode(census.Report)))
            Check(json.RootElement.GetProperty("intervals").GetArrayLength() == 1);

        // A common world translation has already cancelled in the adapter's reference-body frame.
        census = Run(Snapshot(), Snapshot());
        row = census.Report.intervals[0];
        Check(row.status == "observed" && row.reason == "no-observed-change");
        Check(row.changedPositions == 0 && row.maximumPositionDelta == 0);

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
        Check(census.Report.intervals[0].status == "invalid" && census.Report.intervals[0].reason == "floating-origin-changed");
        census = Run(Snapshot(), Snapshot(frameVelocity: 11));
        Check(census.Report.intervals[0].reason == "reference-frame-velocity-changed");
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

        Console.WriteLine("WriterCensus: " + checks + " assertions passed.");
    }
}
