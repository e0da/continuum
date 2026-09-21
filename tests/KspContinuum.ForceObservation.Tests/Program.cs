using System;
using System.IO;
using System.Collections.Generic;
using KspContinuum;
static class Program
{
    static int checks;
    static ForceObservationReport fixture;
    static void Check(bool condition) { checks++; if (!condition) throw new Exception("Force observation assertion " + checks); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static ForceObservationContext Context(long epoch = 1, long topology = 1, long frame = 1) => new ForceObservationContext(
        "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", "FLIGHT", "frame-1", 7, 2, epoch, topology, frame, 0, 100, 2, .02, new Vec());
    static Timing3 Setup()
    {
        UnityEngine.Object.Objects.Clear();
        var stage=new Timing3(); UnityEngine.Object.Objects.Add(stage); TimingManager.Current=stage;
        var part=new Part { Id=3, flightID=1, rb=new UnityEngine.Rigidbody {Id=4,worldCenterOfMass=new Vector3d(10,20,30)},
            force=new Vector3d(1,2,3), torque=new Vector3d(4,5,6) };
        part.RigidBodyPart=part;
        part.forces.Add(new Part.ForceHolder {force=new Vector3d(7,8,9),pos=new Vector3d(11,22,33)});
        FlightGlobals.ActiveVessel=new Vessel {Id=9}; FlightGlobals.ActiveVessel.parts.Add(part);
        HighLogic.LoadedScene=GameScenes.FLIGHT; FlightGlobals.ready=true; TimingManager.SkipAdd=false;
        Krakensbane.Velocity=new Vector3d();
        return stage;
    }
    static void NativeTests()
    {
        var stage=Setup(); int foreignCalls=0; TimingManager.UpdateAction foreign=()=>foreignCalls++;
        stage.onFixedUpdate=foreign;
        var capture=new PartForceObservation(); capture.Start();
        Check(capture.Report.status=="running" && stage.onFixedUpdate.GetInvocationList().Length==2);
        stage.onFixedUpdate(); capture.Dispose();
        var r=capture.Report;
        Check(r.status=="interrupted" && r.cleanupStatus=="removed-owned-callbacks" && r.completedBatches==1);
        Check(r.batches[0].parts[0].force.X==1 && r.batches[0].parts[0].torque.Z==6);
        Check(r.batches[0].parts[0].forces[0].worldLeverArm.Value.Z==3);
        FlightGlobals.ActiveVessel.parts[0].forces.Clear();
        Check(r.batches[0].parts[0].forces.Count==1 && r.gravity=="unavailable-not-observed");
        Check(stage.onFixedUpdate==foreign); capture.Dispose(); Check(stage.onFixedUpdate==foreign);
        Check(GameEvents.onFloatingOriginShift.Handlers==null);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        stage.onFixedUpdate(); GameEvents.onFloatingOriginShift.Fire(); stage.onFixedUpdate();
        FlightGlobals.ActiveVessel.packed=true; stage.onFixedUpdate();
        FlightGlobals.ActiveVessel.packed=false; stage.onFixedUpdate(); capture.Dispose();
        r=capture.Report;
        Check(r.completedBatches==3 && r.skippedCallbacks==1 && r.physicsEpochs==4);
        Check(r.batches[1].context.floatingOriginEvents==1 && !r.batches[0].context.Matches(r.batches[1].context));
        Check(r.batches[2].context.frameGeneration>r.batches[1].context.frameGeneration);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        for(int i=0;i<16;i++) stage.onFixedUpdate?.Invoke();
        Check(capture.Report.status=="complete" && capture.Report.completedBatches==16 && stage.onFixedUpdate==null);
        fixture=capture.Report;
        capture.Dispose();

        stage=Setup(); TimingManager.SkipAdd=true; capture=new PartForceObservation(); capture.Start();
        Check(capture.Report.status=="unavailable" && stage.onFixedUpdate==null); capture.Dispose();
        stage=Setup(); TimingManager.ThrowAfterAdd=true; capture=new PartForceObservation(); capture.Start();
        Check(capture.Report.status=="unavailable" && stage.onFixedUpdate==null && GameEvents.onFloatingOriginShift.Handlers==null);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        FlightGlobals.ActiveVessel.parts[0].force=new Vector3d(double.NaN,0,0);
        stage.onFixedUpdate(); Check(capture.Report.status=="invalid" && capture.Report.completedBatches==0 && stage.onFixedUpdate==null);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        var replacement=new Timing3(); UnityEngine.Object.Objects.Clear(); UnityEngine.Object.Objects.Add(replacement); TimingManager.Current=replacement;
        replacement.onFixedUpdate=foreign; capture.Tick();
        Check(capture.Report.status=="invalid" && stage.onFixedUpdate==null && replacement.onFixedUpdate==foreign);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        var owned=stage.onFixedUpdate; stage.onFixedUpdate+=owned; stage.onFixedUpdate();
        Check(capture.Report.status=="invalid" && stage.onFixedUpdate==null);

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        stage.onFixedUpdate=null; capture.Dispose();
        Check(capture.Report.status=="invalid");

        stage=Setup(); capture=new PartForceObservation(); capture.Start();
        stage.onFixedUpdate(); HighLogic.LoadedScene=GameScenes.MAINMENU; capture.Tick();
        HighLogic.LoadedScene=GameScenes.FLIGHT; stage.onFixedUpdate(); capture.Dispose();
        Check(capture.Report.batches[1].context.topologyGeneration>capture.Report.batches[0].context.topologyGeneration);

        stage=Setup();
        var vessel=FlightGlobals.ActiveVessel; vessel.parts.Clear();
        for(int i=0;i<5;i++)
        {
            var p=new Part {Id=i+100,flightID=(uint)(i+1),rb=new UnityEngine.Rigidbody {Id=i+200}}; p.RigidBodyPart=p;
            for(int j=0;j<64;j++) p.forces.Add(new Part.ForceHolder());
            vessel.parts.Add(p);
        }
        capture=new PartForceObservation(); capture.Start();
        for(int i=0;i<16;i++) stage.onFixedUpdate?.Invoke();
        Check(capture.Report.status=="bounded" && capture.Report.completedBatches==12 && capture.Report.retainedHolders==3840);
        Check(capture.Report.batches.Length==12 && capture.Report.cleanupStatus=="removed-owned-callbacks");

        stage=Setup();
        var alias=new Part {Id=6,flightID=2,parent=FlightGlobals.ActiveVessel.parts[0],RigidBodyPart=FlightGlobals.ActiveVessel.parts[0]};
        FlightGlobals.ActiveVessel.parts.Add(alias);
        capture=new PartForceObservation(); capture.Start(); stage.onFixedUpdate(); capture.Dispose();
        Check(capture.Report.batches[0].parts.Count==2 && capture.Report.batches[0].parts[1].force.X==0 &&
            capture.Report.batches[0].parts[1].rigidBodyPartFlightId==1 && capture.Report.batches[0].parts[0].force.X==1);

        stage=Setup(); capture=new PartForceObservation(); capture.Start(); stage.Destroyed=true; capture.Dispose();
        Check(capture.Report.cleanupStatus=="owner-destroyed" && GameEvents.onFloatingOriginShift.Handlers==null);
    }
    static void Main(string[] args)
    {
        var holders = new[] { new ForceAtPositionObservation(new Vec(2,3,4), new Vec(5,6,7), new Vec(1,2,3)) };
        var part = new ForcePartObservation(1,0,2,3,new Vec(1,2,3),new Vec(4,5,6),new Vec(4,4,4),holders);
        holders[0] = null;
        Check(part.forces[0] != null);
        var parts = new[] { part }; var batch = new ForceObservationBatch(Context(),parts); parts[0] = null;
        Check(batch.parts[0] == part);
        bool readOnly=false;
        try { ((IList<ForcePartObservation>)batch.parts)[0]=null; } catch(NotSupportedException) {readOnly=true;}
        Check(readOnly);
        Check(batch.context.Matches(Context()));
        Check(!batch.context.Matches(Context(epoch:2)));
        Check(!batch.context.Matches(Context(topology:2)));
        Check(!batch.context.Matches(Context(frame:2)));
        Check(!batch.context.Matches(null));
        Check(Reject(() => new ForceObservationBatch(Context(),new[] {part,part})));
        Check(Reject(() => new ForceAtPositionObservation(new Vec(double.NaN,0,0),new Vec(),null)));
        Check(Reject(() => new ForcePartObservation(1,0,2,null,new Vec(),new Vec(),null,new ForceAtPositionObservation[65])));
        Check(Reject(() => new ForceObservationBatch(Context(),new ForcePartObservation[513])));
        NativeTests();
        if(args.Length!=0)
        {
            if(args.Length!=2 || args[0]!="--export-fixture") throw new ArgumentException("Use --export-fixture NEW_PATH.");
            fixture.detail="Managed API test double; not live KSP evidence.";
            using(var stream=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write))
            using(var writer=new StreamWriter(stream)) writer.Write(ReportJson.Encode(fixture));
        }
        Console.WriteLine("Force observation: " + checks + " assertions passed.");
    }
}
