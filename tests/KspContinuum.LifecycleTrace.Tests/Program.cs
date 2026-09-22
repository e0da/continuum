using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KspContinuum;

class Program
{
    static int checks;

    static void Check(bool value, string message)
    {
        checks++;
        if (!value)
            throw new Exception(message);
    }

    static void Setup()
    {
        UnityEngine.Object.Objects.Clear();
        TimingManager.Current = new Timing3();
        TimingManager.FI = new TimingFI();
        TimingManager.Late = new Timing5();
        UnityEngine.Object.Objects.Add(TimingManager.Current);
        UnityEngine.Object.Objects.Add(TimingManager.FI);
        UnityEngine.Object.Objects.Add(TimingManager.Late);
        var p = new Part
        {
            Id = 3,
            flightID = 1,
            rb = new UnityEngine.Rigidbody { Id = 4 },
        };
        p.RigidBodyPart = p;
        FlightGlobals.ActiveVessel = new Vessel { Id = 9 };
        FlightGlobals.ActiveVessel.parts.Add(p);
        HighLogic.LoadedScene = GameScenes.FLIGHT;
        FlightGlobals.ready = true;
        FlightDriver.Pause = false;
        TimeWarp.CurrentRate = 1;
        TimingManager.SkipAdd = false;
        TimingManager.ThrowAfterAdd = false;
        Krakensbane.Velocity = new Vector3d();
    }

    static void Stabilize(LifecycleTraceCapture trace)
    {
        for (int i = 0; i < LifecycleTraceReport.MinimumStableArmingHostFixedObservations; i++)
            trace.ObserveFixedUpdate();
        Check(
            trace.Report.status == "running"
                && trace.Report.completedEvents == 0
                && trace.Report.stableArmingHostFixedObservations
                    == LifecycleTraceReport.MinimumStableArmingHostFixedObservations,
            "orbital context stabilizes before capture"
        );
    }

    static void Adversaries()
    {
        Setup();
        var trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        TimingManager.Current.onFixedUpdate = null;
        trace.Dispose();
        Check(trace.Report.status == "invalid", "lost callback detected on dispose");
        Setup();
        for (int i = 0; i < 512; i++)
            FlightGlobals.ActiveVessel.parts.Add(
                new Part { Id = 100 + i, flightID = (uint)(2 + i) }
            );
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        trace.ObserveFixedUpdate();
        Check(trace.Report.status == "bounded", "oversized initial inventory bounded");
        trace.Dispose();
    }

    static void MoreTests()
    {
        Setup();
        int foreignCalls = 0;
        TimingManager.UpdateAction foreign = () => foreignCalls++;
        TimingManager.Current.onFixedUpdate = foreign;
        TimingManager.FI.onFixedUpdate = foreign;
        TimingManager.Late.onFixedUpdate = foreign;
        var trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        trace.ObserveUpdate();
        trace.Dispose();
        trace.Dispose();
        Check(
            TimingManager.Current.onFixedUpdate == foreign
                && TimingManager.FI.onFixedUpdate == foreign
                && TimingManager.Late.onFixedUpdate == foreign,
            "foreign callbacks retained"
        );
        Check(GameEvents.onFloatingOriginShift.Handlers == null, "origin callback removed");
        trace.ObserveAfterFixedUpdate();
        Check(trace.Report.completedEvents == 1, "late coroutine ignored after stop");
        Setup();
        TimingManager.SkipAdd = true;
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Check(
            trace.Report.status == "unavailable" && TimingManager.FI.onFixedUpdate == null,
            "silent registration failure"
        );
        Setup();
        TimingManager.ThrowAfterAdd = true;
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Check(
            trace.Report.status == "unavailable"
                && TimingManager.Current.onFixedUpdate == null
                && GameEvents.onFloatingOriginShift.Handlers == null,
            "partial registration cleaned"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        var duplicate = TimingManager.FI.onFixedUpdate;
        TimingManager.FI.onFixedUpdate += duplicate;
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "invalid" && TimingManager.FI.onFixedUpdate == null,
            "duplicate owned delegates all removed"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        var old = TimingManager.Current;
        UnityEngine.Object.Objects.Remove(old);
        TimingManager.Current = new Timing3 { onFixedUpdate = foreign };
        UnityEngine.Object.Objects.Add(TimingManager.Current);
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "invalid"
                && old.onFixedUpdate == null
                && TimingManager.Current.onFixedUpdate == foreign,
            "remove retained owner only"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        TimingManager.FI.Destroyed = true;
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "invalid" && trace.Report.cleanupStatus == "owner-destroyed",
            "destroyed owner reported"
        );
        for (int mode = 0; mode < 6; mode++)
        {
            Setup();
            trace = new LifecycleTraceCapture(() => 0);
            trace.Start();
            Stabilize(trace);
            trace.ObserveUpdate();
            if (mode == 0)
                GameEvents.onFloatingOriginShift.Fire();
            if (mode == 1)
                FlightGlobals.ActiveVessel.packed = true;
            if (mode == 2)
                FlightDriver.Pause = true;
            if (mode == 3)
                FlightGlobals.ActiveVessel.parts[0].rb = new UnityEngine.Rigidbody { Id = 800 };
            if (mode == 4)
                HighLogic.LoadedScene = GameScenes.MAINMENU;
            if (mode == 5)
                Krakensbane.Velocity = new Vector3d(1, 0, 0);
            trace.ObserveUpdate();
            Check(
                trace.Report.status == "invalidated" && trace.Report.completedEvents == 2,
                "lifecycle invalidation " + mode
            );
            Check(
                trace.Report.events[1].sampleStatus == "invalidated-no-census"
                    && trace.Report.events[1].parts.Count == 0,
                "no crossgeneration census " + mode
            );
        }
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        trace.ObserveUpdate();
        FlightGlobals.ActiveVessel.parts[0].rb.isKinematic = true;
        trace.ObserveUpdate();
        Check(trace.Report.status == "invalidated", "kinematic ownership change invalidates");
        Setup();
        FlightGlobals.ready = false;
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        trace.ObserveFixedUpdate();
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "arming"
                && trace.Report.completedEvents == 0
                && trace.Report.skippedArmingCallbacks == 2,
            "bounded arming skips ineligible samples"
        );
        FlightGlobals.ready = true;
        trace.ObserveFixedUpdate();
        GameEvents.onFloatingOriginShift.Fire();
        trace.ObserveFixedUpdate();
        trace.ObserveFixedUpdate();
        Check(
            trace.Report.status == "arming"
                && trace.Report.completedEvents == 0
                && trace.Report.stableArmingHostFixedObservations == 2,
            "initial origin shift restarts arming window"
        );
        trace.ObserveFixedUpdate();
        Check(trace.Report.status == "running" && trace.Report.completedEvents == 0,
            "capture starts after stable post-shift orbital context");
        GameEvents.onFloatingOriginShift.Fire();
        trace.ObserveUpdate();
        Check(trace.Report.status == "invalidated" && trace.Report.completedEvents == 1,
            "origin shift after arming remains visible and invalidates");
        trace.Dispose();
        for (int mode = 0; mode < 2; mode++)
        {
            Setup();
            trace = new LifecycleTraceCapture(() => 0);
            trace.Start();
            trace.ObserveFixedUpdate();
            trace.ObserveFixedUpdate();
            if (mode == 0)
                FlightGlobals.ActiveVessel.parts[0].rb = new UnityEngine.Rigidbody { Id = 901 };
            else
                Krakensbane.Velocity = new Vector3d(0, 2, 0);
            trace.ObserveFixedUpdate();
            trace.ObserveFixedUpdate();
            Check(
                trace.Report.status == "arming"
                    && trace.Report.stableArmingHostFixedObservations == 2
                    && trace.Report.completedEvents == 0,
                "arming topology/frame change restarts window " + mode
            );
            trace.ObserveFixedUpdate();
            Check(trace.Report.status == "running", "restarted arming window completes " + mode);
            trace.Dispose();
        }
        Setup();
        FlightGlobals.ActiveVessel.situation = Vessel.Situations.LANDED;
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        for (int i = 0; i < 4; i++) trace.ObserveFixedUpdate();
        Check(trace.Report.status == "arming" && trace.Report.completedEvents == 0,
            "non-orbital context cannot arm writer census");
        trace.Dispose();
        Setup();
        double wall = 0;
        trace = new LifecycleTraceCapture(() => wall);
        trace.Start();
        wall = 30;
        FlightDriver.Pause = true;
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "timeout" && TimingManager.Current.onFixedUpdate == null,
            "wall timeout despite pause"
        );
        Setup();
        wall = 0;
        trace = new LifecycleTraceCapture(() => wall);
        trace.Start();
        wall = 1;
        trace.ObserveUpdate();
        wall = .5;
        trace.ObserveUpdate();
        Check(trace.Report.status == "invalid", "clock regression rejected");
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        FlightGlobals.ActiveVessel.parts[0].rb.velocity = new Vector3d(double.NaN, 0, 0);
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "invalid" && trace.Report.completedEvents == 0,
            "nonfinite refuses whole event"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        for (int i = 0; i < 130; i++)
            trace.ObserveFixedUpdate();
        Check(
            trace.Report.status == "bounded"
                && trace.Report.hostFixedObservations == 120
                && trace.Report.completedEvents == 120,
            "hostfixed hard cap"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        for (int i = 0; i < 8200 && trace.IsRunning; i++)
            trace.ObserveUpdate();
        Check(
            trace.Report.status == "bounded"
                && trace.Report.encodedEventBytes <= LifecycleTraceReport.MaximumEncodedEventBytes,
            "serialized whole-event cap"
        );
        Check(
            Encoding.UTF8.GetByteCount(ReportJson.Encode(trace.Report)) < 4 * 1024 * 1024,
            "bounded report export fits"
        );
        Setup();
        var part = FlightGlobals.ActiveVessel.parts[0];
        for (int i = 0; i < 64; i++)
            part.forces.Add(new Part.ForceHolder());
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        for (int i = 0; i < 300 && trace.IsRunning; i++)
            trace.ObserveUpdate();
        Check(
            trace.Report.status == "bounded"
                && trace.Report.retainedHolders <= LifecycleTraceReport.MaximumRetainedHolders,
            "holder bound"
        );
        Setup();
        part = FlightGlobals.ActiveVessel.parts[0];
        for (int i = 0; i < 65; i++)
            part.forces.Add(new Part.ForceHolder());
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "bounded" && trace.Report.completedEvents == 0,
            "perpart holder refusal"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        bool wrongThread = Task.Run(() =>
            {
                try
                {
                    trace.ObserveUpdate();
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            })
            .GetAwaiter()
            .GetResult();
        Check(wrongThread && trace.Report.completedEvents == 0, "wrong thread refused");
        trace.Dispose();
        Setup();
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        var second = new LifecycleTraceCapture(() => 0);
        bool rejected = false;
        try
        {
            second.Start();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected && trace.IsRunning, "single owner");
        second.Dispose();
        trace.Dispose();
    }

    static void SnapshotTests()
    {
        Setup();
        var part = FlightGlobals.ActiveVessel.parts[0];
        part.rb.position = new Vector3d(3, 4, 5);
        part.rb.velocity = new Vector3d(6, 7, 8);
        part.force = new Vector3d(9, 10, 11);
        part.forces.Add(
            new Part.ForceHolder { force = new Vector3d(12, 13, 14), pos = new Vector3d(1, 2, 3) }
        );
        var trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        trace.ObserveUpdate();
        trace.Dispose();
        part.rb.position = new Vector3d();
        part.forces.Clear();
        part.force = new Vector3d();
        var sample = trace.Report.events[0].parts[0];
        Check(
            sample.position.Value.X == 3 && sample.velocity.Value.Z == 8,
            "pose copied into owned data"
        );
        Check(
            sample.census.force.Z == 11 && sample.census.forces[0].force.X == 12,
            "force census copied without aggregation"
        );
        bool rejected = false;
        try
        {
            ((System.Collections.Generic.IList<LifecyclePartSample>)trace.Report.events[0].parts)[
                0
            ] = null;
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Check(rejected, "event sample collection immutable");
        Setup();
        FlightGlobals.ActiveVessel.parts[0].rb = null;
        trace = new LifecycleTraceCapture(() => 0);
        trace.Start();
        Stabilize(trace);
        trace.ObserveUpdate();
        trace.Dispose();
        Check(
            !trace.Report.events[0].parts[0].position.HasValue
                && !trace.Report.events[0].parts[0].census.nativeRigidbodyInstanceId.HasValue,
            "absent body remains unavailable not zero"
        );
        Setup();
        trace = new LifecycleTraceCapture(() => double.NaN);
        trace.Start();
        Check(
            trace.Report.status == "unavailable" && TimingManager.Current.onFixedUpdate == null,
            "nonfinite start clock rejected"
        );
        Setup();
        double clock = 0;
        trace = new LifecycleTraceCapture(() => clock);
        trace.Start();
        clock = -1;
        trace.ObserveUpdate();
        Check(trace.Report.status == "invalid", "negative elapsed time rejected");
        Setup();
        clock = 0;
        FlightGlobals.ready = false;
        trace = new LifecycleTraceCapture(() => clock);
        trace.Start();
        clock = 30;
        trace.ObserveUpdate();
        Check(
            trace.Report.status == "timeout" && trace.Report.completedEvents == 0,
            "arming timeout without eligible vessel"
        );
    }

    static LifecycleTraceReport fixture;

    static void Main(string[] args)
    {
        Setup();
        using (var trace = new LifecycleTraceCapture())
        {
            trace.Start();
            Check(trace.IsRunning, "capture must register and run");
            Stabilize(trace);
            TimingManager.Current.onFixedUpdate();
            trace.ObserveFixedUpdate();
            TimingManager.FI.onFixedUpdate();
            TimingManager.Late.onFixedUpdate();
            trace.ObserveAfterFixedUpdate();
            trace.ObserveUpdate();
            trace.Dispose();
            fixture = trace.Report;
            Check(trace.Report.events.Length == 6, "one stream has all six seams");
            for (int i = 0; i < 6; i++)
                Check(trace.Report.events[i].context.sequence == i + 1, "monotonic sequence");
        }
        Adversaries();
        MoreTests();
        SnapshotTests();
        if (args.Length > 0)
        {
            if (args.Length != 2 || args[0] != "--export-fixture")
                throw new ArgumentException("Use --export-fixture NEW_PATH");
            fixture.evidence = "managed-api-test-double";
            using (var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = Encoding.UTF8.GetBytes(ReportJson.Encode(fixture));
                file.Write(bytes, 0, bytes.Length);
            }
        }
        Console.WriteLine("Lifecycle assertions: " + checks);
    }
}
