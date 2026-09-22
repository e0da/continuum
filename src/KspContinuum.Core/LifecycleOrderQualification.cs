using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KspContinuum
{
    public sealed class LifecycleOrderQualificationReport
    {
        public const int RequiredTrials = 3;
        public string schema = "ksp-continuum-lifecycle-order-qualification/v1";
        public string evidence = "native-isolated-rigidbody-observation";
        public string status = "waiting";
        public string reason;
        public string integrityStatus = "pending";
        public string cleanupStatus = "pending";
        public string nativeTarget = "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        public string injectionCallback = "before:UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        public string observationCallback = "after:UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate";
        public string qualificationId;
        public string unity;
        public string ksp;
        public string plugin;
        public int installedBeforeCallbacks;
        public int installedAfterCallbacks;
        public int retainedTrials;
        public LifecycleOrderTrial[] trials = new LifecycleOrderTrial[0];
    }

    public sealed class LifecycleOrderTrial
    {
        public int trial;
        public int unityFrameBefore;
        public int unityFrameAfter;
        public double fixedTimeBefore;
        public double fixedTimeAfter;
        public double fixedDeltaSeconds;
        public double[] initialPosition = new double[0];
        public double[] requestedVelocity = new double[0];
        public double[] positionBeforeTarget = new double[0];
        public double[] positionAfterTarget = new double[0];
        public double[] velocityAfterTarget = new double[0];
    }

    public static class LifecycleOrderQualification
    {
        const double PositionTolerance = 1e-5;
        const double VelocityTolerance = 1e-6;

        public static void Validate(LifecycleOrderQualificationReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            Require(report.schema == "ksp-continuum-lifecycle-order-qualification/v1", "Unexpected lifecycle-order schema.");
            Require(report.evidence == "native-isolated-rigidbody-observation" || report.evidence == "portable-helper-fixture",
                "Unexpected lifecycle-order provenance.");
            Require(report.nativeTarget == "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate",
                "Lifecycle-order target is not the native physics node.");
            Require(report.injectionCallback == "before:" + report.nativeTarget
                && report.observationCallback == "after:" + report.nativeTarget,
                "Lifecycle-order callbacks do not bracket the native target.");
            if (report.status != "qualified")
            {
                Require(report.status == "waiting" || report.status == "unavailable" || report.status == "invalid",
                    "Lifecycle-order status is invalid.");
                Require(report.status == "waiting" && report.integrityStatus == "pending" && report.cleanupStatus == "pending"
                    && String.IsNullOrEmpty(report.reason)
                    || report.status != "waiting" && report.integrityStatus == "invalidated"
                    && (report.cleanupStatus == "complete" || report.cleanupStatus == "cleanup-error")
                    && !String.IsNullOrEmpty(report.reason),
                    "Non-qualified lifecycle-order state is contradictory.");
                Require(report.trials != null && report.trials.Length == 0 && report.retainedTrials == 0
                    && String.IsNullOrEmpty(report.qualificationId),
                    "Invalid lifecycle-order receipt retained qualifying evidence.");
                return;
            }
            Require(String.IsNullOrEmpty(report.reason), "Qualified lifecycle-order receipt has a failure reason.");
            Require(report.integrityStatus == "verified-at-every-boundary", "Lifecycle-order bracket integrity is unverified.");
            Require(report.cleanupStatus == "removed-owned-hooks-probe-destroy-requested", "Lifecycle-order cleanup is incomplete.");
            Require(report.installedBeforeCallbacks == 1 && report.installedAfterCallbacks == 1,
                "Lifecycle-order callback ownership is ambiguous.");
            Require(report.trials != null && report.trials.Length == LifecycleOrderQualificationReport.RequiredTrials
                && report.retainedTrials == LifecycleOrderQualificationReport.RequiredTrials, "Lifecycle-order trial set is incomplete.");
            for (int i = 0; i < report.trials.Length; i++) ValidateTrial(report.trials[i], i);
            Require(report.qualificationId == ComputeId(report), "Lifecycle-order qualification identity does not match its evidence.");
        }

        public static string ComputeId(LifecycleOrderQualificationReport report)
        {
            if (report == null || report.trials == null) throw new ArgumentException("Missing lifecycle-order evidence.");
            var canonical = new StringBuilder();
            AppendString(canonical, report.schema); AppendString(canonical, report.evidence); AppendString(canonical, report.nativeTarget);
            AppendString(canonical, report.injectionCallback); AppendString(canonical, report.observationCallback);
            AppendString(canonical, report.unity); AppendString(canonical, report.ksp); AppendString(canonical, report.plugin);
            foreach (var trial in report.trials)
            {
                if (trial == null) throw new ArgumentException("Missing lifecycle-order trial.");
                canonical.Append('|').Append(trial.trial).Append('|').Append(trial.unityFrameBefore).Append('|')
                    .Append(trial.unityFrameAfter).Append('|').Append(Number(trial.fixedTimeBefore)).Append('|')
                    .Append(Number(trial.fixedTimeAfter)).Append('|').Append(Number(trial.fixedDeltaSeconds));
                Append(canonical, trial.initialPosition); Append(canonical, trial.requestedVelocity);
                Append(canonical, trial.positionBeforeTarget); Append(canonical, trial.positionAfterTarget);
                Append(canonical, trial.velocityAfterTarget);
            }
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var result = new StringBuilder("sha256:");
                foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        static void ValidateTrial(LifecycleOrderTrial trial, int index)
        {
            Require(trial != null && trial.trial == index + 1, "Lifecycle-order trial identity is invalid.");
            Require(trial.unityFrameBefore >= 0 && trial.unityFrameAfter == trial.unityFrameBefore,
                "Lifecycle-order callbacks did not share a rendered frame.");
            Require(Finite(trial.fixedTimeBefore) && Finite(trial.fixedTimeAfter)
                && Finite(trial.fixedDeltaSeconds) && trial.fixedDeltaSeconds > 0,
                "Lifecycle-order trial clocks are invalid.");
            Require(Math.Abs(trial.fixedTimeAfter - trial.fixedTimeBefore) <= 1e-9,
                "Lifecycle-order callbacks do not bracket one native target dispatch.");
            Vector(trial.initialPosition, "initial position"); Vector(trial.requestedVelocity, "requested velocity");
            Vector(trial.positionBeforeTarget, "pre-target position"); Vector(trial.positionAfterTarget, "post-target position");
            Vector(trial.velocityAfterTarget, "post-target velocity");
            double speed2 = 0, movement2 = 0;
            for (int component = 0; component < 3; component++)
            {
                Require(Math.Abs(trial.positionBeforeTarget[component] - trial.initialPosition[component]) <= PositionTolerance,
                    "Probe moved before the native physics target.");
                double expected = trial.initialPosition[component] + trial.requestedVelocity[component] * trial.fixedDeltaSeconds;
                double error = trial.positionAfterTarget[component] - expected;
                movement2 += (trial.positionAfterTarget[component] - trial.positionBeforeTarget[component])
                    * (trial.positionAfterTarget[component] - trial.positionBeforeTarget[component]);
                speed2 += trial.requestedVelocity[component] * trial.requestedVelocity[component];
                Require(Math.Abs(error) <= PositionTolerance, "Native physics target did not produce the expected one-step displacement.");
                Require(Math.Abs(trial.velocityAfterTarget[component] - trial.requestedVelocity[component]) <= VelocityTolerance,
                    "Probe velocity changed during lifecycle-order trial.");
            }
            Require(speed2 > 0 && movement2 > PositionTolerance * PositionTolerance,
                "Lifecycle-order trial contains no observable motion.");
        }

        static void Append(StringBuilder output, double[] values)
        {
            if (values == null) { output.Append("|null"); return; }
            foreach (double value in values) output.Append('|').Append(Number(value));
        }
        static void AppendString(StringBuilder output, string value) { value = value ?? String.Empty; output.Append('|').Append(value.Length).Append(':').Append(value); }
        static string Number(double value) { Require(Finite(value), "Nonfinite lifecycle-order value."); return value.ToString("R", CultureInfo.InvariantCulture); }
        static void Vector(double[] value, string name) { Require(value != null && value.Length == 3, "Lifecycle-order " + name + " has the wrong shape."); foreach (double item in value) Require(Finite(item), "Lifecycle-order " + name + " is nonfinite."); }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
