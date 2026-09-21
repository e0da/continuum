using System;
using System.Threading;
using System.Text.Json;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
    static void Reject(Action action) { checks++; try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Ineligible capture accepted"); }
    static ShadowBody PhysicalBody()
    {
        return new ShadowBody { id = 0, nativeInstanceId = -42, mass = 4, constraints = 16, sleeping = true,
            position = new[] { 1.0, 2, 3 }, rotation = new[] { 0.0, 0, 0, 1 },
            velocity = new[] { 4.0, 5, 6 }, angularVelocity = new[] { .1, -.2, .3 },
            centerOfMass = new[] { .25, 0, 0 }, worldCenterOfMass = new[] { 1.25, 2, 3 },
            inertiaTensor = new[] { 2.0, 3, 4 }, inertiaTensorRotation = new[] { 0.0, 0, 0, 1 },
            force = new double[3], forceSource = ShadowPhysicalInput.SyntheticZeroForce,
            predictedPosition = new[] { 1.08, 2.1, 3.12 }, predictedVelocity = new[] { 4.0, 5, 6 } };
    }
    static ShadowReport PhysicalReport()
    {
        return new ShadowReport { accepted = 1, firstAcceptedTick = 7,
            samples = new[] { new ShadowSample { tick = 7, status = "accepted", stepSeconds = .02,
                analyticAvailable = true, referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame,
                rawKrakensbaneFrameVelocity = new[] { 100.0, -20, 3 }, physicsEpoch = 11, floatingOriginEventCount = 2 } },
            firstAcceptedBatch = new[] { PhysicalBody() } };
    }
    sealed class Gate : ISimulationBackend, IDisposable
    {
        public readonly ManualResetEventSlim entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim release = new ManualResetEventSlim();
        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancel)
        { entered.Set(); release.Wait(cancel); return new ConstantForceBackend().Compute(batch, cancel); }
        public void Dispose() { entered.Dispose(); release.Dispose(); }
    }
    static void Transition(string changedTopology, string changedFrame, bool ready, bool returnToOriginal)
    {
        var epoch = new ShadowEpoch(); epoch.Observe("vessel/parts/parents/bodies", "scene/frame-1", true);
        var stamp = epoch.CaptureStamp();
        using (var gate = new Gate())
        using (var worker = new SimulationWorker(gate))
        {
            var batch = SimulationBatch.FromColumns(stamp, .02, new[] { 0 }, new[] { 2.0 },
                new[] { new Vec(1, 2, 3) }, new[] { new Vec(4, 5, 6) }, new[] { new Vec() });
            Check(worker.TrySubmit(batch) == SubmitStatus.Accepted, "submit");
            Check(gate.entered.Wait(5000), "worker entry");
            epoch.Observe(changedTopology, changedFrame, ready);
            if (returnToOriginal) epoch.Observe("vessel/parts/parents/bodies", "scene/frame-1", true);
            gate.release.Set();
            SimulationBatch result; ResultStatus status;
            var until = DateTime.UtcNow.AddSeconds(5);
            do { status = worker.TryTake(epoch.Expected(stamp), out result); if (status != ResultStatus.Pending) break; Thread.Yield(); } while (DateTime.UtcNow < until);
            Check(status == ResultStatus.Stale, "transition did not reject stale result");
            Check(result == null, "stale body payload escaped");
        }
    }
    static void Main()
    {
        var epoch = new ShadowEpoch(); Reject(() => epoch.CaptureStamp());
        epoch.Observe("a", "f", true); var a = epoch.CaptureStamp();
        epoch.Observe("a", "f", true); var unchanged = epoch.Expected(a);
        Check(a.Tick == unchanged.Tick && a.TopologyGeneration == unchanged.TopologyGeneration && a.FrameGeneration == unchanged.FrameGeneration, "unchanged observation invalidated");
        Check(epoch.CaptureStamp().Tick > a.Tick, "tick not monotonic");
        Transition("different-vessel", "scene/frame-1", true, false);
        Transition("same-vessel-different-parent", "scene/frame-1", true, false);
        Transition("vessel/parts/parents/bodies", "new-physics-epoch", true, false);
        Transition("vessel/parts/parents/bodies", "origin-shift", true, false);
        Transition("vessel/parts/parents/bodies", "scene/frame-1", false, false);
        Transition("vessel/parts/parents/bodies", "scene/frame-1", false, true);
        Transition("different-vessel", "scene/frame-1", true, true);
        epoch.Observe("a", "f", false); Reject(() => epoch.CaptureStamp());
        var input = SimulationBatch.FromColumns(new WorkStamp(1, 1, 1), .02, new[] { 0, 1 }, new[] { 3.0, 4.0 },
            new[] { new Vec(1, 2, 3), new Vec(-10, -20, -30) }, new[] { new Vec(4, 5, 6), new Vec(-4, -5, -6) }, new Vec[2]);
        using (var worker = new SimulationWorker(new ConstantForceBackend()))
        {
            Check(worker.TrySubmit(input) == SubmitStatus.Accepted, "ready fixture submission");
            SimulationBatch output; ResultStatus state;
            var until = DateTime.UtcNow.AddSeconds(5);
            do { state = worker.TryTake(input.Stamp, out output); if (state != ResultStatus.Pending) break; Thread.Yield(); } while (DateTime.UtcNow < until);
            Check(state == ResultStatus.Ready && output != null, "unchanged context did not return result");
            Check(output.GetPosition(1).X == -10.08 && output.GetVelocity(1).Y == -5, "native-valued zero-force prediction");
            var report = new ShadowReport { accepted = 1, firstAcceptedTick = input.Stamp.Tick,
                samples = new[] { new ShadowSample { tick = input.Stamp.Tick, status = "accepted", stepSeconds = input.StepSeconds, analyticAvailable = true,
                    referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame, rawKrakensbaneFrameVelocity = new double[3] } },
                firstAcceptedBatch = new[] { new ShadowBody { id = 1, nativeInstanceId = -42, mass = input.GetMass(1), constraints = 0, sleeping = false,
                    position = new[] { -10.0, -20, -30 }, rotation = new[] { 0.0, 0, 0, 1 },
                    velocity = new[] { -4.0, -5, -6 }, angularVelocity = new double[3], centerOfMass = new double[3],
                    worldCenterOfMass = new[] { -10.0, -20, -30 }, inertiaTensor = new[] { 1.0, 1, 1 },
                    inertiaTensorRotation = new[] { 0.0, 0, 0, 1 }, force = new double[3], forceSource = ShadowPhysicalInput.SyntheticZeroForce,
                    predictedPosition = new[] { output.GetPosition(1).X, output.GetPosition(1).Y, output.GetPosition(1).Z }, predictedVelocity = new[] { -4.0, -5, -6 } } } };
            ShadowPhysicalInput.Validate(report);
            using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
            {
                var root = json.RootElement;
                Check(root.GetProperty("schema").GetString() == "ksp-continuum-flight-shadow/v1", "schema missing");
                Check(root.GetProperty("physicalInputSchema").GetString() == "ksp-continuum-rigidbody-input/v1", "physical schema missing");
                Check(root.GetProperty("aggregateForceStatus").GetString() == "unavailable-not-captured", "force availability missing");
                Check(root.GetProperty("samples")[0].GetProperty("analyticAvailable").GetBoolean(), "availability flag lost");
                Check(root.GetProperty("samples")[0].GetProperty("referenceFrame").GetString() == "unity-world-at-capture", "frame identity missing");
                var body = root.GetProperty("firstAcceptedBatch")[0];
                Check(body.GetProperty("nativeInstanceId").GetInt32() == -42, "signed native ID lost");
                Check(body.GetProperty("predictedPosition")[0].GetDouble() == -10.08, "nested prediction missing");
                Check(body.GetProperty("force").GetArrayLength() == 3 && body.GetProperty("force")[0].GetDouble() == 0, "zero-force witness missing");
                Check(body.GetProperty("rotation")[3].GetDouble() == 1, "rotation missing");
                Check(body.GetProperty("inertiaTensor")[0].GetDouble() == 1, "inertia tensor missing");
                Check(body.GetProperty("forceSource").GetString() == "synthetic-zero-not-native-measurement", "force provenance missing");
                Check(body.GetProperty("constraints").GetInt32() == 0 && !body.GetProperty("sleeping").GetBoolean(), "solver flags missing");
            }
        }
        var physical = PhysicalReport(); ShadowPhysicalInput.Validate(physical); Check(true, "valid physical snapshot rejected");
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].rotation = new[] { -.5, .5, -.5, .5 };
        physical.firstAcceptedBatch[0].inertiaTensor = new[] { 0.0, 3, 4 }; ShadowPhysicalInput.Validate(physical);
        Check(true, "normalized signed quaternion or zero inertia rejected");
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].rotation = new double[4]; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].rotation = new[] { 0.0, 0, 0, 1.01 }; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].inertiaTensorRotation[0] = double.MaxValue; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].angularVelocity[1] = double.NaN; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].inertiaTensor[2] = -1; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].inertiaTensor[2] = double.PositiveInfinity; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].constraints = -1; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].force[0] = .001; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch[0].forceSource = "measured"; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.samples[0].rawKrakensbaneFrameVelocity = new double[2]; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.samples[0].status = "stale-discarded"; Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport(); physical.firstAcceptedBatch = new ShadowBody[0]; Reject(() => ShadowPhysicalInput.Validate(physical));
        ShadowPhysicalInput.Validate(new ShadowReport { status = "unavailable", reason = "No eligible state." }); Check(true, "empty unavailable report rejected");
        Console.WriteLine("PASS " + checks + " shadow lifecycle assertions");
    }
}
