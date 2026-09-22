using System;

namespace KspContinuum
{
    public enum LiveControlOperation { Invalid, Hello, ActiveVesselSnapshot }

    public sealed class LiveControlRequest
    {
        public readonly LiveControlOperation operation;
        public readonly string requestId, sessionId;
        public readonly long expectedEpoch, minimumFixedCallbacks;
        public readonly string error;

        LiveControlRequest(LiveControlOperation operation, string requestId, string sessionId,
            long expectedEpoch, long minimumFixedCallbacks, string error)
        {
            this.operation = operation; this.requestId = requestId; this.sessionId = sessionId;
            this.expectedEpoch = expectedEpoch; this.minimumFixedCallbacks = minimumFixedCallbacks;
            this.error = error;
        }

        // Wire parsing is confined here; operations beyond the stream are typed.
        public static LiveControlRequest Parse(string line)
        {
            if (line == null || line.Length > 256 || line.Length == 0)
                return new LiveControlRequest(LiveControlOperation.Invalid, null, null, 0, 0, "invalid-request");
            string[] words = line.Split(' ');
            string id = words.Length > 2 && Token(words[2], 64) ? words[2] : null;
            if (words.Length < 3 || words[1] != "1.0.0" || id == null)
                return new LiveControlRequest(LiveControlOperation.Invalid, id, null, 0, 0,
                    words.Length > 1 && words[1] != "1.0.0" ? "unsupported-version" : "invalid-request");
            if (words[0] == "hello" && words.Length == 3)
                return new LiveControlRequest(LiveControlOperation.Hello, id, null, 0, 0, null);
            if (words[0] == "snapshot" && words.Length == 6)
            {
                Guid session;
                long epoch, tick;
                if (Guid.TryParseExact(words[3], "D", out session) && session != Guid.Empty &&
                    long.TryParse(words[4], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out epoch) && epoch >= 1 &&
                    long.TryParse(words[5], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out tick))
                    return new LiveControlRequest(LiveControlOperation.ActiveVesselSnapshot, id,
                        session.ToString("D"), epoch, tick, null);
            }
            return new LiveControlRequest(LiveControlOperation.Invalid, id, null, 0, 0,
                words[0] == "snapshot" || words[0] == "hello" ? "invalid-request" : "unknown-operation");
        }

        static bool Token(string value, int maximum)
        {
            if (value.Length == 0 || value.Length > maximum) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            return true;
        }
    }

    [Serializable] public sealed class LiveControlIdentity
    {
        public string sessionId, scene, vesselId;
        public long epoch, renderFrame, observedFixedCallbacks;
    }

    [Serializable] public sealed class LiveVesselSnapshot
    {
        public string vesselId, name, body, situation;
        public bool loaded, packed;
        public int parts;
        public double universalTime, altitude, surfaceSpeed, orbitalSpeed;
        public double? throttle;
    }

    [Serializable] public sealed class LiveControlReply
    {
        public string schema = "continuum-live-control/v1";
        public string protocolVersion = "1.0.0", supportedProtocolMin = "1.0.0", supportedProtocolMax = "1.0.0";
        public string requestId;
        public string status, reason;
        public string[] capabilities = { "hello", "active-vessel-snapshot" };
        public LiveControlIdentity identity;
        public LiveVesselSnapshot snapshot;
        public long observerNanoseconds;
    }

    public sealed class LiveControlPlane
    {
        readonly string sessionId;
        string scene, vesselId;
        long epoch;

        public LiveControlPlane(string sessionId)
        {
            Guid parsed;
            if (!Guid.TryParse(sessionId, out parsed) || parsed == Guid.Empty)
                throw new ArgumentException("A nonempty session GUID is required.", "sessionId");
            this.sessionId = parsed.ToString("D");
        }

        // The host calls this on Unity's main thread before servicing a request.
        public void Observe(string currentScene, string currentVesselId, bool forceNewEpoch = false)
        {
            if (string.IsNullOrEmpty(currentScene) || currentScene.Length > 64)
                throw new ArgumentException("Invalid scene.", "currentScene");
            if (currentVesselId != null)
            {
                Guid parsed;
                if (!Guid.TryParse(currentVesselId, out parsed) || parsed == Guid.Empty)
                    throw new ArgumentException("Invalid vessel GUID.", "currentVesselId");
                currentVesselId = parsed.ToString("D");
            }
            if (epoch == 0 || forceNewEpoch || scene != currentScene || vesselId != currentVesselId)
            {
                epoch = checked(epoch + 1);
                scene = currentScene;
                vesselId = currentVesselId;
            }
        }

        public LiveControlReply Execute(LiveControlRequest request, long renderFrame, long observedFixedCallbacks,
            Func<LiveVesselSnapshot> capture)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (epoch == 0 || renderFrame < 0 || observedFixedCallbacks < 0)
                throw new InvalidOperationException("Observe the current game state before handling a request.");
            var identity = new LiveControlIdentity { sessionId = sessionId, scene = scene, vesselId = vesselId,
                epoch = epoch, renderFrame = renderFrame, observedFixedCallbacks = observedFixedCallbacks };
            var reply = new LiveControlReply { identity = identity, requestId = request.requestId };
            if (request.operation == LiveControlOperation.Invalid)
            { reply.status = "rejected"; reply.reason = request.error; return reply; }
            if (request.operation == LiveControlOperation.Hello) { reply.status = "ok"; return reply; }
            if (request.operation != LiveControlOperation.ActiveVesselSnapshot)
            { reply.status = "rejected"; reply.reason = "unknown-operation"; return reply; }
            if (request.sessionId != sessionId || request.expectedEpoch != epoch)
            { reply.status = "rejected"; reply.reason = "stale-identity"; return reply; }
            if (observedFixedCallbacks < request.minimumFixedCallbacks)
            { reply.status = "rejected"; reply.reason = "tick-not-yet-observed"; return reply; }
            if (vesselId == null) { reply.status = "unavailable"; reply.reason = "no-active-vessel"; return reply; }
            if (capture == null) throw new ArgumentNullException("capture");
            LiveVesselSnapshot snapshot = capture();
            if (snapshot == null || snapshot.vesselId != vesselId || snapshot.parts < 0 ||
                !Bounded(snapshot.name, 128) || !Bounded(snapshot.body, 128) ||
                !Bounded(snapshot.situation, 64) ||
                !Finite(snapshot.universalTime) || !Finite(snapshot.altitude) ||
                !Finite(snapshot.surfaceSpeed) || !Finite(snapshot.orbitalSpeed) ||
                (snapshot.throttle.HasValue && !Finite(snapshot.throttle.Value)))
            { reply.status = "rejected"; reply.reason = "capture-changed-or-invalid"; return reply; }
            reply.status = "ok";
            reply.snapshot = snapshot;
            return reply;
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool Bounded(string value, int maximum) { return value != null && value.Length <= maximum; }
    }
}
