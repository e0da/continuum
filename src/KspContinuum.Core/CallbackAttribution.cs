using System;
using System.Collections.Generic;

namespace KspContinuum
{
    [Serializable] public sealed class CallbackAttributionReport
    {
        public string schema = "ksp-continuum-fixed-callback-attribution/v1";
        public string status = "not-started", cleanupStatus = "not-installed", detail;
        public string measurementScope = "Inclusive elapsed wall time for patched managed MonoBehaviour FixedUpdate methods while Unity's ScriptRunBehaviourFixedUpdate subtree is active. Rows can overlap when callbacks invoke one another and must not be summed blindly.";
        public string coverageScope = "Loaded, non-dynamic assemblies referencing Unity and patchable zero-argument instance void FixedUpdate methods declared by MonoBehaviour types.";
        public long clockFrequency, timerReadFloorTicks, activeWindowTicks;
        public int discoveredMethods, patchedMethods, scanErrors, callbackErrors;
        public CallbackAttributionRow[] callbacks;
    }

    [Serializable] public sealed class CallbackAttributionRow
    {
        public string category, assembly, assemblyVersion, assemblyMvid, declaringType, method;
        public int metadataToken, calls, outermostCalls;
        public long inclusiveTicks, maximumTicks, outermostInclusiveTicks, outermostMaximumTicks;
        public double inclusiveMilliseconds, meanMilliseconds, maximumMilliseconds, fractionOfActiveWindow;
        public double outermostInclusiveMilliseconds, outermostMeanMilliseconds, outermostMaximumMilliseconds,
            outermostFractionOfActiveWindow;
    }

    public sealed class CallbackAttributionAccumulator
    {
        sealed class Slot
        {
            public readonly CallbackAttributionRow Row;
            public Slot(CallbackAttributionRow row) { Row = row; }
        }
        readonly long frequency;
        readonly List<Slot> slots = new List<Slot>();
        bool finished;

        public CallbackAttributionAccumulator(long frequency)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException("frequency");
            this.frequency = frequency;
        }

        public int Register(CallbackAttributionRow row)
        {
            if (finished || row == null || string.IsNullOrEmpty(row.assembly) || string.IsNullOrEmpty(row.declaringType) ||
                string.IsNullOrEmpty(row.method)) throw new ArgumentException("Invalid callback attribution row.");
            slots.Add(new Slot(row)); return slots.Count - 1;
        }

        public void Record(int slot, long elapsedTicks, bool outermost)
        {
            if (finished || slot < 0 || slot >= slots.Count || elapsedTicks < 0) throw new ArgumentException("Invalid callback timing sample.");
            CallbackAttributionRow row = slots[slot].Row;
            row.calls++;
            checked { row.inclusiveTicks += elapsedTicks; }
            if (elapsedTicks > row.maximumTicks) row.maximumTicks = elapsedTicks;
            if (outermost)
            {
                row.outermostCalls++;
                checked { row.outermostInclusiveTicks += elapsedTicks; }
                if (elapsedTicks > row.outermostMaximumTicks) row.outermostMaximumTicks = elapsedTicks;
            }
        }

        public CallbackAttributionRow[] Finish(long activeWindowTicks)
        {
            if (finished || activeWindowTicks < 0) throw new ArgumentException("Invalid callback attribution completion.");
            finished = true;
            var rows = new List<CallbackAttributionRow>();
            foreach (Slot slot in slots)
            {
                CallbackAttributionRow row = slot.Row;
                if (row.calls == 0) continue;
                row.inclusiveMilliseconds = row.inclusiveTicks * (1000.0 / frequency);
                row.meanMilliseconds = row.inclusiveMilliseconds / row.calls;
                row.maximumMilliseconds = row.maximumTicks * (1000.0 / frequency);
                row.fractionOfActiveWindow = activeWindowTicks == 0 ? 0 : (double)row.inclusiveTicks / activeWindowTicks;
                row.outermostInclusiveMilliseconds = row.outermostInclusiveTicks * (1000.0 / frequency);
                row.outermostMeanMilliseconds = row.outermostCalls == 0 ? 0 : row.outermostInclusiveMilliseconds / row.outermostCalls;
                row.outermostMaximumMilliseconds = row.outermostMaximumTicks * (1000.0 / frequency);
                row.outermostFractionOfActiveWindow = activeWindowTicks == 0 ? 0 : (double)row.outermostInclusiveTicks / activeWindowTicks;
                rows.Add(row);
            }
            rows.Sort((left, right) => right.outermostInclusiveTicks.CompareTo(left.outermostInclusiveTicks));
            return rows.ToArray();
        }
    }
}
