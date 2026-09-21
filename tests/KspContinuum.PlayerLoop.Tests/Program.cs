using System;
using System.Text.Json;
using KspContinuum;
using UnityEngine.LowLevel;
using Fixed = UnityEngine.PlayerLoop.FixedUpdate;
static class Program
{
    static int checks;
    static void Check(bool ok) { checks++; if (!ok) throw new Exception("PlayerLoop assertion " + checks); }
    static PlayerLoopSystem Native(Type type) { return new PlayerLoopSystem { type = type, updateFunction = (IntPtr)37, loopConditionFunction = (IntPtr)42 }; }
    static PlayerLoopSystem Tree() { return new PlayerLoopSystem { subSystemList = new[] { new PlayerLoopSystem { type = typeof(Fixed), subSystemList = new[] { Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)) } } } }; }
    static void Main()
    {
        var before = Tree(); PlayerLoop.Current = before;
        var capture = new PlayerLoopTiming(); capture.Start();
        var children = PlayerLoop.Current.subSystemList[0].subSystemList;
        Check(children.Length == 6);
        Check(before.subSystemList[0].subSystemList.Length == 2);
        Check(children[1].updateFunction == (IntPtr)37 && children[1].loopConditionFunction == (IntPtr)42);
        Check(children[4].type == typeof(Fixed.PhysicsFixedUpdate));
        foreach (var node in children) if (node.updateDelegate != null) node.updateDelegate();
        capture.Audit(); capture.Dispose();
        var report = capture.Report;
        Check(report.integrityStatus == "verified-at-boundaries");
        Check(report.scopes[0].samples.Length == 1 && report.scopes[1].samples.Length == 1);
        Check(report.scopes[0].milliseconds.count == 1);
        Check(PlayerLoop.Current.subSystemList[0].subSystemList.Length == 2);
        int writes = PlayerLoop.Writes; capture.Dispose(); Check(writes == PlayerLoop.Writes);
        using (var json = JsonDocument.Parse(ReportJson.Encode(report))) Check(json.RootElement.GetProperty("scopes").GetArrayLength() == 2);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        var foreign = Native(typeof(Program));
        var modified = new PlayerLoopSystem[7]; Array.Copy(children, modified, 6); modified[6] = foreign;
        var current = PlayerLoop.Current; current.subSystemList[0].subSystemList = modified; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "verified-at-boundaries");
        Check(PlayerLoop.Current.subSystemList[0].subSystemList.Length == 3);
        Check(PlayerLoop.Current.subSystemList[0].subSystemList[2].type == typeof(Program));

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        var swap = children[1]; children[1] = children[4]; children[4] = swap;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "invalidated");
        Check(capture.Report.scopes[0].milliseconds == null);
        Check(PlayerLoop.Current.subSystemList[0].subSystemList.Length == 2);

        PlayerLoop.Current = Tree(); current = PlayerLoop.Current;
        current.subSystemList = new[] { current.subSystemList[0], current.subSystemList[0] }; PlayerLoop.Current = current;
        writes = PlayerLoop.Writes; capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose();
        Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        for (int i = 0; i < 3; i++) { swap = children[i]; children[i] = children[i + 3]; children[i + 3] = swap; }
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "invalidated");

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        // A foreign owner appends work to our node; removal must preserve its callback and child.
        int calls = 0; PlayerLoopSystem.UpdateFunction extra = () => calls++;
        children[0].updateDelegate += extra;
        children[0].subSystemList = new[] { Native(typeof(string)) };
        capture.Audit(); capture.Dispose();
        Check(capture.Report.integrityStatus == "invalidated");
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        Check(children.Length == 3 && children[0].subSystemList[0].type == typeof(string));
        children[0].updateDelegate(); Check(calls == 1);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        // Before without after simulates a native exception interrupting dispatch.
        children[0].updateDelegate(); capture.Dispose();
        Check(capture.Report.status == "invalid" && capture.Report.scopes[1].sequenceErrors == 1);
        Check(capture.Report.integrityStatus == "invalidated" && capture.Report.scopes[0].status == "invalid" && capture.Report.scopes[0].milliseconds == null);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        children = PlayerLoop.Current.subSystemList[0].subSystemList;
        current = PlayerLoop.Current;
        current.updateDelegate = children[0].updateDelegate; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose();
        Check(capture.Report.integrityStatus == "invalidated" && PlayerLoop.Current.updateDelegate == null);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming();
        PlayerLoop.FailAfterWrite = true; capture.Start(); capture.Dispose();
        Check(capture.Report.status == "unavailable" && PlayerLoop.Current.subSystemList[0].subSystemList.Length == 2);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start();
        current = PlayerLoop.Current;
        current.subSystemList = new[] { new PlayerLoopSystem { type = typeof(string), subSystemList = current.subSystemList } };
        PlayerLoop.Current = current; capture.Audit(); capture.Dispose();
        Check(capture.Report.integrityStatus == "invalidated");

        PlayerLoop.Current = Tree(); current = PlayerLoop.Current;
        current.subSystemList[0].subSystemList = new[] { Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)) };
        PlayerLoop.Current = current; writes = PlayerLoop.Writes;
        capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose();
        Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);

        var buffer = new LoopTimingBuffer("test", 2, 1000);
        buffer.Begin(100, 7, 1, .02); buffer.End(125, 7);
        buffer.Begin(200, 7, 1.02, .02); buffer.End(250, 7);
        buffer.Begin(300, 8, 1.04, .02); buffer.End(350, 8);
        var scope = buffer.Finish(true);
        Check(scope.samples.Length == 2 && scope.droppedSamples == 1);
        Check(scope.milliseconds.mean == 37.5 && scope.samples[0].elapsedTicks == 25);
        buffer = new LoopTimingBuffer("bad", 2, 1000); buffer.Begin(100, 1, 0, .02); buffer.End(110, 2);
        scope = buffer.Finish(true); Check(scope.status == "invalid" && scope.milliseconds == null);
        buffer = new LoopTimingBuffer("missing", 2, 1000); buffer.Begin(100, 1, 0, .02);
        Check(buffer.Finish(true).status == "invalid");
        Console.WriteLine("PlayerLoop: " + checks + " assertions passed.");
    }
}
