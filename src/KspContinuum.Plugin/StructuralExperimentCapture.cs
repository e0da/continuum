using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class StructuralExperimentCapture : IDisposable
    {
        static StructuralExperimentCapture owner;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly string mode;
        readonly Action<StructuralExperimentReport> complete;
        readonly PhysicsBoundaryHooks hooks;
        readonly Stopwatch clock = new Stopwatch();
        readonly List<StructuralBodySample> samples = new List<StructuralBodySample>();
        readonly List<StructuralTraceSample> trace = new List<StructuralTraceSample>();
        Vessel vessel; Rigidbody bodyA, bodyB; ConfigurableJoint joint;
        StructuralContactSentinel sentinelA, sentinelB;
        int originEvents; long cycle; bool started, active, cleanupRequested, destroyRequested, finished, disposed;
        string topology; double[] axis, reference; int sentinelsRequestedForRemoval;
        public StructuralExperimentReport Report { get; private set; }
        public bool IsRunning { get { return started && !finished; } }
        public string Status { get; private set; }

        public StructuralExperimentCapture(string mode, LifecycleOrderQualificationReport qualification,
            Action<StructuralExperimentReport> complete)
        {
            if (mode != "sham" && mode != "impulse") throw new ArgumentException("Mode must be sham or impulse.");
            if (complete == null) throw new ArgumentNullException("complete");
            LifecycleOrderQualification.Validate(qualification);
            if (qualification.status != "qualified" || qualification.evidence != "native-isolated-rigidbody-observation")
                throw new InvalidOperationException("A native qualified physics boundary is required.");
            this.mode = mode; this.complete = complete; hooks = new PhysicsBoundaryHooks(BeforePhysics, AfterPhysics);
            Report = new StructuralExperimentReport {
                mode = mode, impulseMagnitude = mode == "impulse" ? .01 : 0,
                unity = Application.unityVersion,
                ksp = Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision,
                plugin = typeof(StructuralExperimentCapture).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                startedUtc = DateTime.UtcNow.ToString("o"), sessionId = Guid.NewGuid().ToString("D"),
                runId = Guid.NewGuid().ToString("D"), lifecycleQualification = qualification,
                lifecycleQualificationId = qualification.qualificationId,
                injectionCallback = qualification.injectionCallback, observationCallback = qualification.observationCallback,
                stepSeconds = LifecycleOrderQualification.QualifiedStepSeconds(qualification),
            };
            if (Report.unity != qualification.unity || Report.ksp != qualification.ksp || Report.plugin != qualification.plugin)
                throw new InvalidOperationException("Physics qualification belongs to another installed build.");
        }

        public void Start()
        {
            RequireThread();
            if (started || disposed) throw new InvalidOperationException("Structural capture supports one start.");
            if (owner != null) throw new InvalidOperationException("Another structural capture is active.");
            owner = this; started = true;
            try
            {
                Admit();
                sentinelA = bodyA.gameObject.AddComponent<StructuralContactSentinel>();
                sentinelB = bodyB.gameObject.AddComponent<StructuralContactSentinel>();
                sentinelA.Owner = sentinelB.Owner = this;
                Report.admission.installedContactSentinels = 2;
                Report.admission.bodyASentinelTargetInstanceId = bodyA.GetInstanceID();
                Report.admission.bodyBSentinelTargetInstanceId = bodyB.GetInstanceID();
                GameEvents.onFloatingOriginShift.Add(OnOriginShift);
                hooks.Start(); active = true; Report.status = "running"; Status = "Structural " + mode + " capture running.";
                clock.Start();
            }
            catch (Exception error) { Invalidate("Start failed: " + error.GetType().Name + ": " + error.Message); Cleanup(); }
        }

        void Admit()
        {
            vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (!Eligible()) throw new InvalidOperationException("Use a coasting vacuum vessel with SAS/RCS off at normal time rate.");
            if (Math.Abs(Time.fixedDeltaTime - Report.stepSeconds) > 1e-9) throw new InvalidOperationException("Fixed timestep changed after qualification.");
            var bodies = new List<Rigidbody>();
            foreach (Part part in vessel.parts)
                if (part != null && part.rb != null && !part.rb.isKinematic && !bodies.Contains(part.rb)) bodies.Add(part.rb);
            if (bodies.Count != 2) throw new InvalidOperationException("Structural experiment requires exactly two dynamic rigidbodies.");
            Joint[] joints = vessel.GetComponentsInChildren<Joint>(true);
            if (joints.Length != 1 || !(joints[0] is ConfigurableJoint) || !joints[0].gameObject.activeInHierarchy)
                throw new InvalidOperationException("Structural experiment requires exactly one active ConfigurableJoint.");
            joint = (ConfigurableJoint)joints[0]; bodyA = joint.GetComponent<Rigidbody>(); bodyB = joint.connectedBody;
            if (bodyA == null || bodyB == null || bodyA == bodyB || !bodies.Contains(bodyA) || !bodies.Contains(bodyB))
                throw new InvalidOperationException("Joint endpoints do not map to the two dynamic bodies.");
            Report.vesselId = vessel.id.ToString(); Report.bodyAInstanceId = bodyA.GetInstanceID();
            Report.bodyBInstanceId = bodyB.GetInstanceID(); Report.jointInstanceId = joint.GetInstanceID();
            Report.admission = Admission(); topology = StructuralExperiment.ComputeTopology(Report);
            Report.topology = Report.admission.topology = topology;
        }

        StructuralAdmissionEvidence Admission()
        {
            return new StructuralAdmissionEvidence {
                status = "verified", dynamicBodyCount = 2, mappedJointCount = 1, unmappedJointCount = 0,
                bodyAInstanceId = bodyA.GetInstanceID(), bodyBInstanceId = bodyB.GetInstanceID(),
                jointInstanceId = joint.GetInstanceID(), jointType = joint.GetType().FullName,
                jointHostBodyInstanceId = bodyA.GetInstanceID(), jointConnectedBodyInstanceId = bodyB.GetInstanceID(),
                jointEnabled = true, topologyGeneration = 1, joint = CaptureLink(joint),
                jointTransformRotation = A(joint.transform.rotation), hookCleanupStatus = "installed"
            };
        }

        void BeforePhysics()
        {
            if (!active) return;
            try
            {
                RequireStable(); cycle++;
                if (cycle > StructuralExperimentReport.RequiredSamples) throw new InvalidOperationException("Unexpected extra physics cycle.");
                if (cycle != 1) return;
                Vector3 worldAxis = joint.transform.TransformDirection(joint.axis);
                if (worldAxis.sqrMagnitude <= 0) throw new InvalidOperationException("Joint axis is degenerate.");
                worldAxis.Normalize(); axis = A(worldAxis); Report.worldAxis = axis;
                Report.baseline = CaptureBodySample(); Report.baseline.physicsCycle = 1;
                Report.baselineBodyAWorldCenterOfMass = (double[])Report.baseline.bodyAWorldCenterOfMass.Clone();
                Report.baselineBodyBWorldCenterOfMass = (double[])Report.baseline.bodyBWorldCenterOfMass.Clone();
                reference = Difference(Report.baseline.bodyBWorldCenterOfMass, Report.baseline.bodyAWorldCenterOfMass);
                Report.referenceRelativeCenterOfMass = reference;
                Vector3 command = mode == "impulse" ? worldAxis * .01f : Vector3.zero;
                Report.impulseMagnitude = command.magnitude;
                Report.requestedBodyAImpulse = A(command); Report.requestedBodyBImpulse = A(-command);
                Report.requestedNetImpulse = new double[3];
                var witness = new StructuralInjectionWitness { status = "completed", callback = Report.injectionCallback,
                    physicsEpoch = 1, physicsCycle = 1, totalInjectionCallbacks = 1,
                    bodyAInstanceId = bodyA.GetInstanceID(), bodyBInstanceId = bodyB.GetInstanceID(),
                    bodyAImpulse = A(command), bodyBImpulse = A(-command) };
                Report.injection = witness;
                if (mode == "impulse")
                {
                    witness.totalBodyACommands = 1; bodyA.AddForce(command, ForceMode.Impulse); witness.bodyACommandReturned = true;
                    witness.totalBodyBCommands = 1; bodyB.AddForce(-command, ForceMode.Impulse); witness.bodyBCommandReturned = true;
                }
            }
            catch (Exception error) { Invalidate("Pre-physics capture failed: " + error.GetType().Name + ": " + error.Message); }
        }

        void AfterPhysics()
        {
            if (!active) return;
            try
            {
                RequireStable();
                if (cycle <= 0 || samples.Count != cycle - 1) throw new InvalidOperationException("Physics callbacks are unmatched.");
                if (Report.admission.detectedContactCount != 0 || Report.admission.jointBreakCount != 0)
                    throw new InvalidOperationException("Contact or joint break invalidated the experiment.");
                StructuralBodySample sample = CaptureBodySample(); samples.Add(sample);
                double displacement = Dot(Difference(Difference(sample.bodyBWorldCenterOfMass, sample.bodyAWorldCenterOfMass), reference), axis);
                double velocity = Dot(Difference(sample.bodyBVelocity, sample.bodyAVelocity), axis);
                trace.Add(new StructuralTraceSample { physicsEpoch = cycle, relativeDisplacement = displacement, relativeVelocity = velocity });
                Report.admission.contactObservationCount++;
                if (samples.Count == StructuralExperimentReport.RequiredSamples)
                {
                    active = false; cleanupRequested = true;
                    Report.admission.contactWindowFirstEpoch = 1; Report.admission.contactWindowLastEpoch = cycle;
                    Status = "Structural capture complete; cleaning up.";
                }
            }
            catch (Exception error) { Invalidate("Post-physics capture failed: " + error.GetType().Name + ": " + error.Message); }
        }

        StructuralBodySample CaptureBodySample()
        {
            return new StructuralBodySample { physicsEpoch = cycle, physicsCycle = cycle, topologyGeneration = 1,
                frameGeneration = 1, originEventCount = originEvents, unityFrame = Time.frameCount,
                fixedTimeSeconds = Time.fixedTime, bodyAInstanceId = bodyA.GetInstanceID(), bodyBInstanceId = bodyB.GetInstanceID(),
                bodyAWorldCenterOfMass = A(bodyA.worldCenterOfMass), bodyARotation = A(bodyA.rotation),
                bodyAVelocity = A(bodyA.velocity), bodyAAngularVelocity = A(bodyA.angularVelocity),
                bodyBWorldCenterOfMass = A(bodyB.worldCenterOfMass), bodyBRotation = A(bodyB.rotation),
                bodyBVelocity = A(bodyB.velocity), bodyBAngularVelocity = A(bodyB.angularVelocity) };
        }

        void RequireStable()
        {
            RequireThread(); hooks.Audit();
            if (!hooks.IsValid || !Eligible() || vessel != FlightGlobals.ActiveVessel || joint == null || !joint.gameObject.activeInHierarchy
                || bodyA == null || bodyB == null || originEvents != 0
                || Math.Abs(Time.fixedDeltaTime - Report.stepSeconds) > 1e-9)
                throw new InvalidOperationException("Structural context changed during capture.");
            RequireSentinels();
            var candidate = Admission(); var scratch = new StructuralExperimentReport { bodyAInstanceId = Report.bodyAInstanceId,
                bodyBInstanceId = Report.bodyBInstanceId, jointInstanceId = Report.jointInstanceId, admission = candidate };
            if (StructuralExperiment.ComputeTopology(scratch) != topology) throw new InvalidOperationException("Structural topology changed during capture.");
        }

        bool Eligible()
        {
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.HoldPhysics || FlightDriver.Pause
                || Time.timeScale != 1f || TimeWarp.CurrentRate != 1f || vessel.atmDensity != 0
                || vessel.ctrlState == null || vessel.ctrlState.mainThrottle != 0
                || vessel.ActionGroups[KSPActionGroup.SAS] || vessel.ActionGroups[KSPActionGroup.RCS]) return false;
            foreach (Part part in vessel.parts)
                if (part != null) foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                    if (engine != null && (engine.currentThrottle != 0 || engine.finalThrust != 0)) return false;
            return true;
        }

        public void Tick()
        {
            RequireThread();
            if (active)
            {
                try
                {
                    hooks.Audit();
                    if (!hooks.IsValid) Invalidate(hooks.Detail ?? "Physics boundary changed.");
                    else if (!Eligible() || vessel != FlightGlobals.ActiveVessel) Invalidate("Structural eligibility changed while awaiting physics.");
                    else { RequireSentinels(); if (clock.Elapsed.TotalSeconds > 15) Invalidate("Structural capture timed out."); }
                }
                catch (Exception error) { Invalidate("Boundary audit failed: " + error.GetType().Name); }
            }
            if (cleanupRequested && !destroyRequested) Cleanup();
            else if (destroyRequested && !finished && sentinelA == null && sentinelB == null) FinalizeReport();
        }

        void Cleanup()
        {
            active = false; cleanupRequested = false;
            try { hooks.Dispose(); } catch { }
            if (Report.admission != null) Report.admission.hookCleanupStatus = hooks.CleanupStatus;
            if (hooks.CleanupStatus == "cleanup-error") { Report.status = "invalid"; Report.reason = "Physics-boundary hook cleanup failed."; }
            try { GameEvents.onFloatingOriginShift.Remove(OnOriginShift); } catch { }
            if (sentinelA != null && sentinelA.Owner == this) { sentinelsRequestedForRemoval++; sentinelA.Owner = null; UnityEngine.Object.Destroy(sentinelA); }
            if (sentinelB != null && sentinelB.Owner == this) { sentinelsRequestedForRemoval++; sentinelB.Owner = null; UnityEngine.Object.Destroy(sentinelB); }
            destroyRequested = true;
            if (sentinelA == null && sentinelB == null) FinalizeReport();
        }

        void FinalizeReport()
        {
            if (finished) return;
            if (Report.status == "running")
            {
                Report.admission.removedContactSentinels = sentinelsRequestedForRemoval;
                Report.contactObservationStatus = "observed-none"; Report.runEligibility = "eligible";
                Report.receiptValidity = "valid"; Report.cleanupStatus = "complete";
                Report.retainedSamples = samples.Count; Report.bodySamples = samples.ToArray();
                Report.trace = new StructuralTrace { evidence = Report.evidence, topology = Report.topology,
                    stepSeconds = Report.stepSeconds, samples = trace.ToArray() };
                Report.status = "complete";
                try { StructuralExperiment.Validate(Report); }
                catch (Exception error) { InvalidateFinal("Final validation failed: " + error.GetType().Name + ": " + error.Message); }
            }
            else PrepareInvalid();
            finished = true; disposed = true; if (owner == this) owner = null;
            Status = Report.status == "complete" ? "Structural " + mode + " capture complete." : Report.reason;
            try { complete(Report); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        void Invalidate(string reason)
        {
            if (finished) return; Report.status = "invalid"; Report.reason = reason; active = false; cleanupRequested = true; Status = reason;
        }
        void InvalidateFinal(string reason) { Report.status = "invalid"; Report.reason = reason; PrepareInvalid(); }
        void PrepareInvalid()
        {
            Report.status = "invalid"; Report.receiptValidity = "valid-invalidated-run"; Report.runEligibility = "ineligible";
            Report.experimentQualified = "not-evaluated";
            Report.cleanupStatus = hooks.CleanupStatus == "cleanup-error" ? "cleanup-error" : "complete";
            Report.trace = null; Report.bodySamples = new StructuralBodySample[0]; Report.admission = null; Report.baseline = null; Report.injection = null;
            Report.worldAxis = new double[0]; Report.baselineBodyAWorldCenterOfMass = new double[0];
            Report.baselineBodyBWorldCenterOfMass = new double[0]; Report.referenceRelativeCenterOfMass = new double[0];
            Report.requestedBodyAImpulse = new double[0]; Report.requestedBodyBImpulse = new double[0]; Report.requestedNetImpulse = new double[0];
            if (Report.cleanupStatus == "complete")
                try { StructuralExperiment.Validate(Report); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        void RequireSentinels()
        {
            if (sentinelA == null || sentinelB == null || !sentinelA.enabled || !sentinelB.enabled
                || sentinelA.Owner != this || sentinelB.Owner != this
                || sentinelA.gameObject != bodyA.gameObject || sentinelB.gameObject != bodyB.gameObject)
                throw new InvalidOperationException("Structural contact coverage changed.");
        }

        public void ObserveContact() { if (Report.admission != null) Report.admission.detectedContactCount++; }
        public void ObserveJointBreak() { if (Report.admission != null) Report.admission.jointBreakCount++; }
        void OnOriginShift(Vector3d offset, Vector3d nonFrame) { originEvents++; }
        public void Dispose()
        {
            if (finished || disposed) return; RequireThread(); Invalidate("Structural experiment interrupted."); Cleanup();
            if (!finished)
            {
                sentinelA = null; sentinelB = null; PrepareInvalid(); finished = true; disposed = true;
                if (owner == this) owner = null;
                try { complete(Report); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
            }
        }
        void RequireThread() { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Structural capture changed threads."); }

        static StructuralLink CaptureLink(ConfigurableJoint value)
        {
            return new StructuralLink { nativeInstanceId = value.GetInstanceID(), bodyId = 0, connectedBodyId = 1,
                jointType = value.GetType().FullName, anchor = A(value.anchor), connectedAnchor = A(value.connectedAnchor),
                axis = A(value.axis), secondaryAxis = A(value.secondaryAxis),
                breakForce = StructuralThreshold.Value(value.breakForce), breakTorque = StructuralThreshold.Value(value.breakTorque),
                breakForceStatus = StructuralThreshold.Status(value.breakForce), breakTorqueStatus = StructuralThreshold.Status(value.breakTorque),
                collisionEnabled = value.enableCollision, preprocessingEnabled = value.enablePreprocessing,
                massScale = value.massScale, connectedMassScale = value.connectedMassScale,
                configurable = new StructuralConfigurableJoint { autoConfigureConnectedAnchor = value.autoConfigureConnectedAnchor,
                    configuredInWorldSpace = value.configuredInWorldSpace, swapBodies = value.swapBodies,
                    xMotion = value.xMotion.ToString(), yMotion = value.yMotion.ToString(), zMotion = value.zMotion.ToString(),
                    angularXMotion = value.angularXMotion.ToString(), angularYMotion = value.angularYMotion.ToString(), angularZMotion = value.angularZMotion.ToString(),
                    rotationDriveMode = value.rotationDriveMode.ToString(), projectionMode = value.projectionMode.ToString(),
                    projectionDistance = value.projectionDistance, projectionAngle = value.projectionAngle,
                    targetPosition = A(value.targetPosition), targetVelocity = A(value.targetVelocity), targetRotation = A(value.targetRotation),
                    targetAngularVelocity = A(value.targetAngularVelocity), linearLimit = Limit(value.linearLimit), lowAngularXLimit = Limit(value.lowAngularXLimit),
                    highAngularXLimit = Limit(value.highAngularXLimit), angularYLimit = Limit(value.angularYLimit), angularZLimit = Limit(value.angularZLimit),
                    linearLimitSpring = Spring(value.linearLimitSpring), angularXLimitSpring = Spring(value.angularXLimitSpring), angularYZLimitSpring = Spring(value.angularYZLimitSpring),
                    xDrive = Drive(value.xDrive), yDrive = Drive(value.yDrive), zDrive = Drive(value.zDrive), angularXDrive = Drive(value.angularXDrive),
                    angularYZDrive = Drive(value.angularYZDrive), slerpDrive = Drive(value.slerpDrive) } };
        }
        static StructuralLimit Limit(SoftJointLimit v) { return new StructuralLimit { limit = v.limit, bounciness = v.bounciness, contactDistance = v.contactDistance }; }
        static StructuralSpring Spring(SoftJointLimitSpring v) { return new StructuralSpring { spring = v.spring, damper = v.damper }; }
        static StructuralDrive Drive(JointDrive v) { return new StructuralDrive { positionSpring = v.positionSpring, positionDamper = v.positionDamper,
            maximumForce = StructuralThreshold.Value(v.maximumForce), maximumForceStatus = StructuralThreshold.Status(v.maximumForce) }; }
        static double[] A(Vector3 v) { return new[] { (double)v.x, (double)v.y, (double)v.z }; }
        static double[] A(Quaternion q) { return new[] { (double)q.x, (double)q.y, (double)q.z, (double)q.w }; }
        static double[] Difference(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    }

    public sealed class StructuralContactSentinel : MonoBehaviour
    {
        internal StructuralExperimentCapture Owner;
        void OnCollisionEnter(Collision value) { if (Owner != null) Owner.ObserveContact(); }
        void OnCollisionStay(Collision value) { if (Owner != null) Owner.ObserveContact(); }
        void OnJointBreak(float force) { if (Owner != null) Owner.ObserveJointBreak(); }
    }
}
