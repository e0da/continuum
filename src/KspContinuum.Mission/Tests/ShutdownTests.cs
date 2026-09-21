using System;
using KspContinuum;
using KspContinuum.Mission;

static class ShutdownTests
{
    public static void Run(Action<bool> check)
    {
        bool reservedRejected = false;
        try { QualificationShutdown.Requests.Register("qualification-capture", reason => "inactive"); }
        catch (InvalidOperationException) { reservedRejected = true; }
        check(reservedRejected);
        for (int i = 0; i < 31; i++) QualificationShutdown.Requests.Register("optional-" + i, reason => "inactive");
        bool capacityRejected = false;
        try { QualificationShutdown.Requests.Register("overflow", reason => "inactive"); }
        catch (InvalidOperationException) { capacityRejected = true; }
        check(capacityRejected);
        QualificationShutdown.RecordCaptureCleanup(true);
        var fullReceipt = QualificationShutdown.Requests.Request("qualification-complete");
        check(!fullReceipt.HasErrors && fullReceipt.HandlerCount == 32);
        var terminal = new MissionTermination();
        check(terminal.TryBegin("interrupted"));
        terminal.FailFinalization();
        check(terminal.Status == "interrupted");
        check(!terminal.TryBegin("passed") && !terminal.TryBegin("failed"));
        check(terminal.Status == "interrupted");
        var passed = new MissionTermination();
        check(passed.TryBegin("passed"));
        check(!passed.TryBegin("interrupted") && passed.Status == "passed");
        passed.FailFinalization();
        check(passed.Status == "failed");
        var failed = new MissionTermination();
        check(failed.TryBegin("failed"));
        check(!failed.TryBegin("interrupted") && failed.Status == "failed");
        bool flightPresent = true;
        int nativeAccess = 0, fileClosed = 0;
        var late = new MissionCleanup();
        late.TrackFlight("warp", () => flightPresent, () => nativeAccess++);
        late.Track("writer", () => fileClosed++);
        flightPresent = false;
        check(late.ReleaseAll().Count == 0);
        check(nativeAccess == 0 && fileClosed == 1 && late.Skipped.Count == 1 && late.Skipped[0] == "warp");
        flightPresent = true;
        check(late.ReleaseAll().Count == 0 && nativeAccess == 0 && fileClosed == 1);
        late.TrackFlight("new-warp-owner", () => flightPresent, () => nativeAccess++);
        check(late.ReleaseAll().Count == 0 && nativeAccess == 1);
        var invalidNameRegistry = new ShutdownRegistry();
        bool invalidNameRejected = false;
        try { invalidNameRegistry.Register("mission\n", reason => "inactive"); }
        catch (ArgumentException) { invalidNameRejected = true; }
        check(invalidNameRejected);
        var registry = new ShutdownRegistry();
        int released = 0, receiptWritten = 0;
        var owned = new MissionCleanup();
        owned.Track("controller", () => released++);
        owned.Track("recorder", () => released++);
        registry.Register("broken-addon", reason => { throw new InvalidOperationException("broken"); });
        var registration = registry.Register("mission", reason => {
            check(reason == "qualification-complete");
            check(owned.ReleaseAll().Count == 0);
            receiptWritten++; return "interrupted";
        });
        var result = registry.Request("qualification-complete");
        check(result.HasErrors && result.HandlerCount == 2);
        check(released == 2 && receiptWritten == 1);
        check(result.Text("complete").Contains("handler=mission:interrupted"));
        check(result.Text("complete").Contains("status=error"));
        check(ReferenceEquals(result, registry.Request("qualification-complete")));
        check(released == 2 && receiptWritten == 1);
        registration.Dispose(); registration.Dispose();

        registry = new ShutdownRegistry();
        var old = registry.Register("mission", reason => { throw new Exception("old registration invoked"); });
        old.Dispose();
        registry.Register("mission", reason => "already-terminal");
        old.Dispose();
        result = registry.Request("qualification-timeout");
        check(!result.HasErrors && result.HandlerCount == 1);
        check(result.Text("timeout").Contains("handler=mission:already-terminal"));
        result = new ShutdownRegistry().Request("qualification-complete");
        check(!result.HasErrors && result.HandlerCount == 0);
    }
}
