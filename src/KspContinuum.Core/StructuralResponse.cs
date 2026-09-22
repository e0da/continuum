using System;

namespace KspContinuum
{
    public sealed class StructuralTrace
    {
        public string schema = "ksp-continuum-structural-trace/v1";
        public string evidence = "native-adapter-observation";
        public string topology;
        public string referenceFrame = "body-relative";
        public double stepSeconds;
        public StructuralTraceSample[] samples = new StructuralTraceSample[0];
    }

    public sealed class StructuralTraceSample
    {
        public long physicsEpoch;
        public double relativeDisplacement;
        public double relativeVelocity;
    }

    public sealed class StructuralModeFit
    {
        public string schema = "ksp-continuum-structural-mode-fit/v1";
        public int trainingSamples;
        public int heldOutSamples;
        public double recurrenceCurrent;
        public double recurrencePrevious;
        public double heldOutRms;
        public double heldOutPeak;
        public double signalRms;
        public double normalizedRms;
        public double[] prediction = new double[0];
    }

    public static class StructuralResponse
    {
        public static StructuralModeFit FitAndPredict(StructuralTrace trace, int trainingSamples)
        {
            Validate(trace);
            if (trainingSamples < 4 || trainingSamples >= trace.samples.Length)
                throw new ArgumentOutOfRangeException("trainingSamples");

            double xx = 0, xy = 0, yy = 0, xz = 0, yz = 0;
            for (int n = 1; n < trainingSamples - 1; n++)
            {
                double x = trace.samples[n].relativeDisplacement;
                double y = trace.samples[n - 1].relativeDisplacement;
                double z = trace.samples[n + 1].relativeDisplacement;
                xx += x * x; xy += x * y; yy += y * y; xz += x * z; yz += y * z;
            }
            double determinant = xx * yy - xy * xy;
            double scale = Math.Max(1, Math.Max(Math.Abs(xx * yy), Math.Abs(xy * xy)));
            if (!Finite(determinant) || Math.Abs(determinant) <= scale * 1e-12)
                throw new InvalidOperationException("Training motion does not identify a two-state mode.");
            double a = (xz * yy - yz * xy) / determinant;
            double b = (yz * xx - xz * xy) / determinant;
            if (!Finite(a) || !Finite(b))
                throw new InvalidOperationException("Mode coefficients are nonfinite.");

            var prediction = new double[trace.samples.Length];
            for (int i = 0; i < trainingSamples; i++) prediction[i] = trace.samples[i].relativeDisplacement;
            for (int i = trainingSamples; i < prediction.Length; i++)
                prediction[i] = a * prediction[i - 1] + b * prediction[i - 2];

            double errorSquares = 0, signalSquares = 0, peak = 0;
            for (int i = trainingSamples; i < prediction.Length; i++)
            {
                double observed = trace.samples[i].relativeDisplacement;
                double error = prediction[i] - observed;
                if (!Finite(prediction[i]) || !Finite(error))
                    throw new InvalidOperationException("Held-out prediction diverged.");
                errorSquares += error * error;
                signalSquares += observed * observed;
                peak = Math.Max(peak, Math.Abs(error));
            }
            int heldOut = prediction.Length - trainingSamples;
            double rms = Math.Sqrt(errorSquares / heldOut);
            double signalRms = Math.Sqrt(signalSquares / heldOut);
            return new StructuralModeFit {
                trainingSamples = trainingSamples, heldOutSamples = heldOut,
                recurrenceCurrent = a, recurrencePrevious = b,
                heldOutRms = rms, heldOutPeak = peak, signalRms = signalRms,
                normalizedRms = signalRms == 0 ? (rms == 0 ? 0 : double.PositiveInfinity) : rms / signalRms,
                prediction = prediction,
            };
        }

        public static void Validate(StructuralTrace trace)
        {
            if (trace == null) throw new ArgumentNullException("trace");
            if (trace.schema != "ksp-continuum-structural-trace/v1") throw new InvalidOperationException("Unexpected structural trace schema.");
            if (trace.evidence != "native-adapter-observation" && trace.evidence != "portable-helper-fixture")
                throw new InvalidOperationException("Unexpected structural trace provenance.");
            if (trace.referenceFrame != "body-relative") throw new InvalidOperationException("Structural coordinates must be body-relative.");
            if (String.IsNullOrEmpty(trace.topology) || !Finite(trace.stepSeconds) || trace.stepSeconds <= 0)
                throw new InvalidOperationException("Structural trace context is incomplete.");
            if (trace.samples == null || trace.samples.Length < 5 || trace.samples.Length > 4096)
                throw new InvalidOperationException("Structural sample count is outside the bound.");
            long previous = -1;
            for (int i = 0; i < trace.samples.Length; i++)
            {
                StructuralTraceSample sample = trace.samples[i];
                if (sample == null || sample.physicsEpoch <= previous || !Finite(sample.relativeDisplacement) || !Finite(sample.relativeVelocity))
                    throw new InvalidOperationException("Structural trace samples are invalid or unordered.");
                previous = sample.physicsEpoch;
            }
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
