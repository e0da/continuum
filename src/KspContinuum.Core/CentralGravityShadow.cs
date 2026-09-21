using System;

namespace KspContinuum
{
    public static class KrakensbaneFramePersistence
    {
        public const string ModelId = "krakensbane-last-correction-persistence/v1";

        public static Vec PredictFrameVelocityDelta(Vec capturedLastCorrection)
        {
            Validate(capturedLastCorrection);
            return capturedLastCorrection * -1;
        }

        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X);
            AssemblyModel.Finite(value.Y);
            AssemblyModel.Finite(value.Z);
        }
    }

    public static class CentralGravityShadow
    {
        public const string ModelId = "central-point-mass-frozen-acceleration/v1";

        public static SimulationBatch Predict(
            SimulationBatch captured,
            Vec center,
            double mu,
            out Vec meanAcceleration
        )
        {
            if (captured == null || captured.Count > 512)
                throw new ArgumentException("Bounded captured batch required.");
            AssemblyModel.Positive(mu);
            Validate(center);
            var accelerations = new Vec[captured.Count];
            for (int i = 0; i < captured.Count; i++)
                accelerations[i] = Acceleration(captured.GetPosition(i), center, mu);
            return PredictCapturedAccelerations(captured, accelerations, out meanAcceleration);
        }

        public static SimulationBatch PredictCapturedAccelerations(
            SimulationBatch captured,
            Vec[] accelerations,
            out Vec meanAcceleration
        )
        {
            if (
                captured == null
                || captured.Count > 512
                || accelerations == null
                || accelerations.Length != captured.Count
            )
                throw new ArgumentException("Bounded matching acceleration census required.");
            var predicted = new SimulationBody[captured.Count];
            meanAcceleration = new Vec();
            double dt = captured.StepSeconds;
            for (int i = 0; i < captured.Count; i++)
            {
                Vec force = captured.GetForce(i);
                if (force.X != 0 || force.Y != 0 || force.Z != 0)
                    throw new ArgumentException(
                        "Gravity counterfactual requires the untouched zero-force baseline."
                    );
                Vec position = captured.GetPosition(i),
                    velocity = captured.GetVelocity(i),
                    acceleration = accelerations[i];
                Validate(acceleration);
                meanAcceleration += acceleration * (1.0 / captured.Count);
                predicted[i] = new SimulationBody(
                    captured.GetId(i),
                    captured.GetMass(i),
                    position + velocity * dt + acceleration * (.5 * dt * dt),
                    velocity + acceleration * dt,
                    force
                );
            }
            Validate(meanAcceleration);
            return new SimulationBatch(captured.Stamp, dt, predicted);
        }

        public static SimulationBatch AdjustVelocityFrame(SimulationBatch predicted, Vec frameDelta)
        {
            if (predicted == null || predicted.Count > 512)
                throw new ArgumentException("Bounded prediction required.");
            Validate(frameDelta);
            var bodies = new SimulationBody[predicted.Count];
            for (int i = 0; i < bodies.Length; i++)
                bodies[i] = new SimulationBody(
                    predicted.GetId(i),
                    predicted.GetMass(i),
                    predicted.GetPosition(i),
                    predicted.GetVelocity(i) + frameDelta * -1,
                    predicted.GetForce(i)
                );
            return new SimulationBatch(predicted.Stamp, predicted.StepSeconds, bodies);
        }

        public static Vec Acceleration(Vec position, Vec center, double mu)
        {
            AssemblyModel.Positive(mu);
            Validate(position);
            Validate(center);
            Vec offset = center + position * -1;
            Validate(offset);
            double scale = Math.Max(
                Math.Abs(offset.X),
                Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z))
            );
            if (scale == 0)
                throw new ArgumentException("Point-mass center is singular.");
            Vec normalized = offset * (1 / scale);
            double norm = Math.Sqrt(
                normalized.X * normalized.X
                    + normalized.Y * normalized.Y
                    + normalized.Z * normalized.Z
            );
            double inverseRadius = (1 / scale) / norm;
            double magnitude = (mu * inverseRadius) * inverseRadius;
            if (magnitude == 0)
                throw new ArgumentException("Gravity underflow is not a qualified zero field.");
            Vec result = normalized * (magnitude / norm);
            Validate(result);
            return result;
        }

        static void Validate(Vec value)
        {
            AssemblyModel.Finite(value.X);
            AssemblyModel.Finite(value.Y);
            AssemblyModel.Finite(value.Z);
        }
    }

    public sealed class CentralGravityComparison
    {
        readonly ShadowComparison comparison = new ShadowComparison();
        ShadowSample source,
            local;
        SimulationBatch prediction,
            zeroPrediction;
        Vec predictedFrameDelta;
        string topologyKey,
            frameKey;

        public void Attach(
            ShadowSample source,
            SimulationBatch predicted,
            SimulationBatch zeroPrediction,
            Vec predictedFrameDelta,
            string topology,
            string frame
        )
        {
            if (this.source != null)
                throw new InvalidOperationException("Gravity comparison already pending.");
            if (
                source == null
                || predicted == null
                || zeroPrediction == null
                || predicted.Count != zeroPrediction.Count
                || predicted.StepSeconds != zeroPrediction.StepSeconds
                || !predicted.Stamp.Matches(zeroPrediction.Stamp)
            )
                throw new ArgumentException("Paired predictions must share capture identity.");
            for (int i = 0; i < predicted.Count; i++)
                if (predicted.GetId(i) != zeroPrediction.GetId(i))
                    throw new ArgumentException("Paired body identities differ.");
            local = new ShadowSample
            {
                bodies = source.bodies,
                physicsEpoch = source.physicsEpoch,
                captureFixedTimeSeconds = source.captureFixedTimeSeconds,
                stepSeconds = source.stepSeconds,
            };
            comparison.Attach(local, predicted, topology, frame);
            this.source = source;
            this.prediction = predicted;
            this.zeroPrediction = zeroPrediction;
            this.predictedFrameDelta = predictedFrameDelta;
            topologyKey = topology;
            frameKey = frame;
            source.gravityComparisonStatus = "waiting";
            source.gravityPredictedFrameStatus = "waiting";
        }

        public void Observe(
            long epoch,
            string topology,
            string frame,
            bool eligible,
            double step,
            double fixedTime,
            int unityFrame,
            long origin,
            Vec[] positions,
            Vec[] velocities,
            double[] frameVelocity
        )
        {
            if (source == null)
                return;
            if (
                comparison.Observe(
                    epoch,
                    topology,
                    frame,
                    eligible,
                    step,
                    fixedTime,
                    unityFrame,
                    origin,
                    positions,
                    velocities,
                    frameVelocity
                )
            )
            {
                if (local.observedComparisonAvailable)
                {
                    try
                    {
                        var initial = source.rawKrakensbaneFrameVelocity;
                        if (initial == null || initial.Length != 3)
                            throw new ArgumentException("Missing start frame velocity.");
                        Vec delta = new Vec(
                            frameVelocity[0] - initial[0],
                            frameVelocity[1] - initial[1],
                            frameVelocity[2] - initial[2]
                        );
                        Vec predictionError = predictedFrameDelta + delta * -1;
                        source.frameVelocityDeltaPredictionError = new[]
                        {
                            predictionError.X,
                            predictionError.Y,
                            predictionError.Z,
                        };
                        source.frameVelocityDeltaPredictionErrorMetersPerSecond = Math.Sqrt(
                            predictionError.X * predictionError.X
                                + predictionError.Y * predictionError.Y
                                + predictionError.Z * predictionError.Z
                        );
                        var adjusted = CentralGravityShadow.AdjustVelocityFrame(prediction, delta);
                        ComparePredictedFrame(
                            epoch,
                            topology,
                            frame,
                            eligible,
                            step,
                            fixedTime,
                            unityFrame,
                            origin,
                            positions,
                            velocities,
                            frameVelocity
                        );
                        var adjustedSample = new ShadowSample
                        {
                            bodies = source.bodies,
                            physicsEpoch = source.physicsEpoch,
                            captureFixedTimeSeconds = source.captureFixedTimeSeconds,
                            stepSeconds = source.stepSeconds,
                        };
                        var adjustedComparison = new ShadowComparison();
                        adjustedComparison.Attach(adjustedSample, adjusted, topologyKey, frameKey);
                        adjustedComparison.Observe(
                            epoch,
                            topology,
                            frame,
                            eligible,
                            step,
                            fixedTime,
                            unityFrame,
                            origin,
                            positions,
                            velocities,
                            frameVelocity
                        );
                        source.gravityFrameAdjustedVelocityAvailable =
                            adjustedSample.observedComparisonAvailable;
                        source.gravityFrameAdjustedStatus = adjustedSample.comparisonStatus;
                        if (adjustedSample.observedComparisonAvailable)
                        {
                            source.gravityFrameAdjustedVelocityMaxMetersPerSecond =
                                adjustedSample.observedVelocityMaxMetersPerSecond;
                            source.gravityFrameAdjustedVelocityRmsMetersPerSecond =
                                adjustedSample.observedVelocityRmsMetersPerSecond;
                            var zeroAdjusted = CentralGravityShadow.AdjustVelocityFrame(
                                zeroPrediction,
                                delta
                            );
                            var zeroSample = new ShadowSample
                            {
                                bodies = source.bodies,
                                physicsEpoch = source.physicsEpoch,
                                captureFixedTimeSeconds = source.captureFixedTimeSeconds,
                                stepSeconds = source.stepSeconds,
                            };
                            var zeroComparison = new ShadowComparison();
                            zeroComparison.Attach(zeroSample, zeroAdjusted, topologyKey, frameKey);
                            zeroComparison.Observe(
                                epoch,
                                topology,
                                frame,
                                eligible,
                                step,
                                fixedTime,
                                unityFrame,
                                origin,
                                positions,
                                velocities,
                                frameVelocity
                            );
                            source.zeroFrameAdjustedVelocityAvailable =
                                zeroSample.observedComparisonAvailable;
                            if (zeroSample.observedComparisonAvailable)
                            {
                                source.zeroFrameAdjustedVelocityMaxMetersPerSecond =
                                    zeroSample.observedVelocityMaxMetersPerSecond;
                                source.zeroFrameAdjustedVelocityRmsMetersPerSecond =
                                    zeroSample.observedVelocityRmsMetersPerSecond;
                                source.gravityFrameAdjustedVelocityRmsDeltaFromZero =
                                    adjustedSample.observedVelocityRmsMetersPerSecond
                                    - zeroSample.observedVelocityRmsMetersPerSecond;
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        source.gravityFrameAdjustedStatus = "unavailable-invalid-frame-delta";
                    }
                }
                else
                {
                    source.gravityFrameAdjustedStatus = local.comparisonStatus;
                    source.gravityPredictedFrameStatus = local.comparisonStatus;
                }
                Publish();
            }
        }

        void ComparePredictedFrame(
            long epoch,
            string topology,
            string frame,
            bool eligible,
            double step,
            double fixedTime,
            int unityFrame,
            long origin,
            Vec[] positions,
            Vec[] velocities,
            double[] frameVelocity
        )
        {
            var gravity = CentralGravityShadow.AdjustVelocityFrame(prediction, predictedFrameDelta);
            var zero = CentralGravityShadow.AdjustVelocityFrame(
                zeroPrediction,
                predictedFrameDelta
            );
            var gravitySample = ComparisonSample();
            var zeroSample = ComparisonSample();
            var gravityComparison = new ShadowComparison();
            var zeroComparison = new ShadowComparison();
            gravityComparison.Attach(gravitySample, gravity, topologyKey, frameKey);
            zeroComparison.Attach(zeroSample, zero, topologyKey, frameKey);
            gravityComparison.Observe(
                epoch,
                topology,
                frame,
                eligible,
                step,
                fixedTime,
                unityFrame,
                origin,
                positions,
                velocities,
                frameVelocity
            );
            zeroComparison.Observe(
                epoch,
                topology,
                frame,
                eligible,
                step,
                fixedTime,
                unityFrame,
                origin,
                positions,
                velocities,
                frameVelocity
            );
            source.gravityPredictedFrameStatus = gravitySample.comparisonStatus;
            source.gravityPredictedFrameVelocityAvailable =
                gravitySample.observedComparisonAvailable;
            source.zeroPredictedFrameVelocityAvailable = zeroSample.observedComparisonAvailable;
            if (gravitySample.observedComparisonAvailable && zeroSample.observedComparisonAvailable)
            {
                source.gravityPredictedFrameVelocityMaxMetersPerSecond =
                    gravitySample.observedVelocityMaxMetersPerSecond;
                source.gravityPredictedFrameVelocityRmsMetersPerSecond =
                    gravitySample.observedVelocityRmsMetersPerSecond;
                source.zeroPredictedFrameVelocityMaxMetersPerSecond =
                    zeroSample.observedVelocityMaxMetersPerSecond;
                source.zeroPredictedFrameVelocityRmsMetersPerSecond =
                    zeroSample.observedVelocityRmsMetersPerSecond;
                source.gravityPredictedFrameVelocityRmsDeltaFromZero =
                    gravitySample.observedVelocityRmsMetersPerSecond
                    - zeroSample.observedVelocityRmsMetersPerSecond;
            }
        }

        ShadowSample ComparisonSample()
        {
            return new ShadowSample
            {
                bodies = source.bodies,
                physicsEpoch = source.physicsEpoch,
                captureFixedTimeSeconds = source.captureFixedTimeSeconds,
                stepSeconds = source.stepSeconds,
            };
        }

        public void Cancel(string reason)
        {
            if (source != null)
            {
                comparison.Cancel(reason);
                source.gravityFrameAdjustedStatus = local.comparisonStatus;
                source.gravityPredictedFrameStatus = local.comparisonStatus;
                Publish();
            }
        }

        void Publish()
        {
            source.gravityComparisonStatus = local.comparisonStatus;
            source.gravityComparisonAvailable = local.observedComparisonAvailable;
            if (local.observedComparisonAvailable)
            {
                source.gravityComparedBodies = local.comparedBodies;
                source.gravityPositionMaxMeters = local.observedPositionMaxMeters;
                source.gravityPositionRmsMeters = local.observedPositionRmsMeters;
                source.gravityVelocityMaxMetersPerSecond = local.observedVelocityMaxMetersPerSecond;
                source.gravityVelocityRmsMetersPerSecond = local.observedVelocityRmsMetersPerSecond;
                if (source.observedComparisonAvailable)
                {
                    source.gravityVelocityRmsDeltaFromZero =
                        local.observedVelocityRmsMetersPerSecond
                        - source.observedVelocityRmsMetersPerSecond;
                    if (source.observedVelocityRmsMetersPerSecond > 0)
                    {
                        double ratio =
                            local.observedVelocityRmsMetersPerSecond
                            / source.observedVelocityRmsMetersPerSecond;
                        if (!double.IsInfinity(ratio) && !double.IsNaN(ratio))
                            source.gravityVelocityRmsRatioToZero = ratio;
                    }
                }
                if (
                    source.rawKrakensbaneFrameVelocity != null
                    && source.rawKrakensbaneFrameVelocity.Length == 3
                )
                {
                    var delta = new double[3];
                    bool finite = true;
                    for (int i = 0; i < 3; i++)
                    {
                        delta[i] =
                            local.comparisonRawKrakensbaneFrameVelocity[i]
                            - source.rawKrakensbaneFrameVelocity[i];
                        finite &= !double.IsInfinity(delta[i]) && !double.IsNaN(delta[i]);
                    }
                    if (finite)
                        source.gravityEndpointFrameVelocityDelta = delta;
                }
            }
            source = null;
            local = null;
            prediction = null;
            zeroPrediction = null;
            predictedFrameDelta = new Vec();
        }
    }
}
