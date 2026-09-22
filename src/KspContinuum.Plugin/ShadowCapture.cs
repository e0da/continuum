using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace KspContinuum
{
    public sealed class ShadowCapture : IDisposable
    {
        static ShadowCapture owner;
        readonly ShadowEpoch epoch = new ShadowEpoch();
        readonly ShadowComparison comparison = new ShadowComparison();
        readonly CentralGravityComparison gravityComparison = new CentralGravityComparison();
        SimulationBatch pendingGravity;
        ShadowSample comparisonSample;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<ShadowSample> samples = new List<ShadowSample>();
        readonly ShadowReport report;
        readonly SimulationWorker worker;
        Action<ShadowReport> completion;
        SimulationBatch pending;
        ShadowSample pendingSample;
        ShadowBody[] pendingPhysical;
        StructuralLink[] pendingLinks;
        int pendingUnmappedJoints;
        double activeStart = -1,
            submittedAt;
        long physicsEpoch,
            originEvents,
            lastCaptureEpoch = -1;
        bool finished;
        public string Status { get; private set; }
        public bool IsRunning
        {
            get { return !finished; }
        }

        public ShadowCapture(Action<ShadowReport> complete)
        {
            if (complete == null)
                throw new ArgumentNullException("complete");
            if (owner != null)
                throw new InvalidOperationException("A flight shadow capture is already active.");
            report = new ShadowReport
            {
                unity = Application.unityVersion,
                ksp = Versioning.GetVersionString(),
                plugin = typeof(ShadowCapture).Assembly.GetName().Version.ToString(),
                startedUtc = DateTime.UtcNow.ToString("o"),
            };
            worker = new SimulationWorker(new ConstantForceBackend());
            completion = complete;
            owner = this;
            try
            {
                GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            }
            catch
            {
                worker.Dispose();
                owner = null;
                throw;
            }
            Status = "Shadow armed: waiting for a loaded, unpacked vessel at normal rate.";
        }

        void OnOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            originEvents++;
        }

        public void FixedBoundary()
        {
            physicsEpoch++;
        }

        public void Tick(bool allowCapture)
        {
            if (finished)
                return;
            try
            {
                Pump(allowCapture);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogException(error);
                Finish("failed", error.GetType().Name + ": " + error.Message);
            }
        }

        void Pump(bool allowCapture)
        {
            double auditStart = clock.Elapsed.TotalMilliseconds;
            Vessel vessel =
                HighLogic.LoadedSceneIsFlight && FlightGlobals.ready
                    ? FlightGlobals.ActiveVessel
                    : null;
            var bodies = new List<Rigidbody>();
            string topology = Topology(vessel, bodies);
            Vector3d frameVelocity = Krakensbane.GetFrameVelocity();
            Vector3d lastCorrection = Krakensbane.GetLastCorrection();
            string physicalFrame = HighLogic.LoadedScene.ToString();
            string frame =
                physicsEpoch
                + ":"
                + physicalFrame
                + ":"
                + originEvents
                + ":"
                + F(frameVelocity.x)
                + ":"
                + F(frameVelocity.y)
                + ":"
                + F(frameVelocity.z);
            bool eligible =
                vessel != null
                && vessel.loaded
                && !vessel.packed
                && !vessel.HoldPhysics
                && !FlightDriver.Pause
                && TimeWarp.CurrentRate == 1
                && Time.timeScale == 1
                && bodies.Count != 0;
            epoch.Observe(topology, frame, eligible);
            double auditMs = clock.Elapsed.TotalMilliseconds - auditStart;
            if (comparison.IsPending)
            {
                Vec[] observedPositions = null,
                    observedVelocities = null;
                if (eligible && physicsEpoch == comparison.ExpectedEpoch)
                {
                    observedPositions = new Vec[bodies.Count];
                    observedVelocities = new Vec[bodies.Count];
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        observedPositions[i] = V(bodies[i].position);
                        observedVelocities[i] = V(bodies[i].velocity);
                    }
                }
                if (
                    comparison.Observe(
                        physicsEpoch,
                        topology,
                        physicalFrame,
                        eligible,
                        Time.fixedDeltaTime,
                        Time.fixedTime,
                        Time.frameCount,
                        originEvents,
                        observedPositions,
                        observedVelocities,
                        A(frameVelocity)
                    )
                )
                {
                    double gravityCompareStart = clock.Elapsed.TotalMilliseconds;
                    gravityComparison.Observe(
                        physicsEpoch,
                        topology,
                        physicalFrame,
                        eligible,
                        Time.fixedDeltaTime,
                        Time.fixedTime,
                        Time.frameCount,
                        originEvents,
                        observedPositions,
                        observedVelocities,
                        A(frameVelocity)
                    );
                    comparisonSample.gravityComparisonMilliseconds =
                        clock.Elapsed.TotalMilliseconds - gravityCompareStart;
                    if (comparisonSample.observedComparisonAvailable)
                        report.compared++;
                    else
                        report.comparisonSkipped++;
                    comparisonSample = null;
                }
            }
            if (samples.Count >= report.requestedSamples && !comparison.IsPending)
            {
                Finish("complete", "Bounded sample count and comparison attempts reached.");
                return;
            }
            if (pending != null)
            {
                pendingSample.collectAuditMilliseconds += auditMs;
                double before = clock.Elapsed.TotalMilliseconds;
                SimulationBatch result;
                ResultStatus state = worker.TryTake(epoch.Expected(pending.Stamp), out result);
                pendingSample.collectMilliseconds += clock.Elapsed.TotalMilliseconds - before;
                if (state == ResultStatus.Ready || state == ResultStatus.Stale)
                {
                    pendingSample.status =
                        state == ResultStatus.Ready ? "accepted" : "stale-discarded";
                    pendingSample.collectUnityFrame = Time.frameCount;
                    pendingSample.handoffWallMilliseconds =
                        clock.Elapsed.TotalMilliseconds - submittedAt;
                    if (state == ResultStatus.Ready)
                        Accept(result, topology, physicalFrame);
                    else
                    {
                        report.stale++;
                        if (pendingGravity != null)
                            pendingSample.gravityComparisonStatus = "skipped-worker-stale";
                    }
                    samples.Add(pendingSample);
                    pending = null;
                    pendingSample = null;
                    pendingPhysical = null;
                    pendingLinks = null;
                    pendingUnmappedJoints = 0;
                    pendingGravity = null;
                    if (samples.Count >= report.requestedSamples && !comparison.IsPending)
                    {
                        Finish("complete", "Bounded sample count reached.");
                        return;
                    }
                }
                else if (
                    state == ResultStatus.Faulted
                    || state == ResultStatus.Disposed
                    || state == ResultStatus.Empty
                )
                    throw new InvalidOperationException(
                        "Shadow worker entered " + state,
                        worker.Fault
                    );
            }
            if (activeStart < 0 && eligible)
                activeStart = clock.Elapsed.TotalSeconds;
            if (activeStart < 0 && clock.Elapsed.TotalSeconds >= report.readyTimeoutSeconds)
            {
                Finish("unavailable", "No eligible flight state before ready timeout.");
                return;
            }
            if (
                activeStart >= 0
                && clock.Elapsed.TotalSeconds - activeStart >= report.activeTimeoutSeconds
            )
            {
                Finish("timeout", "Active capture wall-time bound reached.");
                return;
            }
            if (!eligible)
            {
                Status =
                    "Shadow waiting: paused, packed, loading, warped, or no dynamic part bodies.";
                return;
            }
            if (
                !allowCapture
                || pending != null
                || lastCaptureEpoch == physicsEpoch
                || samples.Count >= report.requestedSamples
            )
                return;
            double captureStart = clock.Elapsed.TotalMilliseconds;
            int count = bodies.Count;
            var ids = new int[count];
            var masses = new double[count];
            var positions = new Vec[count];
            var velocities = new Vec[count];
            var forces = new Vec[count];
            ShadowBody[] physical =
                report.firstAcceptedBatch.Length == 0 ? new ShadowBody[count] : null;
            for (int i = 0; i < count; i++)
            {
                Rigidbody body = bodies[i];
                Vector3 position = body.position,
                    velocity = body.velocity;
                float mass = body.mass;
                ids[i] = i;
                masses[i] = mass;
                positions[i] = V(position);
                velocities[i] = V(velocity);
                if (physical != null)
                    physical[i] = new ShadowBody
                    {
                        id = i,
                        nativeInstanceId = body.GetInstanceID(),
                        mass = mass,
                        constraints = (int)body.constraints,
                        sleeping = body.IsSleeping(),
                        position = A(position),
                        rotation = A(body.rotation),
                        velocity = A(velocity),
                        angularVelocity = A(body.angularVelocity),
                        centerOfMass = A(body.centerOfMass),
                        worldCenterOfMass = A(body.worldCenterOfMass),
                        inertiaTensor = A(body.inertiaTensor),
                        inertiaTensorRotation = A(body.inertiaTensorRotation),
                        force = new double[3],
                        forceSource = ShadowPhysicalInput.SyntheticZeroForce,
                    };
            }
            StructuralLink[] links = null;
            int unmappedJoints = 0;
            if (physical != null) links = CaptureLinks(vessel, bodies, out unmappedJoints);
            var batch = SimulationBatch.FromColumns(
                epoch.CaptureStamp(),
                Time.fixedDeltaTime,
                ids,
                masses,
                positions,
                velocities,
                forces
            );
            var sample = new ShadowSample
            {
                tick = batch.Stamp.Tick,
                topologyGeneration = batch.Stamp.TopologyGeneration,
                frameGeneration = batch.Stamp.FrameGeneration,
                vesselId = vessel.id.ToString("D"),
                body = vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                situation = vessel.situation.ToString(),
                parts = vessel.parts.Count,
                bodies = count,
                captureUnityFrame = Time.frameCount,
                universalTime = Planetarium.GetUniversalTime(),
                stepSeconds = batch.StepSeconds,
                packed = vessel.packed,
                warpRate = TimeWarp.CurrentRate,
                captureMilliseconds = auditMs + clock.Elapsed.TotalMilliseconds - captureStart,
                referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame,
                rawKrakensbaneFrameVelocity = A(frameVelocity),
                rawKrakensbaneLastCorrection = A(lastCorrection),
                physicsEpoch = physicsEpoch,
                floatingOriginEventCount = originEvents,
                captureFixedTimeSeconds = Time.fixedTime,
            };
            Vec predictedFrameDelta = KrakensbaneFramePersistence.PredictFrameVelocityDelta(
                new Vec(lastCorrection.x, lastCorrection.y, lastCorrection.z)
            );
            sample.predictedKrakensbaneFrameVelocityDelta = A(predictedFrameDelta);
            pendingGravity = null;
            double gravityStart = clock.Elapsed.TotalMilliseconds;
            if (vessel.mainBody == null)
                sample.gravityModelStatus = "unavailable-no-central-body";
            else
            {
                try
                {
                    Vector3d nativeCenter = vessel.mainBody.position;
                    Vec center = new Vec(nativeCenter.x, nativeCenter.y, nativeCenter.z),
                        meanAcceleration;
                    double mu = vessel.mainBody.gMagnitudeAtCenter;
                    AssemblyModel.Positive(mu);
                    AssemblyModel.Positive(vessel.mainBody.gravParameter);
                    var accelerations = new Vec[count];
                    for (int i = 0; i < count; i++)
                    {
                        var p = batch.GetPosition(i);
                        CentralGravityShadow.Acceleration(p, center, mu);
                        Vector3d acceleration = FlightGlobals.getGeeForceAtPosition(
                            new Vector3d(p.X, p.Y, p.Z),
                            vessel.mainBody
                        );
                        accelerations[i] = new Vec(acceleration.x, acceleration.y, acceleration.z);
                    }
                    pendingGravity = CentralGravityShadow.PredictCapturedAccelerations(
                        batch,
                        accelerations,
                        out meanAcceleration
                    );
                    sample.gravityOrbitalMu = vessel.mainBody.gravParameter;
                    sample.gravityAccelerationSource =
                        "FlightGlobals.getGeeForceAtPosition(position,mainBody)";
                    sample.gravityModelStatus = "captured-frozen-acceleration";
                    sample.gravityMu = mu;
                    sample.gravityCenterUnityWorld = A(center);
                    sample.gravityMeanAcceleration = A(meanAcceleration);
                }
                catch (ArgumentException)
                {
                    sample.gravityModelStatus = "unavailable-invalid-central-model";
                }
            }
            sample.gravityPredictionMilliseconds = clock.Elapsed.TotalMilliseconds - gravityStart;
            submittedAt = clock.Elapsed.TotalMilliseconds;
            SubmitStatus submission = worker.TrySubmit(batch);
            sample.submitMilliseconds = clock.Elapsed.TotalMilliseconds - submittedAt;
            if (submission != SubmitStatus.Accepted)
                throw new InvalidOperationException("Shadow submission: " + submission);
            pending = batch;
            pendingSample = sample;
            pendingPhysical = physical;
            pendingLinks = links;
            pendingUnmappedJoints = unmappedJoints;
            lastCaptureEpoch = physicsEpoch;
            report.submitted++;
            Status =
                "Shadow transport: "
                + report.accepted
                + " accepted, "
                + report.stale
                + " stale, "
                + count
                + " bodies. No vessel writes.";
        }

        static string Topology(Vessel vessel, List<Rigidbody> bodies)
        {
            if (vessel == null)
                return "no-active-vessel";
            if (vessel.parts == null || vessel.parts.Count > SimulationBatch.MaxBodies)
                throw new InvalidOperationException(
                    "Vessel part inventory is absent or exceeds the shadow bound."
                );
            var signature = new StringBuilder(vessel.id.ToString("D"));
            signature
                .Append(':')
                .Append(vessel.GetInstanceID())
                .Append(':')
                .Append(vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID());
            var distinct = new HashSet<Rigidbody>();
            foreach (Part part in vessel.parts)
            {
                if (part == null)
                    throw new InvalidOperationException("Vessel contains a missing part.");
                Rigidbody rb = part.rb;
                signature
                    .Append('|')
                    .Append(part.flightID)
                    .Append(':')
                    .Append(part.GetInstanceID())
                    .Append(':')
                    .Append(part.parent == null ? 0 : part.parent.flightID)
                    .Append(':')
                    .Append(rb == null ? 0 : rb.GetInstanceID())
                    .Append(':')
                    .Append(rb != null && rb.isKinematic);
                if (rb != null && !rb.isKinematic && distinct.Add(rb))
                {
                    if (bodies.Count == 512)
                        throw new InvalidOperationException(
                            "Shadow body count exceeds 512; capture is not truncated."
                        );
                    bodies.Add(rb);
                }
            }
            return signature.ToString();
        }

        void Accept(SimulationBatch result, string topology, string physicalFrame)
        {
            double maxPosition = 0,
                maxVelocity = 0;
            for (int i = 0; i < result.Count; i++)
            {
                Vec expected =
                    pending.GetPosition(i) + pending.GetVelocity(i) * pending.StepSeconds;
                maxPosition = Math.Max(maxPosition, Distance(expected, result.GetPosition(i)));
                maxVelocity = Math.Max(
                    maxVelocity,
                    Distance(pending.GetVelocity(i), result.GetVelocity(i))
                );
            }
            pendingSample.analyticAvailable = true;
            pendingSample.analyticMaxPositionError = maxPosition;
            pendingSample.analyticMaxVelocityError = maxVelocity;
            if (maxPosition != 0 || maxVelocity != 0)
                throw new InvalidOperationException("Zero-force transport oracle mismatch.");
            if (report.firstAcceptedBatch.Length == 0)
            {
                if (pendingPhysical == null || pendingPhysical.Length != pending.Count)
                    throw new InvalidOperationException(
                        "Accepted shadow request has no physical input snapshot."
                    );
                report.firstAcceptedTick = pending.Stamp.Tick;
                for (int i = 0; i < pending.Count; i++)
                {
                    pendingPhysical[i].predictedPosition = A(result.GetPosition(i));
                    pendingPhysical[i].predictedVelocity = A(result.GetVelocity(i));
                }
                report.firstAcceptedBatch = pendingPhysical;
                report.firstAcceptedLinks = pendingLinks ?? new StructuralLink[0];
                report.firstAcceptedUnmappedJoints = pendingUnmappedJoints;
            }
            comparison.Attach(pendingSample, result, topology, physicalFrame);
            if (pendingGravity != null)
                gravityComparison.Attach(
                    pendingSample,
                    pendingGravity,
                    result,
                    new Vec(
                        pendingSample.predictedKrakensbaneFrameVelocityDelta[0],
                        pendingSample.predictedKrakensbaneFrameVelocityDelta[1],
                        pendingSample.predictedKrakensbaneFrameVelocityDelta[2]
                    ),
                    topology,
                    physicalFrame
                );
            comparisonSample = pendingSample;
            report.accepted++;
        }

        static StructuralLink[] CaptureLinks(Vessel vessel, List<Rigidbody> bodies, out int unmapped)
        {
            var bodyIds = new Dictionary<Rigidbody, int>();
            for (int i = 0; i < bodies.Count; i++) bodyIds.Add(bodies[i], i);
            var seen = new HashSet<int>();
            var links = new List<StructuralLink>();
            unmapped = 0;
            Joint[] joints = vessel.GetComponentsInChildren<Joint>(true);
            if (joints.Length > 2048)
                throw new InvalidOperationException("Shadow joint count exceeds 2048; capture is not truncated.");
            Array.Sort(joints, (left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
            foreach (Joint joint in joints)
            {
                if (joint == null || !seen.Add(joint.GetInstanceID())) continue;
                Rigidbody host = joint.GetComponent<Rigidbody>();
                int hostId, connectedId;
                if (host == null || joint.connectedBody == null || !bodyIds.TryGetValue(host, out hostId)
                    || !bodyIds.TryGetValue(joint.connectedBody, out connectedId) || hostId == connectedId)
                {
                    unmapped++;
                    continue;
                }
                links.Add(new StructuralLink {
                    nativeInstanceId = joint.GetInstanceID(), bodyId = hostId, connectedBodyId = connectedId,
                    jointType = joint.GetType().FullName, anchor = A(joint.anchor), connectedAnchor = A(joint.connectedAnchor),
                    axis = A(joint.axis), secondaryAxis = A(joint is ConfigurableJoint ? ((ConfigurableJoint)joint).secondaryAxis : Vector3.zero),
                    breakForce = joint.breakForce, breakTorque = joint.breakTorque, collisionEnabled = joint.enableCollision,
                    preprocessingEnabled = joint.enablePreprocessing, massScale = joint.massScale,
                    connectedMassScale = joint.connectedMassScale,
                });
            }
            return links.ToArray();
        }

        static double Distance(Vec a, Vec b)
        {
            double x = a.X - b.X,
                y = a.Y - b.Y,
                z = a.Z - b.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        static Vec V(Vector3 v)
        {
            return new Vec(v.x, v.y, v.z);
        }

        static double[] A(Vec v)
        {
            return new[] { v.X, v.Y, v.Z };
        }

        static double[] A(Vector3 v)
        {
            return new[] { (double)v.x, v.y, v.z };
        }

        static double[] A(Vector3d v)
        {
            return new[] { v.x, v.y, v.z };
        }

        static double[] A(Quaternion q)
        {
            return new[] { (double)q.x, q.y, q.z, q.w };
        }

        static string F(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException("Nonfinite physical frame.");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        void Finish(string state, string reason)
        {
            if (finished)
                return;
            finished = true;
            gravityComparison.Cancel("on-" + state);
            if (comparison.Cancel("on-" + state))
            {
                report.comparisonSkipped++;
                comparisonSample = null;
            }
            GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
            worker.Dispose();
            if (ReferenceEquals(owner, this))
                owner = null;
            if (pendingSample != null)
            {
                pendingSample.status = "abandoned-on-" + state;
                if (pendingGravity != null)
                    pendingSample.gravityComparisonStatus = "skipped-on-" + state;
                pendingSample.collectUnityFrame = Time.frameCount;
                pendingSample.handoffWallMilliseconds =
                    clock.Elapsed.TotalMilliseconds - submittedAt;
                samples.Add(pendingSample);
            }
            pending = null;
            pendingSample = null;
            pendingPhysical = null;
            pendingGravity = null;
            report.status = state;
            report.reason = reason;
            report.wallSeconds = clock.Elapsed.TotalSeconds;
            report.physicsEpochs = physicsEpoch;
            report.originEvents = originEvents;
            report.samples = samples.ToArray();
            clock.Stop();
            Status =
                "Shadow "
                + state
                + ": "
                + report.accepted
                + " accepted, "
                + report.stale
                + " stale.";
            var callback = completion;
            completion = null;
            try
            {
                ShadowPhysicalInput.Validate(report);
                callback(report);
            }
            catch (Exception error)
            {
                Status = "Shadow export failed: " + error.GetType().Name;
                UnityEngine.Debug.LogError("[Continuum] " + Status);
                UnityEngine.Debug.LogException(error);
            }
        }

        public void Dispose()
        {
            Finish("interrupted", "Stopped by caller or flight panel teardown.");
        }
    }
}
