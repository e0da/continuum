namespace KspContinuum
{
    public sealed class ShadowReport
    {
        public string schema = "ksp-continuum-flight-shadow/v1";
        public string scope = "Read-only transport probe: captured Unity rigidbody positions/velocities, zero-force constant-velocity prediction. No vessel writes, gravity model, stock trajectory comparison, or speedup claim.";
        public string framePolicy = "Unity world coordinates; raw Rigidbody.velocity. Every observed physics epoch and floating-origin event invalidates pending results; exact Krakensbane frame-velocity changes also invalidate. This is a conservative epoch guard, not a complete KSP frame model.";
        public string status = "waiting", reason, unity, ksp, plugin, startedUtc;
        public string units = "position m; velocity m/s; native Rigidbody.mass retained without unit conversion; timings ms; dt s";
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
    }
    public sealed class ShadowBody
    {
        public int id, nativeInstanceId;
        public double mass;
        public double[] position, velocity, force, predictedPosition, predictedVelocity;
    }
}
