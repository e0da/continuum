using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using KspContinuum;

static class Program
{
    static int checks;

    static void Check(bool ok, string reason)
    {
        checks++;
        if (!ok)
            throw new Exception(reason);
    }

    static void Reject(Action action)
    {
        checks++;
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception("Ineligible capture accepted");
    }

    static ShadowBody PhysicalBody()
    {
        return new ShadowBody
        {
            id = 0,
            nativeInstanceId = -42,
            mass = 4,
            constraints = 16,
            sleeping = true,
            position = new[] { 1.0, 2, 3 },
            rotation = new[] { 0.0, 0, 0, 1 },
            velocity = new[] { 4.0, 5, 6 },
            angularVelocity = new[] { .1, -.2, .3 },
            centerOfMass = new[] { .25, 0, 0 },
            worldCenterOfMass = new[] { 1.25, 2, 3 },
            inertiaTensor = new[] { 2.0, 3, 4 },
            inertiaTensorRotation = new[] { 0.0, 0, 0, 1 },
            force = new double[3],
            forceSource = ShadowPhysicalInput.SyntheticZeroForce,
            predictedPosition = new[] { 1.08, 2.1, 3.12 },
            predictedVelocity = new[] { 4.0, 5, 6 },
        };
    }

    static ShadowReport PhysicalReport()
    {
        return new ShadowReport
        {
            accepted = 1,
            firstAcceptedTick = 7,
            samples = new[]
            {
                new ShadowSample
                {
                    tick = 7,
                    status = "accepted",
                    stepSeconds = .02,
                    analyticAvailable = true,
                    referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame,
                    rawKrakensbaneFrameVelocity = new[] { 100.0, -20, 3 },
                    physicsEpoch = 11,
                    floatingOriginEventCount = 2,
                },
            },
            firstAcceptedBatch = new[] { PhysicalBody() },
        };
    }

    static StructuralLimit Limit(double value = 1)
    {
        return new StructuralLimit { limit = value, bounciness = .1, contactDistance = .01 };
    }

    static StructuralSpring Spring()
    {
        return new StructuralSpring { spring = 10, damper = 2 };
    }

    static StructuralDrive Drive()
    {
        return new StructuralDrive { positionSpring = 11, positionDamper = 3, maximumForce = 100, maximumForceStatus = "finite" };
    }

    static StructuralConfigurableJoint Configurable()
    {
        return new StructuralConfigurableJoint {
            autoConfigureConnectedAnchor = false, configuredInWorldSpace = true, swapBodies = false,
            xMotion = "Limited", yMotion = "Locked", zMotion = "Free",
            angularXMotion = "Limited", angularYMotion = "Locked", angularZMotion = "Free",
            rotationDriveMode = "Slerp", projectionMode = "PositionAndRotation",
            projectionDistance = .1, projectionAngle = 2,
            targetPosition = new[] { .1, .2, .3 }, targetVelocity = new[] { .4, .5, .6 },
            targetRotation = new[] { 0.0, 0, 0, 1 }, targetAngularVelocity = new[] { .7, .8, .9 },
            linearLimit = Limit(), lowAngularXLimit = Limit(-20), highAngularXLimit = Limit(30),
            angularYLimit = Limit(40), angularZLimit = Limit(50),
            linearLimitSpring = Spring(), angularXLimitSpring = Spring(), angularYZLimitSpring = Spring(),
            xDrive = Drive(), yDrive = Drive(), zDrive = Drive(), angularXDrive = Drive(),
            angularYZDrive = Drive(), slerpDrive = Drive(),
        };
    }

    sealed class Gate : ISimulationBackend, IDisposable
    {
        public readonly ManualResetEventSlim entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim release = new ManualResetEventSlim();

        public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancel)
        {
            entered.Set();
            release.Wait(cancel);
            return new ConstantForceBackend().Compute(batch, cancel);
        }

        public void Dispose()
        {
            entered.Dispose();
            release.Dispose();
        }
    }

    static void Transition(
        string changedTopology,
        string changedFrame,
        bool ready,
        bool returnToOriginal
    )
    {
        var epoch = new ShadowEpoch();
        epoch.Observe("vessel/parts/parents/bodies", "scene/frame-1", true);
        var stamp = epoch.CaptureStamp();
        using (var gate = new Gate())
        using (var worker = new SimulationWorker(gate))
        {
            var batch = SimulationBatch.FromColumns(
                stamp,
                .02,
                new[] { 0 },
                new[] { 2.0 },
                new[] { new Vec(1, 2, 3) },
                new[] { new Vec(4, 5, 6) },
                new[] { new Vec() }
            );
            Check(worker.TrySubmit(batch) == SubmitStatus.Accepted, "submit");
            Check(gate.entered.Wait(5000), "worker entry");
            epoch.Observe(changedTopology, changedFrame, ready);
            if (returnToOriginal)
                epoch.Observe("vessel/parts/parents/bodies", "scene/frame-1", true);
            gate.release.Set();
            SimulationBatch result;
            ResultStatus status;
            var until = DateTime.UtcNow.AddSeconds(5);
            do
            {
                status = worker.TryTake(epoch.Expected(stamp), out result);
                if (status != ResultStatus.Pending)
                    break;
                Thread.Yield();
            } while (DateTime.UtcNow < until);
            Check(status == ResultStatus.Stale, "transition did not reject stale result");
            Check(result == null, "stale body payload escaped");
        }
    }

    static ShadowReport comparisonFixture;

    static void ComparisonTests()
    {
        var input = SimulationBatch.FromColumns(
            new WorkStamp(1, 1, 1),
            .02,
            new[] { 0, 1 },
            new[] { 1.0, 1.0 },
            new[] { new Vec(1, 0, 0), new Vec(2, 0, 0) },
            new[] { new Vec(), new Vec() },
            new Vec[2]
        );
        var sample = new ShadowSample
        {
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .02,
            bodies = 2,
        };
        var comparison = new ShadowComparison();
        comparison.Attach(sample, input, "topology", "frame");
        Check(comparison.IsPending, "accepted prediction waits for observed boundary");
        Check(
            !comparison.Observe(3, "topology", "frame", true, .02, 2, 7, 0, null, null, null),
            "same boundary cannot compare"
        );
        Check(
            comparison.Observe(
                4,
                "topology",
                "frame",
                true,
                .02,
                2.02,
                8,
                1,
                new[] { new Vec(4, 0, 0), new Vec(2, 4, 0) },
                new[] { new Vec(0, 0, 5), new Vec() },
                new[] { 1.0, 2, 3 }
            ),
            "next boundary compares"
        );
        Check(
            sample.observedComparisonAvailable && sample.comparedBodies == 2,
            "observed comparison available"
        );
        Check(
            sample.observedPositionMaxMeters == 4
                && System.Math.Abs(sample.observedPositionRmsMeters - System.Math.Sqrt(12.5))
                    < 1e-12,
            "position max/rms"
        );
        Check(
            sample.observedVelocityMaxMetersPerSecond == 5
                && System.Math.Abs(
                    sample.observedVelocityRmsMetersPerSecond - System.Math.Sqrt(12.5)
                ) < 1e-12,
            "velocity max/rms"
        );
        sample.tick = 1;
        sample.status = "accepted";
        sample.analyticAvailable = true;
        sample.rawKrakensbaneFrameVelocity = new double[3];
        sample.topologyGeneration = sample.frameGeneration = 1;
        sample.vesselId = "00000000-0000-0000-0000-000000000001";
        sample.body = "Mun";
        sample.situation = "LANDED";
        sample.parts = 2;
        sample.captureUnityFrame = sample.collectUnityFrame = 7;
        sample.warpRate = 1;
        sample.universalTime = 100;
        var first = PhysicalBody();
        var second = PhysicalBody();
        first.id = 0;
        second.id = 1;
        second.nativeInstanceId = -43;
        first.position = first.predictedPosition = new[] { 1.0, 0, 0 };
        second.position = second.predictedPosition = new[] { 2.0, 0, 0 };
        first.velocity = first.predictedVelocity = new double[3];
        second.velocity = second.predictedVelocity = new double[3];
        foreach (var body in new[] { first, second })
        {
            body.mass = 1;
            body.constraints = 0;
            body.sleeping = false;
            body.rotation = body.inertiaTensorRotation = new[] { 0.0, 0, 0, 1 };
            body.angularVelocity = body.centerOfMass = new double[3];
            body.worldCenterOfMass = body.position;
            body.inertiaTensor = new[] { 1.0, 1, 1 };
        }
        comparisonFixture = new ShadowReport
        {
            evidence = "portable-helper-fixture",
            status = "complete",
            submitted = 1,
            accepted = 1,
            compared = 1,
            physicsEpochs = 4,
            originEvents = 1,
            unity = "portable-fixture",
            ksp = "portable-fixture",
            plugin = "portable-fixture",
            startedUtc = "2026-09-21T00:00:00Z",
            firstAcceptedTick = 1,
            firstAcceptedBatch = new[] { first, second },
            samples = new[] { sample },
        };
        for (int mode = 0; mode < 9; mode++)
        {
            var rejected = new ShadowSample
            {
                bodies = 2,
                physicsEpoch = 3,
                captureFixedTimeSeconds = mode == 8 ? 1e8 : 2,
                stepSeconds = .02,
            };
            comparison.Attach(rejected, input, "topology", "frame");
            var positions = new[] { new Vec(), new Vec() };
            var velocities = new[] { new Vec(), new Vec() };
            if (mode == 6)
                velocities[1] = new Vec(double.NaN, 0, 0);
            Check(
                comparison.Observe(
                    mode == 2 ? 5 : 4,
                    mode == 0 ? "changed" : "topology",
                    mode == 3 ? "shifted" : "frame",
                    mode != 1,
                    mode == 4 ? .01 : .02,
                    mode == 5 ? 2
                        : mode == 8 ? 1e8 + .02
                        : 2.02,
                    8,
                    0,
                    mode == 7 ? new Vec[1] : positions,
                    velocities,
                    new double[3]
                ),
                "comparison refusal resolves " + mode
            );
            Check(
                !rejected.observedComparisonAvailable
                    && rejected.comparisonStatus.StartsWith("skipped-"),
                "invalid context produces no residual " + mode
            );
            Check(
                rejected.comparedBodies == 0 && rejected.observedPositionMaxMeters == 0,
                "no partial residual " + mode
            );
        }
        var cancelled = new ShadowSample
        {
            bodies = 2,
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .02,
        };
        comparison.Attach(cancelled, input, "topology", "frame");
        Check(
            comparison.Cancel("on-interrupted")
                && !comparison.IsPending
                && cancelled.comparisonStatus == "skipped-on-interrupted",
            "teardown resolves pending comparison"
        );
        Check(!comparison.Cancel("again"), "cancel idempotent");
        var large = new ShadowSample
        {
            bodies = 2,
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .02,
        };
        comparison.Attach(large, input, "topology", "frame");
        comparison.Observe(
            4,
            "topology",
            "frame",
            true,
            .02,
            2.02,
            8,
            0,
            new[] { new Vec(1e200, 0, 0), new Vec(1e200, 0, 0) },
            new Vec[2],
            new double[3]
        );
        Check(
            large.observedComparisonAvailable && large.observedPositionRmsMeters == 1e200,
            "scaled RMS avoids square overflow"
        );
    }

    static ShadowReport gravityFixture;

    static void GravityTests()
    {
        Check(
            KrakensbaneFramePersistence.PredictFrameVelocityDelta(new Vec(-2, 3, 0)).X == 2,
            "frame persistence reverses prior correction"
        );
        bool badCorrection = false;
        try
        {
            KrakensbaneFramePersistence.PredictFrameVelocityDelta(new Vec(double.NaN, 0, 0));
        }
        catch (ArgumentException)
        {
            badCorrection = true;
        }
        Check(badCorrection, "nonfinite frame correction rejected");
        var source = SimulationBatch.FromColumns(
            new WorkStamp(1, 1, 1),
            .2,
            new[] { 0, 1 },
            new[] { 1.0, 100.0 },
            new[] { new Vec(10, 0, 0), new Vec(0, 20, 0) },
            new Vec[2],
            new Vec[2]
        );
        Vec mean;
        var predicted = CentralGravityShadow.Predict(source, new Vec(), 100, out mean);
        Check(
            System.Math.Abs(predicted.GetVelocity(0).X + .2) < 1e-14,
            "central acceleration changes velocity"
        );
        Check(
            System.Math.Abs(predicted.GetPosition(0).X - 9.98) < 1e-14,
            "frozen central acceleration position"
        );
        Check(
            System.Math.Abs(predicted.GetVelocity(1).Y + .05) < 1e-14,
            "inverse square acceleration per body"
        );
        Check(mean.X == -.5 && mean.Y == -.125, "mean captured gravity telemetry");
        Check(
            source.GetVelocity(0).X == 0 && source.GetPosition(0).X == 10,
            "baseline input remains immutable"
        );
        Check(
            CentralGravityShadow.Acceleration(new Vec(1000010, 0, 0), new Vec(1000000, 0, 0), 100).X
                == -1,
            "translation invariant central field"
        );
        foreach (double bad in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try
            {
                CentralGravityShadow.Predict(source, new Vec(), bad, out mean);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check(rejected, "invalid mu rejected");
        }
        bool singular = false;
        try
        {
            CentralGravityShadow.Acceleration(new Vec(), new Vec(), 100);
        }
        catch (ArgumentException)
        {
            singular = true;
        }
        Check(singular, "singular radius rejected");
        bool nonfinite = false;
        try
        {
            CentralGravityShadow.PredictCapturedAccelerations(
                source,
                new[] { new Vec(), new Vec(double.NaN, 0, 0) },
                out mean
            );
        }
        catch (ArgumentException)
        {
            nonfinite = true;
        }
        Check(nonfinite, "nonfinite native acceleration rejected");
        var one = SimulationBatch.FromColumns(
            new WorkStamp(1, 1, 1),
            .2,
            new[] { 0 },
            new[] { 1.0 },
            new[] { new Vec(10, 0, 0) },
            new Vec[1],
            new Vec[1]
        );
        var gravity = CentralGravityShadow.Predict(one, new Vec(), 100, out mean);
        var sample = new ShadowSample
        {
            tick = 1,
            status = "accepted",
            analyticAvailable = true,
            bodies = 1,
            parts = 1,
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .2,
            rawKrakensbaneFrameVelocity = new double[3],
            rawKrakensbaneLastCorrection = new[] { .2, 0, 0 },
            predictedKrakensbaneFrameVelocityDelta = new[] { -.2, 0, 0 },
            vesselId = "00000000-0000-0000-0000-000000000001",
            body = "synthetic-central-body",
            situation = "FLYING",
            topologyGeneration = 1,
            frameGeneration = 1,
            captureUnityFrame = 7,
            collectUnityFrame = 7,
            warpRate = 1,
            gravityModelStatus = "captured-frozen-acceleration",
            gravityAccelerationSource = "analytic-fixture",
            gravityMu = 100,
            gravityOrbitalMu = 100,
            gravityCenterUnityWorld = new double[3],
            gravityMeanAcceleration = new[] { -1.0, 0, 0 },
        };
        var baseline = new ShadowComparison();
        baseline.Attach(sample, one, "t", "f");
        var paired = new CentralGravityComparison();
        paired.Attach(sample, gravity, one, new Vec(-.2, 0, 0), "t", "f");
        var observedPosition = new[] { new Vec(10, 0, 0) };
        var observedVelocity = new Vec[1];
        var endFrame = new[] { -.2, 0.0, 0.0 };
        baseline.Observe(
            4,
            "t",
            "f",
            true,
            .2,
            2.2,
            8,
            1,
            observedPosition,
            observedVelocity,
            endFrame
        );
        paired.Observe(
            4,
            "t",
            "f",
            true,
            .2,
            2.2,
            8,
            1,
            observedPosition,
            observedVelocity,
            endFrame
        );
        Check(
            sample.observedVelocityRmsMetersPerSecond == 0
                && sample.gravityVelocityRmsMetersPerSecond == .2,
            "raw central model can lose to zero"
        );
        Check(
            sample.gravityVelocityRmsDeltaFromZero == .2
                && !sample.gravityVelocityRmsRatioToZero.HasValue,
            "negative result retained, zero denominator not divided"
        );
        Check(
            sample.gravityFrameAdjustedVelocityAvailable
                && sample.zeroFrameAdjustedVelocityAvailable,
            "paired adjusted diagnostics available"
        );
        Check(
            sample.gravityFrameAdjustedVelocityRmsMetersPerSecond == 0
                && sample.zeroFrameAdjustedVelocityRmsMetersPerSecond == .2,
            "subtract frame delta sign and matched baseline"
        );
        Check(
            sample.gravityFrameAdjustedVelocityRmsDeltaFromZero == -.2,
            "adjusted A/B uses same frame correction"
        );
        Check(
            sample.gravityEndpointFrameVelocityDelta[0] == -.2,
            "observed future frame delta retained"
        );
        Check(
            sample.gravityPredictedFrameVelocityAvailable
                && sample.zeroPredictedFrameVelocityAvailable
                && sample.gravityPredictedFrameVelocityRmsMetersPerSecond == 0
                && sample.frameVelocityDeltaPredictionErrorMetersPerSecond == 0,
            "independent persistence prediction matches synthetic endpoint"
        );
        var miss = new ShadowSample
        {
            bodies = 1,
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .2,
            rawKrakensbaneFrameVelocity = new double[3],
        };
        baseline = new ShadowComparison();
        baseline.Attach(miss, one, "t", "f");
        paired.Attach(miss, gravity, one, new Vec(), "t", "f");
        baseline.Observe(
            4,
            "t",
            "f",
            true,
            .2,
            2.2,
            8,
            1,
            observedPosition,
            observedVelocity,
            endFrame
        );
        paired.Observe(
            4,
            "t",
            "f",
            true,
            .2,
            2.2,
            8,
            1,
            observedPosition,
            observedVelocity,
            endFrame
        );
        Check(
            miss.frameVelocityDeltaPredictionErrorMetersPerSecond == .2
                && miss.gravityPredictedFrameVelocityAvailable
                && miss.gravityPredictedFrameVelocityRmsMetersPerSecond == .2,
            "persistence miss remains a finite result"
        );
        var body = PhysicalBody();
        body.mass = 1;
        body.constraints = 0;
        body.sleeping = false;
        body.position = body.predictedPosition = body.worldCenterOfMass = new[] { 10.0, 0, 0 };
        body.velocity =
            body.predictedVelocity =
            body.angularVelocity =
            body.centerOfMass =
                new double[3];
        gravityFixture = new ShadowReport
        {
            evidence = "portable-helper-fixture",
            status = "complete",
            submitted = 1,
            accepted = 1,
            compared = 1,
            firstAcceptedTick = 1,
            physicsEpochs = 4,
            originEvents = 1,
            firstAcceptedBatch = new[] { body },
            samples = new[] { sample },
        };
        var skipped = new ShadowSample
        {
            bodies = 1,
            physicsEpoch = 3,
            captureFixedTimeSeconds = 2,
            stepSeconds = .2,
        };
        paired.Attach(skipped, gravity, one, new Vec(), "t", "f");
        paired.Observe(
            5,
            "t",
            "f",
            true,
            .2,
            2.4,
            8,
            1,
            observedPosition,
            observedVelocity,
            endFrame
        );
        Check(
            !skipped.gravityComparisonAvailable
                && skipped.gravityComparisonStatus == "skipped-missed-boundary",
            "gravity shares refusal gate"
        );
        paired.Attach(skipped, gravity, one, new Vec(), "t", "f");
        paired.Cancel("on-interrupted");
        Check(
            skipped.gravityComparisonStatus == "skipped-on-interrupted",
            "gravity cancellation releases state"
        );
    }

    static void Main(string[] args)
    {
        ComparisonTests();
        GravityTests();
        var epoch = new ShadowEpoch();
        Reject(() => epoch.CaptureStamp());
        epoch.Observe("a", "f", true);
        var a = epoch.CaptureStamp();
        epoch.Observe("a", "f", true);
        var unchanged = epoch.Expected(a);
        Check(
            a.Tick == unchanged.Tick
                && a.TopologyGeneration == unchanged.TopologyGeneration
                && a.FrameGeneration == unchanged.FrameGeneration,
            "unchanged observation invalidated"
        );
        Check(epoch.CaptureStamp().Tick > a.Tick, "tick not monotonic");
        Transition("different-vessel", "scene/frame-1", true, false);
        Transition("same-vessel-different-parent", "scene/frame-1", true, false);
        Transition("vessel/parts/parents/bodies", "new-physics-epoch", true, false);
        Transition("vessel/parts/parents/bodies", "origin-shift", true, false);
        Transition("vessel/parts/parents/bodies", "scene/frame-1", false, false);
        Transition("vessel/parts/parents/bodies", "scene/frame-1", false, true);
        Transition("different-vessel", "scene/frame-1", true, true);
        epoch.Observe("a", "f", false);
        Reject(() => epoch.CaptureStamp());
        var input = SimulationBatch.FromColumns(
            new WorkStamp(1, 1, 1),
            .02,
            new[] { 0, 1 },
            new[] { 3.0, 4.0 },
            new[] { new Vec(1, 2, 3), new Vec(-10, -20, -30) },
            new[] { new Vec(4, 5, 6), new Vec(-4, -5, -6) },
            new Vec[2]
        );
        using (var worker = new SimulationWorker(new ConstantForceBackend()))
        {
            Check(worker.TrySubmit(input) == SubmitStatus.Accepted, "ready fixture submission");
            SimulationBatch output;
            ResultStatus state;
            var until = DateTime.UtcNow.AddSeconds(5);
            do
            {
                state = worker.TryTake(input.Stamp, out output);
                if (state != ResultStatus.Pending)
                    break;
                Thread.Yield();
            } while (DateTime.UtcNow < until);
            Check(
                state == ResultStatus.Ready && output != null,
                "unchanged context did not return result"
            );
            Check(
                output.GetPosition(1).X == -10.08 && output.GetVelocity(1).Y == -5,
                "native-valued zero-force prediction"
            );
            var report = new ShadowReport
            {
                accepted = 1,
                firstAcceptedTick = input.Stamp.Tick,
                samples = new[]
                {
                    new ShadowSample
                    {
                        tick = input.Stamp.Tick,
                        status = "accepted",
                        stepSeconds = input.StepSeconds,
                        analyticAvailable = true,
                        referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame,
                        rawKrakensbaneFrameVelocity = new double[3],
                    },
                },
                firstAcceptedBatch = new[]
                {
                    new ShadowBody
                    {
                        id = 1,
                        nativeInstanceId = -42,
                        mass = input.GetMass(1),
                        constraints = 0,
                        sleeping = false,
                        position = new[] { -10.0, -20, -30 },
                        rotation = new[] { 0.0, 0, 0, 1 },
                        velocity = new[] { -4.0, -5, -6 },
                        angularVelocity = new double[3],
                        centerOfMass = new double[3],
                        worldCenterOfMass = new[] { -10.0, -20, -30 },
                        inertiaTensor = new[] { 1.0, 1, 1 },
                        inertiaTensorRotation = new[] { 0.0, 0, 0, 1 },
                        force = new double[3],
                        forceSource = ShadowPhysicalInput.SyntheticZeroForce,
                        predictedPosition = new[]
                        {
                            output.GetPosition(1).X,
                            output.GetPosition(1).Y,
                            output.GetPosition(1).Z,
                        },
                        predictedVelocity = new[] { -4.0, -5, -6 },
                    },
                },
            };
            ShadowPhysicalInput.Validate(report);
            using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
            {
                var root = json.RootElement;
                Check(
                    root.GetProperty("schema").GetString() == "ksp-continuum-flight-shadow/v2",
                    "schema missing"
                );
                Check(
                    root.GetProperty("physicalInputSchema").GetString()
                        == "ksp-continuum-rigidbody-input/v1",
                    "physical schema missing"
                );
                Check(
                    root.GetProperty("aggregateForceStatus").GetString()
                        == "unavailable-not-captured",
                    "force availability missing"
                );
                Check(
                    root.GetProperty("samples")[0].GetProperty("analyticAvailable").GetBoolean(),
                    "availability flag lost"
                );
                Check(
                    root.GetProperty("samples")[0].GetProperty("referenceFrame").GetString()
                        == "unity-world-at-capture",
                    "frame identity missing"
                );
                var body = root.GetProperty("firstAcceptedBatch")[0];
                Check(
                    body.GetProperty("nativeInstanceId").GetInt32() == -42,
                    "signed native ID lost"
                );
                Check(
                    body.GetProperty("predictedPosition")[0].GetDouble() == -10.08,
                    "nested prediction missing"
                );
                Check(
                    body.GetProperty("force").GetArrayLength() == 3
                        && body.GetProperty("force")[0].GetDouble() == 0,
                    "zero-force witness missing"
                );
                Check(body.GetProperty("rotation")[3].GetDouble() == 1, "rotation missing");
                Check(
                    body.GetProperty("inertiaTensor")[0].GetDouble() == 1,
                    "inertia tensor missing"
                );
                Check(
                    body.GetProperty("forceSource").GetString()
                        == "synthetic-zero-not-native-measurement",
                    "force provenance missing"
                );
                Check(
                    body.GetProperty("constraints").GetInt32() == 0
                        && !body.GetProperty("sleeping").GetBoolean(),
                    "solver flags missing"
                );
            }
        }
        var physical = PhysicalReport();
        ShadowPhysicalInput.Validate(physical);
        Check(true, "valid physical snapshot rejected");
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].rotation = new[] { -.5, .5, -.5, .5 };
        physical.firstAcceptedBatch[0].inertiaTensor = new[] { 0.0, 3, 4 };
        ShadowPhysicalInput.Validate(physical);
        Check(true, "normalized signed quaternion or zero inertia rejected");
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].rotation = new double[4];
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].rotation = new[] { 0.0, 0, 0, 1.01 };
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].inertiaTensorRotation[0] = double.MaxValue;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].angularVelocity[1] = double.NaN;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].inertiaTensor[2] = -1;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].inertiaTensor[2] = double.PositiveInfinity;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].constraints = -1;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].force[0] = .001;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch[0].forceSource = "measured";
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.samples[0].rawKrakensbaneFrameVelocity = new double[2];
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.samples[0].status = "stale-discarded";
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch = new ShadowBody[0];
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        var linked = PhysicalBody();
        linked.id = 1;
        linked.nativeInstanceId = -43;
        physical.firstAcceptedBatch = new[] { physical.firstAcceptedBatch[0], linked };
        physical.firstAcceptedLinks = new[] { new StructuralLink {
            nativeInstanceId = -99, bodyId = 0, connectedBodyId = 1, jointType = "UnityEngine.ConfigurableJoint",
            anchor = new double[3], connectedAnchor = new[] { 1.0, 0, 0 }, axis = new[] { 1.0, 0, 0 },
            secondaryAxis = new[] { 0.0, 1, 0 }, breakForce = double.PositiveInfinity,
            breakTorque = 100, breakForceStatus = "unbreakable", breakTorqueStatus = "finite",
            massScale = 1, connectedMassScale = 1,
            configurable = Configurable(),
        } };
        physical.firstAcceptedLinks[0].breakForce = 0;
        ShadowPhysicalInput.Validate(physical);
        Check(true, "valid structural link rejected");
        Check(ReportJson.Encode(physical).Contains("\"breakForceStatus\":\"unbreakable\""), "structural link did not serialize");
        Check(StructuralThreshold.Status(double.PositiveInfinity) == "unbreakable"
            && StructuralThreshold.Value(double.PositiveInfinity) == 0, "unbreakable threshold canonicalization");
        Check(ReportJson.Encode(physical).Contains("\"projectionMode\":\"PositionAndRotation\""),
            "configurable joint payload did not serialize");
        physical.firstAcceptedLinks[0].configurable.slerpDrive.maximumForceStatus = "unbreakable";
        physical.firstAcceptedLinks[0].configurable.slerpDrive.maximumForce = 0;
        ShadowPhysicalInput.Validate(physical);
        physical.firstAcceptedLinks[0].configurable.targetRotation = new double[4];
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch = new[] { physical.firstAcceptedBatch[0], linked };
        physical.firstAcceptedLinks = new[] { new StructuralLink {
            nativeInstanceId = -99, bodyId = 0, connectedBodyId = 1, jointType = "UnityEngine.ConfigurableJoint",
            anchor = new double[3], connectedAnchor = new double[3], axis = new[] { 1.0, 0, 0 },
            secondaryAxis = new[] { 0.0, 1, 0 }, breakForce = 0, breakTorque = 0,
            breakForceStatus = "unbreakable", breakTorqueStatus = "unbreakable", massScale = 1,
            connectedMassScale = 1, configurable = Configurable(),
        } };
        physical.firstAcceptedLinks[0].configurable.projectionMode = "PositionOnly";
        ShadowPhysicalInput.Validate(physical);
        physical.firstAcceptedLinks[0].configurable.projectionMode = "bogus";
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical = PhysicalReport();
        physical.firstAcceptedBatch = new[] { physical.firstAcceptedBatch[0], linked };
        physical.firstAcceptedLinks = new[] { new StructuralLink {
            nativeInstanceId = -99, bodyId = 0, connectedBodyId = 1, jointType = "UnityEngine.FixedJoint",
            anchor = new double[3], connectedAnchor = new double[3], axis = new[] { 1.0, 0, 0 },
            secondaryAxis = new double[3], breakForce = 0, breakTorque = 0,
            breakForceStatus = "unbreakable", breakTorqueStatus = "unbreakable", massScale = 1,
            connectedMassScale = 1, configurable = Configurable(),
        } };
        Reject(() => ShadowPhysicalInput.Validate(physical));
        physical.firstAcceptedLinks[0].configurable = null;
        ShadowPhysicalInput.Validate(physical);
        physical.firstAcceptedLinks[0].connectedBodyId = 0;
        Reject(() => ShadowPhysicalInput.Validate(physical));
        ShadowPhysicalInput.Validate(
            new ShadowReport { status = "unavailable", reason = "No eligible state." }
        );
        Check(true, "empty unavailable report rejected");
        if (args.Length > 0)
        {
            if (
                args.Length != 2
                || args[0] != "--export-fixture" && args[0] != "--export-gravity-fixture"
            )
                throw new ArgumentException("Use --export-fixture NEW_PATH");
            var selectedFixture =
                args[0] == "--export-gravity-fixture" ? gravityFixture : comparisonFixture;
            ShadowPhysicalInput.Validate(selectedFixture);
            using (var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = Encoding.UTF8.GetBytes(ReportJson.Encode(selectedFixture));
                output.Write(bytes, 0, bytes.Length);
            }
        }
        Console.WriteLine("PASS " + checks + " shadow lifecycle assertions");
    }
}
