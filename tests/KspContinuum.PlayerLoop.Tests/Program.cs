using System;
using System.Text.Json;
using KspContinuum;
using UnityEngine.LowLevel;
using Fixed = UnityEngine.PlayerLoop.FixedUpdate;
using Update = UnityEngine.PlayerLoop.Update;
using Late = UnityEngine.PlayerLoop.PreLateUpdate;

static class Program
{
    static int checks;
    static void Check(bool ok) { checks++; if (!ok) throw new Exception("PlayerLoop assertion " + checks); }
    static PlayerLoopSystem Native(Type type) { return new PlayerLoopSystem { type = type, updateFunction = (IntPtr)37, loopConditionFunction = (IntPtr)42 }; }
    static PlayerLoopSystem Tree() { return new PlayerLoopSystem { subSystemList = new[] {
        new PlayerLoopSystem { type = typeof(Fixed), subSystemList = new[] { Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)) } },
        new PlayerLoopSystem { type = typeof(Update), subSystemList = new[] { Native(typeof(Update.ScriptRunBehaviourUpdate)) } },
        new PlayerLoopSystem { type = typeof(Late), subSystemList = new[] { Native(typeof(Late.ScriptRunBehaviourLateUpdate)) } }
    } }; }
    static void Dispatch(PlayerLoopSystem node) { if (node.updateDelegate != null) node.updateDelegate(); if (node.subSystemList != null) foreach (var child in node.subSystemList) Dispatch(child); }
    static PlayerLoopSystem Find(PlayerLoopSystem node, Type type) { if (node.type == type) return node; if (node.subSystemList != null) foreach (var child in node.subSystemList) { try { return Find(child, type); } catch (InvalidOperationException) { } } throw new InvalidOperationException(); }

    static void Main()
    {
        var before = Tree(); PlayerLoop.Current = before; var capture = new PlayerLoopTiming(); capture.Start();
        Check(before.subSystemList.Length == 3 && before.subSystemList[0].subSystemList.Length == 2);
        Check(PlayerLoop.Current.subSystemList.Length == 5); Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 6);
        Check(Find(PlayerLoop.Current, typeof(Update)).subSystemList.Length == 3); Check(Find(PlayerLoop.Current, typeof(Late)).subSystemList.Length == 3);
        Dispatch(PlayerLoop.Current); capture.Audit(); capture.Dispose(); var report = capture.Report;
        Check(report.schema == "ksp-continuum-playerloop/v2"); Check(report.integrityStatus == "verified-at-boundaries" && report.cleanupStatus == "removed-owned-hooks"); Check(report.scopes.Length == 5);
        string[] names = { typeof(Fixed).FullName, typeof(Fixed.PhysicsFixedUpdate).FullName, typeof(Fixed.ScriptRunBehaviourFixedUpdate).FullName, typeof(Update.ScriptRunBehaviourUpdate).FullName, typeof(Late.ScriptRunBehaviourLateUpdate).FullName };
        for (int i = 0; i < names.Length; i++) { Check(report.scopes[i].name == names[i]); Check(report.scopes[i].samples.Length == 1); Check(report.scopes[i].milliseconds.count == 1); Check(report.scopes[i].timeDomain == (i < 3 ? "fixed" : "frame")); }
        Check(report.scopes[0].overlap == "contains-fixed-children" && report.scopes[1].overlap == "contained-by-fixed" && report.scopes[4].overlap == "separate-frame-phase");
        Check(PlayerLoop.Current.subSystemList.Length == 3 && Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 2);
        int writes = PlayerLoop.Writes; capture.Dispose(); Check(writes == PlayerLoop.Writes);
        using (var json = JsonDocument.Parse(ReportJson.Encode(report))) Check(json.RootElement.GetProperty("scopes").GetArrayLength() == 5);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); var current = PlayerLoop.Current;
        var expanded = new PlayerLoopSystem[current.subSystemList.Length + 1]; Array.Copy(current.subSystemList, expanded, current.subSystemList.Length); expanded[expanded.Length - 1] = Native(typeof(Program)); current.subSystemList = expanded; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "verified-at-boundaries"); Check(PlayerLoop.Current.subSystemList.Length == 4 && PlayerLoop.Current.subSystemList[3].type == typeof(Program));

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current; var fixedNode = Find(current, typeof(Fixed));
        expanded = new PlayerLoopSystem[fixedNode.subSystemList.Length + 1]; Array.Copy(fixedNode.subSystemList, expanded, fixedNode.subSystemList.Length); expanded[expanded.Length - 1] = Native(typeof(decimal)); fixedNode.subSystemList = expanded; current.subSystemList[1] = fixedNode; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "verified-at-boundaries"); Check(Find(PlayerLoop.Current, typeof(Fixed)).subSystemList.Length == 3);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current;
        fixedNode = Find(current, typeof(Fixed)); var swap = fixedNode.subSystemList[1]; fixedNode.subSystemList[1] = fixedNode.subSystemList[4]; fixedNode.subSystemList[4] = swap; current.subSystemList[1] = fixedNode; PlayerLoop.Current = current;
        capture.Audit(); capture.Dispose(); Check(capture.Report.integrityStatus == "invalidated"); foreach (var scope in capture.Report.scopes) Check(scope.status == "invalid" && scope.milliseconds == null);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); current = PlayerLoop.Current; int calls = 0; PlayerLoopSystem.UpdateFunction extra = () => calls++;
        current.subSystemList[0].updateDelegate += extra; current.subSystemList[0].subSystemList = new[] { Native(typeof(string)) }; PlayerLoop.Current = current; capture.Audit(); capture.Dispose();
        Check(capture.Report.integrityStatus == "invalidated"); Check(PlayerLoop.Current.subSystemList[0].subSystemList[0].type == typeof(string)); PlayerLoop.Current.subSystemList[0].updateDelegate(); Check(calls == 1);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); capture.Start(); PlayerLoop.Current.subSystemList[0].updateDelegate(); capture.Dispose(); Check(capture.Report.status == "invalid"); foreach (var scope in capture.Report.scopes) Check(scope.status == "invalid" && scope.milliseconds == null);

        PlayerLoop.Current = Tree(); capture = new PlayerLoopTiming(); PlayerLoop.FailAfterWrite = true; capture.Start(); capture.Dispose(); Check(capture.Report.status == "unavailable" && PlayerLoop.Current.subSystemList.Length == 3);
        PlayerLoop.Current = Tree(); current = PlayerLoop.Current; current.subSystemList[1].subSystemList = Array.Empty<PlayerLoopSystem>(); PlayerLoop.Current = current; writes = PlayerLoop.Writes; capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose(); Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);
        PlayerLoop.Current = Tree(); current = PlayerLoop.Current; current.subSystemList[0].subSystemList = new[] { Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.PhysicsFixedUpdate)), Native(typeof(Fixed.ScriptRunBehaviourFixedUpdate)) }; PlayerLoop.Current = current; writes = PlayerLoop.Writes; capture = new PlayerLoopTiming(); capture.Start(); capture.Dispose(); Check(capture.Report.status == "unavailable" && PlayerLoop.Writes == writes);

        var buffer = new LoopTimingBuffer("test", 2, 1000); buffer.Begin(100, 7, 1, .02); buffer.End(125, 7); buffer.Begin(200, 7, 1.02, .02); buffer.End(250, 7); buffer.Begin(300, 8, 1.04, .02); buffer.End(350, 8);
        var result = buffer.Finish(true); Check(result.samples.Length == 2 && result.droppedSamples == 1); Check(result.milliseconds.mean == 37.5 && result.samples[0].elapsedTicks == 25);
        buffer = new LoopTimingBuffer("bad", 2, 1000); buffer.Begin(100, 1, 0, .02); buffer.End(110, 2); result = buffer.Finish(true); Check(result.status == "invalid" && result.milliseconds == null);
        buffer = new LoopTimingBuffer("missing", 2, 1000); buffer.Begin(100, 1, 0, .02); Check(buffer.Finish(true).status == "invalid");
        Console.WriteLine("PlayerLoop: " + checks + " assertions passed.");
    }
}
