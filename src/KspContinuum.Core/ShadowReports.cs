namespace KspContinuum
{
    public sealed class ShadowReport
    {
        public string evidence = "native-adapter-observation";
        public string schema = "ksp-continuum-flight-shadow/v2";
        public string physicalInputSchema = ShadowPhysicalInput.PhysicalInputSchema;
        public string referenceFrameSchema = ShadowPhysicalInput.ReferenceFrameSchema;
        public string aggregateForceStatus = ShadowPhysicalInput.AggregateForceUnavailable;
        public string scope =
            "Read-only transport probe: one accepted batch retains captured Unity rigidbody pose, velocity, center-of-mass and principal-inertia inputs. Force is synthetic zero for a constant-velocity arithmetic check, not a native force measurement. Accepted predictions are compared against the next matching one-step stock observation when available. Differences include omitted forces; no vessel writes, validated stock trajectory model, gravity model, or speedup claim.";
        public string framePolicy =
            "Unity world coordinates; raw Rigidbody.velocity. Every observed physics epoch and floating-origin event invalidates pending worker results; exact Krakensbane frame-velocity changes also invalidate worker publication. Historical comparisons separately require exactly one observed host boundary, matching topology and scene, and a resolvable fixed-time interval. Both endpoint origin counters and frame velocities are retained because raw residuals include routine KSP frame adjustment. This is a conservative guard, not a complete KSP frame model.";
        public string status = "waiting",
            reason,
            unity,
            ksp,
            plugin,
            startedUtc;
        public string units =
            "position and centers m; velocity m/s; angular velocity rad/s; quaternion [x,y,z,w]; native Rigidbody.mass and inertia tensor retained without unit conversion; timings ms; dt s";
        public string comparisonScope =
            "Zero-force baseline versus observed raw-coordinate KSP motion: discrepancy telemetry including gravity, thrust, contacts, constraints and Krakensbane frame adjustment; not solver accuracy or native-force validation.";
        public int compared,
            comparisonSkipped;
        public int maxBodies = 512,
            requestedSamples = 120,
            submitted,
            accepted,
            stale;
        public double readyTimeoutSeconds = 120,
            activeTimeoutSeconds = 30,
            wallSeconds;
        public long originEvents,
            physicsEpochs;
        public ShadowSample[] samples = new ShadowSample[0];
        public ShadowBody[] firstAcceptedBatch = new ShadowBody[0];
        public long firstAcceptedTick;
    }

    public sealed class ShadowSample
    {
        public long tick,
            topologyGeneration,
            frameGeneration;
        public string status,
            vesselId,
            body,
            situation;
        public int parts,
            bodies,
            captureUnityFrame,
            collectUnityFrame;
        public double universalTime,
            stepSeconds,
            captureMilliseconds,
            submitMilliseconds,
            collectMilliseconds,
            collectAuditMilliseconds,
            handoffWallMilliseconds;
        public double analyticMaxPositionError,
            analyticMaxVelocityError;
        public bool analyticAvailable,
            packed;
        public double warpRate;
        public string referenceFrame = ShadowPhysicalInput.UnityWorldReferenceFrame;
        public double[] rawKrakensbaneFrameVelocity = new double[0];
        public double[] comparisonRawKrakensbaneFrameVelocity = new double[0];
        public long physicsEpoch,
            floatingOriginEventCount;
        public bool observedComparisonAvailable;
        public string comparisonStatus = "not-accepted";
        public int comparedBodies,
            comparisonUnityFrame;
        public long comparisonPhysicsEpoch;
        public long comparisonFloatingOriginEventCount;
        public double captureFixedTimeSeconds,
            comparisonFixedTimeSeconds,
            observedDeltaSeconds;
        public double observedPositionMaxMeters,
            observedPositionRmsMeters,
            observedVelocityMaxMetersPerSecond,
            observedVelocityRmsMetersPerSecond;
    }

    public sealed class ShadowBody
    {
        public int id,
            nativeInstanceId;
        public double mass;
        public int constraints;
        public bool sleeping;
        public double[] position,
            rotation,
            velocity,
            angularVelocity,
            centerOfMass,
            worldCenterOfMass;
        public double[] inertiaTensor,
            inertiaTensorRotation;
        public double[] force,
            predictedPosition,
            predictedVelocity;
        public string forceSource = ShadowPhysicalInput.SyntheticZeroForce;
    }
}

namespace KspContinuum
{
    public sealed class ShadowComparison
    {
        ShadowSample sample;
        SimulationBatch prediction;
        string topology,
            frame;
        public bool IsPending
        {
            get { return sample != null; }
        }
        public long ExpectedEpoch
        {
            get { return sample == null ? -1 : sample.physicsEpoch + 1; }
        }

        public void Attach(
            ShadowSample sample,
            SimulationBatch prediction,
            string topology,
            string frame
        )
        {
            if (
                IsPending
                || sample == null
                || prediction == null
                || prediction.Count > 512
                || sample.bodies != prediction.Count
                || sample.physicsEpoch < 0
                || sample.physicsEpoch == long.MaxValue
                || sample.stepSeconds != prediction.StepSeconds
                || !Finite(sample.captureFixedTimeSeconds)
                || topology == null
                || frame == null
            )
                throw new System.ArgumentException("Invalid comparison capture.");
            this.sample = sample;
            this.prediction = prediction;
            this.topology = topology;
            this.frame = frame;
            sample.comparisonStatus = "waiting";
        }

        public bool Observe(
            long epoch,
            string topology,
            string frame,
            bool eligible,
            double step,
            double fixedTime,
            int unityFrame,
            long comparisonOriginEvents,
            Vec[] positions,
            Vec[] velocities,
            double[] comparisonFrameVelocity
        )
        {
            if (!IsPending)
                return false;
            if (!eligible || topology != this.topology || frame != this.frame)
                return Cancel("context-change");
            if (epoch == sample.physicsEpoch)
                return false;
            if (epoch != ExpectedEpoch)
                return Cancel("missed-boundary");
            sample.comparisonPhysicsEpoch = epoch;
            sample.comparisonUnityFrame = unityFrame;
            sample.comparisonFloatingOriginEventCount = comparisonOriginEvents;
            if (!Finite(fixedTime) || !Finite(step))
                return Cancel("invalid-observation");
            sample.comparisonFixedTimeSeconds = fixedTime;
            double delta = fixedTime - sample.captureFixedTimeSeconds;
            if (!Finite(delta))
                return Cancel("invalid-observation");
            sample.observedDeltaSeconds = delta;
            if (
                comparisonFrameVelocity == null
                || comparisonFrameVelocity.Length != 3
                || !Finite(comparisonFrameVelocity[0])
                || !Finite(comparisonFrameVelocity[1])
                || !Finite(comparisonFrameVelocity[2])
            )
                return Cancel("invalid-observation");
            sample.comparisonRawKrakensbaneFrameVelocity = comparisonFrameVelocity;
            // Unity exposes fixedTime as float: reject when its precision cannot resolve the requested interval.
            double tolerance = System.Math.Max(
                1e-6,
                System.Math.Max(
                    System.Math.Abs(fixedTime),
                    System.Math.Abs(sample.captureFixedTimeSeconds)
                ) * 2.384185791015625e-7
            );
            if (tolerance >= prediction.StepSeconds * .25)
                return Cancel("timestep-resolution");
            if (
                step != prediction.StepSeconds
                || System.Math.Abs(sample.observedDeltaSeconds - prediction.StepSeconds) > tolerance
            )
                return Cancel("timestep-change");
            if (
                positions == null
                || velocities == null
                || positions.Length != prediction.Count
                || velocities.Length != prediction.Count
            )
                return Cancel("invalid-observation");
            double maxPosition = 0,
                maxVelocity = 0,
                positionSquares = 0,
                velocitySquares = 0;
            for (int i = 0; i < prediction.Count; i++)
            {
                double p = Distance(positions[i], prediction.GetPosition(i));
                double v = Distance(velocities[i], prediction.GetVelocity(i));
                if (!Finite(p) || !Finite(v))
                    return Cancel("invalid-observation");
                Accumulate(p, ref maxPosition, ref positionSquares);
                Accumulate(v, ref maxVelocity, ref velocitySquares);
            }
            sample.observedPositionMaxMeters = maxPosition;
            sample.observedVelocityMaxMetersPerSecond = maxVelocity;
            sample.observedPositionRmsMeters =
                maxPosition * System.Math.Sqrt(positionSquares / prediction.Count);
            sample.observedVelocityRmsMetersPerSecond =
                maxVelocity * System.Math.Sqrt(velocitySquares / prediction.Count);
            sample.comparedBodies = prediction.Count;
            sample.observedComparisonAvailable = true;
            sample.comparisonStatus = "compared";
            sample = null;
            prediction = null;
            return true;
        }

        public bool Cancel(string reason)
        {
            if (!IsPending)
                return false;
            sample.comparisonStatus = "skipped-" + reason;
            sample = null;
            prediction = null;
            return true;
        }

        static void Accumulate(double value, ref double maximum, ref double squares)
        {
            if (value == 0)
                return;
            if (value > maximum)
            {
                double ratio = maximum / value;
                squares = squares * ratio * ratio + 1;
                maximum = value;
            }
            else
            {
                double ratio = value / maximum;
                squares += ratio * ratio;
            }
        }

        static double Distance(Vec a, Vec b)
        {
            double x = a.X - b.X,
                y = a.Y - b.Y,
                z = a.Z - b.Z;
            double scale = System.Math.Max(
                System.Math.Abs(x),
                System.Math.Max(System.Math.Abs(y), System.Math.Abs(z))
            );
            if (scale == 0)
                return 0;
            x /= scale;
            y /= scale;
            z /= scale;
            return scale * System.Math.Sqrt(x * x + y * y + z * z);
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
