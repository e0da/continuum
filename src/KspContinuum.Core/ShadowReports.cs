namespace KspContinuum
{
    public sealed class ShadowReport
    {
        public string schema = "ksp-continuum-flight-shadow/v1";
        public string physicalInputSchema = ShadowPhysicalInput.PhysicalInputSchema;
        public string referenceFrameSchema = ShadowPhysicalInput.ReferenceFrameSchema;
        public string aggregateForceStatus = ShadowPhysicalInput.AggregateForceUnavailable;
        public string scope = "Read-only transport probe: one accepted batch retains captured Unity rigidbody pose, velocity, center-of-mass and principal-inertia inputs. Force is synthetic zero for a constant-velocity arithmetic check, not a native force measurement. No vessel writes, stock trajectory prediction, gravity model, or speedup claim.";
        public string framePolicy = "Unity world coordinates; raw Rigidbody.velocity. Every observed physics epoch and floating-origin event invalidates pending results; exact Krakensbane frame-velocity changes also invalidate. This is a conservative epoch guard, not a complete KSP frame model.";
        public string status = "waiting", reason, unity, ksp, plugin, startedUtc;
        public string units = "position and centers m; velocity m/s; angular velocity rad/s; quaternion [x,y,z,w]; native Rigidbody.mass and inertia tensor retained without unit conversion; timings ms; dt s";
        public int maxBodies = 512, requestedSamples = 120, submitted, accepted, stale;
        public double readyTimeoutSeconds = 120, activeTimeoutSeconds = 30, wallSeconds;
        public long originEvents, physicsEpochs;
        public ShadowSample[] samples = new ShadowSample[0];
        public ShadowBody[] firstAcceptedBatch = new ShadowBody[0];
        public long firstAcceptedTick;
    }
    public sealed class ShadowSample
    {
        public long tick, topologyGeneration, frameGeneration;
        public string status, vesselId, body, situation;
        public int parts, bodies, captureUnityFrame, collectUnityFrame;
        public double universalTime, stepSeconds, captureMilliseconds, submitMilliseconds, collectMilliseconds, collectAuditMilliseconds, handoffWallMilliseconds;
        public double analyticMaxPositionError, analyticMaxVelocityError;
        public bool analyticAvailable, packed;
        public double warpRate;
        public string referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame;
        public double[] rawKrakensbaneFrameVelocity = new double[0];
        public long physicsEpoch, floatingOriginEventCount;
    }
    public sealed class ShadowBody
    {
        public int id, nativeInstanceId;
        public double mass;
        public int constraints;
        public bool sleeping;
        public double[] position, rotation, velocity, angularVelocity, centerOfMass, worldCenterOfMass;
        public double[] inertiaTensor, inertiaTensorRotation;
        public double[] force, predictedPosition, predictedVelocity;
        public string forceSource = ShadowPhysicalInput.SyntheticZeroForce;
    }
}
