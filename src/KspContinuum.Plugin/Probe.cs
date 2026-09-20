using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Profiling;

namespace KspContinuum
{
    public sealed class Probe : IDisposable
    {
        readonly List<Recorder> recorders = new List<Recorder>();
        readonly List<bool> enabledBefore = new List<bool>();
        public void Dispose()
        {
            for (int i = 0; i < recorders.Count; i++) recorders[i].enabled = enabledBefore[i];
            recorders.Clear(); enabledBefore.Clear();
        }
        public IEnumerator Run(Action<ProbeReport> complete)
        {
            var reports = new List<MarkerReport>();
            try
            {
                foreach (string name in new[] { "Physics.Simulate", "Physics.Processing", "BehaviourFixedUpdate", "GC.Collect" })
                {
                    var recorder = Recorder.Get(name);
                    var row = new MarkerReport { name = name, status = "unavailable", nanoseconds = new long[0], blocks = new int[0] };
                    if (recorder != null && recorder.isValid)
                    {
                        enabledBefore.Add(recorder.enabled); recorder.enabled = true; recorders.Add(recorder);
                        row.status = "available-no-samples"; row.nanoseconds = new long[300]; row.blocks = new int[300];
                    }
                    reports.Add(row);
                }
                for (int frame = 0; frame < 300; frame++)
                {
                    yield return null;
                    int index = 0;
                    foreach (var row in reports)
                    {
                        if (row.status == "unavailable") continue;
                        var recorder = recorders[index++];
                        row.nanoseconds[frame] = recorder.elapsedNanoseconds;
                        row.blocks[frame] = recorder.sampleBlockCount;
                        if (row.blocks[frame] > 0) row.status = "observed";
                    }
                }
                complete(new ProbeReport { markers = reports.ToArray() });
            }
            finally { Dispose(); }
        }
    }
}
