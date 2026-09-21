using System;

namespace KspContinuum
{
    // Main-thread observations invalidate pending work, including transitions back to an earlier state.
    public sealed class ShadowEpoch
    {
        string topology, frame;
        bool eligible;
        long topologyGeneration, frameGeneration, tick;
        public void Observe(string newTopology, string newFrame, bool newEligible)
        {
            if (newTopology == null || newFrame == null) throw new ArgumentNullException("Observation keys");
            if (topology != newTopology) { topology = newTopology; topologyGeneration++; }
            if (frame != newFrame || eligible != newEligible) { frame = newFrame; frameGeneration++; }
            eligible = newEligible;
        }
        public WorkStamp CaptureStamp()
        {
            if (!eligible) throw new InvalidOperationException("Shadow capture is not eligible.");
            return new WorkStamp(++tick, topologyGeneration, frameGeneration);
        }
        public WorkStamp Expected(WorkStamp captured)
        {
            if (captured == null) throw new ArgumentNullException("captured");
            return new WorkStamp(captured.Tick, topologyGeneration, frameGeneration);
        }
    }
}
