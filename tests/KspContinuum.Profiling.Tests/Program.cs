using System;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int assertions;
    static void Check(bool value) { assertions++; if (!value) throw new Exception("Profiling assertion " + assertions); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static void Near(double actual, double expected) { Check(Math.Abs(actual - expected) < 1e-10); }
    static void Main()
    {
        var distribution = ProfilingSummary.Distribution(new double[] { 10, 1, 4, 2, 3 });
        Check(distribution != null && distribution.count == 5);
        Near(distribution.minimum, 1); Near(distribution.maximum, 10); Near(distribution.mean, 4);
        Near(distribution.p50, 3); Near(distribution.p95, 8.8); Near(distribution.p99, 9.76);
        Check(ProfilingSummary.Distribution(new double[0]) == null);
        var single = ProfilingSummary.Distribution(new double[] { 0 });
        Near(single.p99, 0); Check(single.count == 1);
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { double.NaN })));
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { double.PositiveInfinity })));
        Check(Reject(() => ProfilingSummary.Distribution(new double[] { -1 })));

        var marker = new MarkerReport { name = "Example", nanoseconds = new long[] { 1000000, 0, 0, 3000000 },
            blocks = new int[] { 2, 0, 0, 1 }, available = new bool[] { true, true, false, true } };
        var summary = ProfilingSummary.Marker(marker);
        Check(summary.availableFrames == 3 && summary.unavailableFrames == 1 && summary.observedFrames == 2 && summary.zeroBlockFrames == 1);
        Check(summary.totalBlocks == 3); Near(summary.observedMilliseconds.mean, 2); Near(summary.observedMilliseconds.p50, 2);
        var absent = ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 0 }, blocks = new int[] { 0 }, available = new bool[] { false } });
        Check(absent.observedMilliseconds == null && absent.unavailableFrames == 1 && absent.zeroBlockFrames == 0);
        var inactive = ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 0 }, blocks = new int[] { 0 }, available = new bool[] { true } });
        Check(inactive.observedMilliseconds == null && inactive.unavailableFrames == 0 && inactive.zeroBlockFrames == 1);
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { -1 }, blocks = new int[] { 1 }, available = new bool[] { true } })));
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[] { 1 }, blocks = new int[] { 0 }, available = new bool[] { true } })));
        Check(Reject(() => ProfilingSummary.Marker(new MarkerReport { nanoseconds = new long[0], blocks = new int[] { 1 }, available = new bool[] { true } })));

        marker.summary = summary;
        var report = new ProbeReport { markers = new[] { marker }, frames = new[] { new ProfileFrame {
            contextFrame = 10, markerFrame = 10, observedFrame = 11, contextAligned = true, wallMilliseconds = 16.7,
            vesselStatus = "unavailable-no-active-vessel", parts = -1, vesselId = null } }, wallIntervals = distribution };
        using (JsonDocument json = JsonDocument.Parse(ReportJson.Encode(report)))
        {
            Check(json.RootElement.GetProperty("schema").GetString() == "ksp-continuum-markers/v2");
            Check(json.RootElement.GetProperty("frames")[0].GetProperty("parts").GetInt32() == -1);
            Check(json.RootElement.GetProperty("frames")[0].GetProperty("vesselId").ValueKind == JsonValueKind.Null);
            Check(!json.RootElement.GetProperty("markers")[0].GetProperty("available")[2].GetBoolean());
            Near(json.RootElement.GetProperty("markers")[0].GetProperty("summary").GetProperty("observedMilliseconds").GetProperty("mean").GetDouble(), 2);
        }
        var interrupted = new ProbeReport { requestedFrames = 3, frames = new[] {
            new ProfileFrame { wallMilliseconds = 10, contextAligned = true },
            new ProfileFrame { wallMilliseconds = 30, contextAligned = false },
            new ProfileFrame { wallMilliseconds = 9000, contextAligned = true } },
            markers = new[] { new MarkerReport { name = "Partial", available = new[] { true, false, false },
                nanoseconds = new long[] { 2000000, 0, 0 }, blocks = new[] { 1, 0, 0 } } } };
        ProfilingSummary.Finish(interrupted, 2);
        Check(interrupted.frames.Length == 2 && interrupted.markers[0].nanoseconds.Length == 2 && interrupted.completedFrames == 2);
        Check(interrupted.wallIntervals.count == 2 && interrupted.contextMisalignedFrames == 1);
        Near(interrupted.wallIntervals.mean, 20);
        Check(interrupted.markers[0].status == "observed" && interrupted.markers[0].summary.unavailableFrames == 1);
        Check(Reject(() => ProfilingSummary.Finish(interrupted, 3)));
        var empty = new ProbeReport { requestedFrames = 2, frames = new ProfileFrame[2], markers = new[] {
            new MarkerReport { name = "Missing", available = new bool[2], nanoseconds = new long[2], blocks = new int[2] } } };
        ProfilingSummary.Finish(empty, 0);
        Check(empty.frames.Length == 0 && empty.wallIntervals == null && empty.markers[0].status == "unavailable");
        var stockProfile = ComparableProfile(10, 8, 6, 4);
        var candidateProfile = ComparableProfile(8, 6, 3, 3);
        var expected = new ProfileComparisonExpectation { substitutionId = "cluster-a", substitutionStatus = "verified",
            stockRigidbodies = 128, stockJoints = 127, candidateRigidbodies = 128, candidateJoints = 127 };
        var comparison = ProfileComparisonSummary.Compare(stockProfile, candidateProfile, expected);
        Near(comparison.wallIntervals.meanSpeedupPercent, 20);
        Near(comparison.fixedUpdate.meanSpeedupPercent, 25);
        Near(comparison.physicsFixedUpdate.meanSpeedupPercent, 50);
        Near(comparison.behaviourFixedUpdate.meanSpeedupPercent, 25);
        Check(comparison.stockFrames == 1 && comparison.candidateFrames == 1);
        candidateProfile.frames[0].joints = 8;
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, expected)));
        candidateProfile.frames[0].rigidbodies = 1;
        candidateProfile.frames[0].joints = 0;
        var structuralExpected = new ProfileComparisonExpectation { substitutionId = "cluster-a", substitutionStatus = "verified",
            stockRigidbodies = 128, stockJoints = 127, candidateRigidbodies = 1, candidateJoints = 0 };
        var structural = ProfileComparisonSummary.Compare(stockProfile, candidateProfile, structuralExpected);
        Check(structural.status == "comparable" && structural.substitutionId == "cluster-a");
        candidateProfile.playerLoop.scopes[0].droppedSamples = 1;
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, structuralExpected)));
        Check(Reject(() => ProfileComparisonSummary.Compare(stockProfile, candidateProfile, null)));
        var forceContext = new ForceObservationContext("11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", "FLIGHT", "frame-1", 10, -42, 1, 1, 1, 0, 100, 1, .02, new Vec());
        var forcePart = new ForcePartObservation(1, 0, -8, -9, new Vec(1, 2, 3), new Vec(), new Vec(4, 5, 6),
            new[] { new ForceAtPositionObservation(new Vec(7, 8, 9), new Vec(5, 5, 6), new Vec(1, 0, 0)) });
        var forceReport = new ForceObservationReport { batches = new[] { new ForceObservationBatch(forceContext, new[] { forcePart }) } };
        using (JsonDocument json = JsonDocument.Parse(ReportJson.Encode(forceReport)))
        {
            var batch = json.RootElement.GetProperty("batches")[0];
            Check(batch.GetProperty("parts")[0].GetProperty("force").GetProperty("Y").GetDouble() == 2);
            Check(batch.GetProperty("parts")[0].GetProperty("forces")[0].GetProperty("worldLeverArm").GetProperty("X").GetDouble() == 1);
            Check(json.RootElement.GetProperty("gravity").GetString() == "unavailable-not-observed");
        }
        Console.WriteLine("Profiling: " + assertions + " assertions passed.");
    }

    static ProbeReport ComparableProfile(double wall, double fixedMs, double physicsMs, double behaviourMs)
    {
        return new ProbeReport {
            status = "complete", requestedFrames = 1, completedFrames = 1, contextMisalignedFrames = 0,
            unity = "2019.4", ksp = "1.12.5", platform = "OSXPlayer", processor = "CPU", processorCount = 8,
            graphicsDevice = "GPU", targetFrameRate = -1, vSyncCount = 0,
            frames = new[] { new ProfileFrame { contextAligned = true, scene = "FLIGHT", body = "Kerbin", situation = "ORBITING",
                vesselId = "vessel", parts = 128, rigidbodies = 128, joints = 127, colliders = 128, loadedVessels = 1,
                screenWidth = 1920, screenHeight = 1080, fixedDeltaSeconds = .02, timeScale = 1, warpRate = 1,
                throttleCommand = 0, packed = false, loaded = true, paused = false, wallMilliseconds = wall } },
            wallIntervals = Dist(wall), markers = new MarkerReport[0],
            playerLoop = new LoopTimingReport { status = "observed", integrityStatus = "verified-at-boundaries", cleanupStatus = "removed-owned-hooks",
                scopes = new[] { Scope("UnityEngine.PlayerLoop.FixedUpdate", fixedMs),
                    Scope("UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate", physicsMs),
                    Scope("UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate", behaviourMs) } }
        };
    }
    static LoopTimingScope Scope(string name, double value) { return new LoopTimingScope { name = name, status = "observed", milliseconds = Dist(value), samples = new LoopTimingSample[1] }; }
    static ProfileDistribution Dist(double value) { return new ProfileDistribution { count = 1, minimum = value, maximum = value, mean = value, p50 = value, p95 = value, p99 = value }; }
}
