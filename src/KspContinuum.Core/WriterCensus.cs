using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public interface IPlayerLoopBracketObserver
    {
        void Before(string scope, int frame, double fixedTimeSeconds);
        void After(string scope, int frame, double fixedTimeSeconds);
        void Fault(string scope, Exception error);
    }

    [Serializable] public sealed class WriterCensusReport
    {
        public string schema = "ksp-continuum-writer-census/v1";
        public string status = "no-samples";
        public string measurementScope = "Read-only pose and velocity changes across named PlayerLoop intervals; changed state does not identify the writer.";
        public string positionFrame = "Body center of mass relative to the first ordered active-vessel body; invariant to a common FloatingOrigin translation.";
        public string velocityFrame = "Rigidbody velocity plus the captured Krakensbane frame velocity.";
        public string internalFrame = "Pose and velocity relative to the first ordered physical body, expressed in that body's rotating frame.";
        public int maximumIntervals = 1024, droppedIntervals, invalidIntervals;
        public WriterCensusInterval[] intervals;
    }

    [Serializable] public sealed class WriterCensusInterval
    {
        public string scope, status, reason, vesselId, topologyKey;
        public int frame, bodyCount, changedPositions, changedOrientations, changedVelocities, changedAngularVelocities;
        public int changedInternalPositions, changedInternalOrientations, changedInternalVelocities, changedInternalAngularVelocities;
        public long originGeneration, originTransforms;
        public double fixedTimeSeconds, maximumPositionDelta, maximumOrientationDeltaRadians,
            maximumVelocityDelta, maximumAngularVelocityDelta, frameVelocityDelta;
        public double maximumInternalPositionDelta, maximumInternalOrientationDeltaRadians,
            maximumInternalVelocityDelta, maximumInternalAngularVelocityDelta;
    }

    [Serializable] public sealed class WriterCensusSnapshot
    {
        public string vesselId, topologyKey;
        public long originGeneration;
        public Vec frameVelocity;
        public WriterCensusBody[] bodies;
    }

    [Serializable] public sealed class WriterCensusBody
    {
        public string id;
        public Vec relativePosition, normalizedVelocity, angularVelocity;
        public Vec internalPosition, internalVelocity, internalAngularVelocity;
        public double[] orientation, internalOrientation;
    }

    public sealed class WriterCensus : IPlayerLoopBracketObserver
    {
        readonly Func<WriterCensusSnapshot> capture;
        readonly Dictionary<string, WriterCensusSnapshot> pending = new Dictionary<string, WriterCensusSnapshot>();
        readonly List<WriterCensusInterval> intervals = new List<WriterCensusInterval>();
        readonly int capacity;
        bool faulted;
        public WriterCensusReport Report { get; private set; }

        public WriterCensus(Func<WriterCensusSnapshot> capture, int capacity = 1024)
        {
            if (capture == null) throw new ArgumentNullException("capture");
            if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException("capacity");
            this.capture = capture; this.capacity = capacity;
            Report = new WriterCensusReport { maximumIntervals = capacity, intervals = new WriterCensusInterval[0] };
        }

        public void Before(string scope, int frame, double fixedTimeSeconds)
        {
            if (faulted || !Observed(scope)) return;
            try { pending[scope] = capture(); }
            catch (Exception error) { Fault(scope, error); }
        }

        public void After(string scope, int frame, double fixedTimeSeconds)
        {
            if (faulted || !Observed(scope)) return;
            try
            {
                WriterCensusSnapshot before;
                if (!pending.TryGetValue(scope, out before)) { Add(Invalid(scope, frame, fixedTimeSeconds, "missing-before-snapshot")); return; }
                pending.Remove(scope);
                Add(Compare(scope, frame, fixedTimeSeconds, before, capture()));
            }
            catch (Exception error) { Fault(scope, error); }
        }

        public void Fault(string scope, Exception error)
        {
            faulted = true; Report.status = "invalid";
            Add(Invalid(scope, -1, 0, "capture-failed:" + error.GetType().Name));
        }

        public void Finish()
        {
            if (pending.Count != 0) { Report.invalidIntervals += pending.Count; pending.Clear(); faulted = true; }
            Report.intervals = intervals.ToArray();
            Report.status = faulted ? "invalid" : intervals.Count == 0 ? "no-samples" : "observed";
        }

        static bool Observed(string scope)
        {
            return scope == "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate" ||
                scope == "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate";
        }

        void Add(WriterCensusInterval interval)
        {
            if (interval.status == "invalid") Report.invalidIntervals++;
            if (intervals.Count < capacity) intervals.Add(interval); else Report.droppedIntervals++;
        }

        static WriterCensusInterval Invalid(string scope, int frame, double time, string reason)
        {
            return new WriterCensusInterval { scope = scope, frame = frame, fixedTimeSeconds = time, status = "invalid", reason = reason };
        }

        static WriterCensusInterval Compare(string scope, int frame, double time, WriterCensusSnapshot before, WriterCensusSnapshot after)
        {
            var result = new WriterCensusInterval { scope = scope, frame = frame, fixedTimeSeconds = time, status = "observed" };
            if (before == null || after == null) { result.status = "invalid"; result.reason = "snapshot-unavailable"; return result; }
            result.vesselId = before.vesselId; result.topologyKey = before.topologyKey; result.originGeneration = before.originGeneration;
            if (before.vesselId != after.vesselId) return Reject(result, "active-vessel-changed");
            if (before.topologyKey != after.topologyKey) return Reject(result, "topology-changed");
            if (after.originGeneration < before.originGeneration) return Reject(result, "floating-origin-generation-regressed");
            result.originTransforms = after.originGeneration - before.originGeneration;
            result.frameVelocityDelta = Distance(before.frameVelocity, after.frameVelocity);
            if (before.bodies == null || after.bodies == null || before.bodies.Length != after.bodies.Length) return Reject(result, "body-membership-changed");
            result.bodyCount = before.bodies.Length;
            for (int i = 0; i < before.bodies.Length; i++)
            {
                if (before.bodies[i].id != after.bodies[i].id) return Reject(result, "body-membership-changed");
                double position = Distance(before.bodies[i].relativePosition, after.bodies[i].relativePosition);
                double orientation;
                if (!OrientationDistance(before.bodies[i].orientation, after.bodies[i].orientation, out orientation))
                    return Reject(result, "invalid-orientation");
                double velocity = Distance(before.bodies[i].normalizedVelocity, after.bodies[i].normalizedVelocity);
                double angularVelocity = Distance(before.bodies[i].angularVelocity, after.bodies[i].angularVelocity);
                double internalPosition = Distance(before.bodies[i].internalPosition, after.bodies[i].internalPosition);
                double internalOrientation;
                if (!OrientationDistance(before.bodies[i].internalOrientation, after.bodies[i].internalOrientation, out internalOrientation))
                    return Reject(result, "invalid-internal-orientation");
                double internalVelocity = Distance(before.bodies[i].internalVelocity, after.bodies[i].internalVelocity);
                double internalAngularVelocity = Distance(before.bodies[i].internalAngularVelocity, after.bodies[i].internalAngularVelocity);
                if (position > 0) result.changedPositions++;
                if (orientation > 0) result.changedOrientations++;
                if (velocity > 0) result.changedVelocities++;
                if (angularVelocity > 0) result.changedAngularVelocities++;
                if (internalPosition > 0) result.changedInternalPositions++;
                if (internalOrientation > 0) result.changedInternalOrientations++;
                if (internalVelocity > 0) result.changedInternalVelocities++;
                if (internalAngularVelocity > 0) result.changedInternalAngularVelocities++;
                result.maximumPositionDelta = Math.Max(result.maximumPositionDelta, position);
                result.maximumOrientationDeltaRadians = Math.Max(result.maximumOrientationDeltaRadians, orientation);
                result.maximumVelocityDelta = Math.Max(result.maximumVelocityDelta, velocity);
                result.maximumAngularVelocityDelta = Math.Max(result.maximumAngularVelocityDelta, angularVelocity);
                result.maximumInternalPositionDelta = Math.Max(result.maximumInternalPositionDelta, internalPosition);
                result.maximumInternalOrientationDeltaRadians = Math.Max(result.maximumInternalOrientationDeltaRadians, internalOrientation);
                result.maximumInternalVelocityDelta = Math.Max(result.maximumInternalVelocityDelta, internalVelocity);
                result.maximumInternalAngularVelocityDelta = Math.Max(result.maximumInternalAngularVelocityDelta, internalAngularVelocity);
            }
            result.reason = result.changedPositions == 0 && result.changedOrientations == 0 &&
                result.changedVelocities == 0 && result.changedAngularVelocities == 0
                ? "no-observed-change" : "state-changed-within-interval";
            return result;
        }

        static WriterCensusInterval Reject(WriterCensusInterval value, string reason) { value.status = "invalid"; value.reason = reason; return value; }
        static double Distance(Vec a, Vec b)
        {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }
        static bool OrientationDistance(double[] a, double[] b, out double radians)
        {
            radians = 0;
            if (a == null || b == null || a.Length != 4 || b.Length != 4) return false;
            double an = 0, bn = 0, dot = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Finite(a[i]) || !Finite(b[i])) return false;
                an += a[i] * a[i]; bn += b[i] * b[i]; dot += a[i] * b[i];
            }
            if (!Finite(an) || !Finite(bn) || an <= 1e-24 || bn <= 1e-24) return false;
            double normalizedDot = Math.Abs(dot / Math.Sqrt(an * bn));
            if (!Finite(normalizedDot)) return false;
            normalizedDot = Math.Min(1, normalizedDot);
            radians = 2 * Math.Acos(normalizedDot);
            return Finite(radians);
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
