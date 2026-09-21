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
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<ShadowSample> samples = new List<ShadowSample>();
        readonly ShadowReport report;
        readonly SimulationWorker worker;
        Action<ShadowReport> completion;
        SimulationBatch pending;
        ShadowSample pendingSample;
        int[] pendingNativeIds;
        double activeStart = -1, submittedAt;
        long physicsEpoch, originEvents, lastCaptureEpoch = -1;
        bool finished;
        public string Status { get; private set; }
        public bool IsRunning { get { return !finished; } }

        public ShadowCapture(Action<ShadowReport> complete)
        {
            if (complete == null) throw new ArgumentNullException("complete");
            if (owner != null) throw new InvalidOperationException("A flight shadow capture is already active.");
            report = new ShadowReport { unity = Application.unityVersion, ksp = Versioning.GetVersionString(),
                plugin = typeof(ShadowCapture).Assembly.GetName().Version.ToString(), startedUtc = DateTime.UtcNow.ToString("o") };
            worker = new SimulationWorker(new ConstantForceBackend());
            completion = complete; owner = this;
            try { GameEvents.onFloatingOriginShift.Add(OnOriginShift); }
            catch { worker.Dispose(); owner = null; throw; }
            Status = "Shadow armed: waiting for a loaded, unpacked vessel at normal rate.";
        }

        void OnOriginShift(Vector3d offset, Vector3d nonFrame) { originEvents++; }
        public void FixedBoundary() { physicsEpoch++; }
        public void Tick(bool allowCapture)
        {
            if (finished) return;
            try { Pump(allowCapture); }
            catch (Exception error)
            {
                UnityEngine.Debug.LogException(error);
                Finish("failed", error.GetType().Name + ": " + error.Message);
            }
        }
        void Pump(bool allowCapture)
        {
            double auditStart = clock.Elapsed.TotalMilliseconds;
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            var bodies = new List<Rigidbody>();
            string topology = Topology(vessel, bodies);
            Vector3d frameVelocity = Krakensbane.GetFrameVelocity();
            string frame = HighLogic.LoadedScene + ":" + physicsEpoch + ":" + originEvents + ":" +
                F(frameVelocity.x) + ":" + F(frameVelocity.y) + ":" + F(frameVelocity.z);
            bool eligible = vessel != null && vessel.loaded && !vessel.packed && !vessel.HoldPhysics &&
                !FlightDriver.Pause && TimeWarp.CurrentRate == 1 && Time.timeScale == 1 && bodies.Count != 0;
            epoch.Observe(topology, frame, eligible);
            double auditMs = clock.Elapsed.TotalMilliseconds - auditStart;
            if (pending != null)
            {
                pendingSample.collectAuditMilliseconds += auditMs;
                double before = clock.Elapsed.TotalMilliseconds;
                SimulationBatch result;
                ResultStatus state = worker.TryTake(epoch.Expected(pending.Stamp), out result);
                pendingSample.collectMilliseconds += clock.Elapsed.TotalMilliseconds - before;
                if (state == ResultStatus.Ready || state == ResultStatus.Stale)
                {
                    pendingSample.status = state == ResultStatus.Ready ? "accepted" : "stale-discarded";
                    pendingSample.collectUnityFrame = Time.frameCount;
                    pendingSample.handoffWallMilliseconds = clock.Elapsed.TotalMilliseconds - submittedAt;
                    if (state == ResultStatus.Ready) Accept(result);
                    else report.stale++;
                    samples.Add(pendingSample); pending = null; pendingSample = null; pendingNativeIds = null;
                    if (samples.Count >= report.requestedSamples) { Finish("complete", "Bounded sample count reached."); return; }
                }
                else if (state == ResultStatus.Faulted || state == ResultStatus.Disposed || state == ResultStatus.Empty)
                    throw new InvalidOperationException("Shadow worker entered " + state, worker.Fault);
            }
            if (activeStart < 0 && eligible) activeStart = clock.Elapsed.TotalSeconds;
            if (activeStart < 0 && clock.Elapsed.TotalSeconds >= report.readyTimeoutSeconds)
            { Finish("unavailable", "No eligible flight state before ready timeout."); return; }
            if (activeStart >= 0 && clock.Elapsed.TotalSeconds - activeStart >= report.activeTimeoutSeconds)
            { Finish("timeout", "Active capture wall-time bound reached."); return; }
            if (!eligible) { Status = "Shadow waiting: paused, packed, loading, warped, or no dynamic part bodies."; return; }
            if (!allowCapture || pending != null || lastCaptureEpoch == physicsEpoch) return;
            double captureStart = clock.Elapsed.TotalMilliseconds;
            int count = bodies.Count;
            var ids = new int[count]; var nativeIds = new int[count]; var masses = new double[count];
            var positions = new Vec[count]; var velocities = new Vec[count]; var forces = new Vec[count];
            for (int i = 0; i < count; i++)
            {
                Rigidbody body = bodies[i];
                ids[i] = i; nativeIds[i] = body.GetInstanceID(); masses[i] = body.mass;
                positions[i] = V(body.position); velocities[i] = V(body.velocity);
            }
            var batch = SimulationBatch.FromColumns(epoch.CaptureStamp(), Time.fixedDeltaTime, ids, masses, positions, velocities, forces);
            var sample = new ShadowSample { tick = batch.Stamp.Tick, topologyGeneration = batch.Stamp.TopologyGeneration,
                frameGeneration = batch.Stamp.FrameGeneration, vesselId = vessel.id.ToString("D"), body = vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                situation = vessel.situation.ToString(), parts = vessel.parts.Count, bodies = count, captureUnityFrame = Time.frameCount,
                universalTime = Planetarium.GetUniversalTime(), stepSeconds = batch.StepSeconds, packed = vessel.packed, warpRate = TimeWarp.CurrentRate,
                captureMilliseconds = auditMs + clock.Elapsed.TotalMilliseconds - captureStart };
            submittedAt = clock.Elapsed.TotalMilliseconds;
            SubmitStatus submission = worker.TrySubmit(batch);
            sample.submitMilliseconds = clock.Elapsed.TotalMilliseconds - submittedAt;
            if (submission != SubmitStatus.Accepted) throw new InvalidOperationException("Shadow submission: " + submission);
            pending = batch; pendingSample = sample; pendingNativeIds = nativeIds; lastCaptureEpoch = physicsEpoch;
            report.submitted++;
            Status = "Shadow transport: " + report.accepted + " accepted, " + report.stale + " stale, " + count + " bodies. No vessel writes.";
        }

        static string Topology(Vessel vessel, List<Rigidbody> bodies)
        {
            if (vessel == null) return "no-active-vessel";
            if (vessel.parts == null || vessel.parts.Count > SimulationBatch.MaxBodies)
                throw new InvalidOperationException("Vessel part inventory is absent or exceeds the shadow bound.");
            var signature = new StringBuilder(vessel.id.ToString("D"));
            signature.Append(':').Append(vessel.GetInstanceID()).Append(':').Append(vessel.mainBody == null ? 0 : vessel.mainBody.GetInstanceID());
            var distinct = new HashSet<Rigidbody>();
            foreach (Part part in vessel.parts)
            {
                if (part == null) throw new InvalidOperationException("Vessel contains a missing part.");
                Rigidbody rb = part.rb;
                signature.Append('|').Append(part.flightID).Append(':').Append(part.GetInstanceID()).Append(':')
                    .Append(part.parent == null ? 0 : part.parent.flightID).Append(':').Append(rb == null ? 0 : rb.GetInstanceID())
                    .Append(':').Append(rb != null && rb.isKinematic);
                if (rb != null && !rb.isKinematic && distinct.Add(rb))
                {
                    if (bodies.Count == 512) throw new InvalidOperationException("Shadow body count exceeds 512; capture is not truncated.");
                    bodies.Add(rb);
                }
            }
            return signature.ToString();
        }
        void Accept(SimulationBatch result)
        {
            double maxPosition = 0, maxVelocity = 0;
            for (int i = 0; i < result.Count; i++)
            {
                Vec expected = pending.GetPosition(i) + pending.GetVelocity(i) * pending.StepSeconds;
                maxPosition = Math.Max(maxPosition, Distance(expected, result.GetPosition(i)));
                maxVelocity = Math.Max(maxVelocity, Distance(pending.GetVelocity(i), result.GetVelocity(i)));
            }
            pendingSample.analyticAvailable = true;
            pendingSample.analyticMaxPositionError = maxPosition; pendingSample.analyticMaxVelocityError = maxVelocity;
            if (maxPosition != 0 || maxVelocity != 0) throw new InvalidOperationException("Zero-force transport oracle mismatch.");
            if (report.firstAcceptedBatch.Length == 0)
            {
                report.firstAcceptedTick = pending.Stamp.Tick;
                report.firstAcceptedBatch = new ShadowBody[pending.Count];
                for (int i = 0; i < pending.Count; i++) report.firstAcceptedBatch[i] = new ShadowBody {
                    id = pending.GetId(i), nativeInstanceId = pendingNativeIds[i], mass = pending.GetMass(i),
                    position = A(pending.GetPosition(i)), velocity = A(pending.GetVelocity(i)), force = A(pending.GetForce(i)),
                    predictedPosition = A(result.GetPosition(i)), predictedVelocity = A(result.GetVelocity(i)) };
            }
            report.accepted++;
        }
        static double Distance(Vec a, Vec b) { double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z; return Math.Sqrt(x * x + y * y + z * z); }
        static Vec V(Vector3 v) { return new Vec(v.x, v.y, v.z); }
        static double[] A(Vec v) { return new[] { v.X, v.Y, v.Z }; }
        static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        void Finish(string state, string reason)
        {
            if (finished) return;
            finished = true;
            GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
            worker.Dispose();
            if (ReferenceEquals(owner, this)) owner = null;
            if (pendingSample != null)
            {
                pendingSample.status = "abandoned-on-" + state;
                pendingSample.collectUnityFrame = Time.frameCount;
                pendingSample.handoffWallMilliseconds = clock.Elapsed.TotalMilliseconds - submittedAt;
                samples.Add(pendingSample);
            }
            pending = null; pendingSample = null; pendingNativeIds = null;
            report.status = state; report.reason = reason; report.wallSeconds = clock.Elapsed.TotalSeconds;
            report.physicsEpochs = physicsEpoch; report.originEvents = originEvents; report.samples = samples.ToArray();
            clock.Stop(); Status = "Shadow " + state + ": " + report.accepted + " accepted, " + report.stale + " stale.";
            var callback = completion; completion = null;
            try { callback(report); }
            catch (Exception error)
            {
                Status = "Shadow export failed: " + error.GetType().Name;
                UnityEngine.Debug.LogError("[Continuum] " + Status); UnityEngine.Debug.LogException(error);
            }
        }
        public void Dispose() { Finish("interrupted", "Stopped by caller or flight panel teardown."); }
    }
}
