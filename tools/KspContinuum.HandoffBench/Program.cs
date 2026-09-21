using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using KspContinuum;

static class Program
{
    static readonly JsonSerializerOptions Json=new JsonSerializerOptions {WriteIndented=true};
    static double[] V(Vec v)=>new[]{v.X,v.Y,v.Z};
    static object State(HandoffSnapshot s)=>new {s.Frame,s.BaseEpoch,offset=s.Offset,bodies=s.Bodies.Select(b=>new {b.Id,b.Generation,b.Mass,b.Radius,position=V(b.Position),velocity=V(b.Velocity)}).ToArray()};
    static string Hash(HandoffSnapshot s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(State(s))))).ToLowerInvariant();
    static HandoffSnapshot Initial(Vec origin,Vec boost,double epoch=0)=>new HandoffSnapshot("fixture-inertial",epoch,0,new[]{
        new HandoffBody(1,0,1,1,origin+new Vec(-10,0,0),boost+new Vec(1,0,0)),
        new HandoffBody(2,0,3,1,origin+new Vec(10,0,0),boost+new Vec(-1,0,0)),
        new HandoffBody(3,0,1,1,origin+new Vec(1000,1000,0),boost)});
    sealed class Adapter
    {
        readonly Vec origin,boost;
        internal SphereContactResult Result;
        internal int SolverBodies;
        internal Adapter(Vec origin,Vec boost){this.origin=origin;this.boost=boost;}
        internal HandoffSnapshot Solve(HandoffInput input,CancellationToken token)
        {
            SolverBodies=input.Snapshot.Bodies.Count;
            if(SolverBodies!=2)throw new InvalidOperationException("The fixture adapter requires exactly two spheres.");
            var a=input.Snapshot.Bodies[0];var b=input.Snapshot.Bodies[1];
            Vec movingOrigin=origin+boost*input.Snapshot.Offset;
            SphereContactBody Local(HandoffBody body)=>new SphereContactBody(body.Id,body.Position+movingOrigin*-1,body.Velocity+boost*-1,body.Radius,body.Mass);
            Result=SphereContactSolver.Solve(Local(a),Local(b),input.TargetOffset-input.Snapshot.Offset,token);
            if(Result.Status==SphereContactStatus.Cancelled)throw new OperationCanceledException(token);
            if(Result.Status!=SphereContactStatus.Complete)throw new InvalidOperationException("Contact solver declined: "+Result.Status);
            HandoffBody Publish(HandoffBody body,SphereContactBody result)=>new HandoffBody(body.Id,body.Generation,body.Mass,body.Radius,
                body.Position+body.Velocity*Result.ElapsedSeconds,result.Velocity+boost);
            return new HandoffSnapshot(input.Snapshot.Frame,input.Snapshot.BaseEpoch,input.Snapshot.Offset+Result.ElapsedSeconds,
                new[]{Publish(a,Result.First),Publish(b,Result.Second)});
        }
    }
    static object Run(string name,Vec origin,Vec boost,double epoch,SortedDictionary<string,bool> checks)
    {
        var initial=Initial(origin,boost,epoch);var world=new HandoffWorld(initial);var plan=world.Plan(20,new EncounterBudget());
        var adapter=new Adapter(origin,boost);
        var status=world.Execute(plan,1,20,adapter.Solve,CancellationToken.None);var after=world.Capture();
        double[][] positions=after.Bodies.Select(b=>V(b.Position+(origin+boost*after.Offset)*-1)).ToArray();
        double[][] velocities=after.Bodies.Select(b=>V(b.Velocity+boost*-1)).ToArray();
        checks[name+"ContactBarrier"]=status==HandoffStatus.Committed && adapter.Result.ContactSeconds==9 && after.Offset==9;
        checks[name+"FrameInvariant"]=positions[0].SequenceEqual(new double[]{-1,0,0}) && positions[1].SequenceEqual(new double[]{1,0,0}) &&
            velocities[0].SequenceEqual(new double[]{-2,0,0}) && velocities[1].SequenceEqual(new double[]{0,0,0});
        checks[name+"QuietExcludedFromSolver"]=adapter.SolverBodies==2 && positions[2].SequenceEqual(new double[]{1000,1000,0});
        double momentum=velocities[0][0]+3*velocities[1][0],energy=.5*velocities[0][0]*velocities[0][0]+1.5*velocities[1][0]*velocities[1][0];
        checks[name+"Conservation"]=momentum == -2 && energy == 2;
        long committedRevision=world.Revision;string firstHash=Hash(after);
        var resumePlan=world.Plan(2,new EncounterBudget());var resumeAdapter=new Adapter(origin,boost);
        var resumeStatus=world.Execute(resumePlan,1,11,resumeAdapter.Solve,CancellationToken.None);
        var resumed=world.Capture();
        var resumedPositions=resumed.Bodies.Select(b=>V(b.Position+(origin+boost*resumed.Offset)*-1)).ToArray();
        checks[name+"RescreenThenResume"]=resumeStatus==HandoffStatus.Committed && !resumeAdapter.Result.Collided && resumed.Offset==11 &&
            resumedPositions[0].SequenceEqual(new double[]{-5,0,0}) && resumedPositions[1].SequenceEqual(new double[]{1,0,0});
        world.Replace(initial);long restoreRevision=world.Revision;
        var oldStatus=world.Execute(plan,1,20,adapter.Solve,CancellationToken.None);
        checks[name+"RestoreInvalidatesOldPlan"]=oldStatus!=HandoffStatus.Committed && Hash(world.Capture())==Hash(initial) && restoreRevision>committedRevision;
        var replayPlan=world.Plan(20,new EncounterBudget());var replayAdapter=new Adapter(origin,boost);
        var replayStatus=world.Execute(replayPlan,1,20,replayAdapter.Solve,CancellationToken.None);
        bool replayEquivalent=replayStatus==HandoffStatus.Committed && Hash(world.Capture())==firstHash;
        checks[name+"ReplayEquivalent"]=replayEquivalent;
        return new {name,origin=V(origin),sharedVelocity=V(boost),before=State(initial),after=State(after),status=status.ToString(),
            contactSeconds=adapter.Result.ContactSeconds,solverBodies=adapter.SolverBodies,normalizedPositions=positions,normalizedVelocities=velocities,
            resumedOffset=resumed.Offset,resumedNormalizedPositions=resumedPositions,momentum,kineticEnergy=energy,committedRevision,restoreRevision,replayEquivalent,semanticSha256=firstHash,
            candidateLowerSeconds=plan.Encounter.Candidates[0].LowerSeconds};
    }
    static object RejectCases(SortedDictionary<string,bool> checks)
    {
        var initial=Initial(new Vec(),new Vec());var world=new HandoffWorld(initial);var adapter=new Adapter(new Vec(),new Vec());
        var plan=world.Plan(20,new EncounterBudget());string unchanged=Hash(world.Capture());
        var malformed=world.Execute(plan,1,20,(input,token)=>{
            var valid=adapter.Solve(input,token);
            return new HandoffSnapshot(valid.Frame,valid.BaseEpoch,valid.Offset,new[]{valid.Bodies[0]});
        },CancellationToken.None);
        checks["malformedNoPartialPublication"]=malformed==HandoffStatus.InvalidResult && Hash(world.Capture())==unchanged;
        var fault=world.Execute(plan,1,20,(input,token)=>throw new InvalidOperationException("Injected solver fault"),CancellationToken.None);
        checks["faultNoPublication"]=fault==HandoffStatus.Faulted && Hash(world.Capture())==unchanged;
        var cts=new CancellationTokenSource();cts.Cancel();
        var cancelled=world.Execute(plan,1,20,adapter.Solve,cts.Token);
        checks["cancelNoPublication"]=cancelled==HandoffStatus.Cancelled && Hash(world.Capture())==unchanged;
        var replacement=new HandoffSnapshot(initial.Frame,initial.BaseEpoch,initial.Offset,initial.Bodies.Concat(new[]{new HandoffBody(4,0,1,1,new Vec(500,500,0),new Vec())}));
        var stale=world.Execute(plan,1,20,(input,token)=>{var result=adapter.Solve(input,token);world.Replace(replacement);return result;},CancellationToken.None);
        checks["stagingRejectsStaleSolve"]=stale==HandoffStatus.Stale && Hash(world.Capture())==Hash(replacement);
        var fresh=world.Plan(20,new EncounterBudget());
        checks["debrisIncludedInFreshPlan"]=fresh.Encounter.Advances.Count==4;
        return new {malformed=malformed.ToString(),fault=fault.ToString(),cancelled=cancelled.ToString(),stale=stale.ToString(),freshPlanBodies=fresh.Encounter.Advances.Count};
    }
    static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2||args[0]!="--output"||File.Exists(args[1])||Directory.Exists(args[1]))throw new ArgumentException("Use --output NEW_PATH.");
            var checks=new SortedDictionary<string,bool>();
            var runs=new[]{Run("local",new Vec(),new Vec(),0,checks),Run("translated",new Vec(Math.Pow(2,40),-Math.Pow(2,40),0),new Vec(),0,checks),
                Run("translatedBoosted",new Vec(Math.Pow(2,40),-Math.Pow(2,40),0),new Vec(1048576,-524288,0),Math.Pow(2,60),checks)};
            var rejections=RejectCases(checks);
            var report=new {schema="ksp-continuum-handoff-bench/v1",qualified=checks.Values.All(x=>x),createdUtc=DateTime.UtcNow.ToString("o"),
                gameIntegrated=false,asynchronousIslandsQualified=false,crossPlatformDeterminismQualified=false,performanceMeasured=false,
                model="Straight inertial sphere trajectories, translating local solver frame, first-contact elastic response, common-time atomic in-memory publication. Same-process checkpoint replay.",checks,runs,rejections};
            using(var stream=new FileStream(args[1],FileMode.CreateNew,FileAccess.Write))JsonSerializer.Serialize(stream,report,Json);
            Console.WriteLine(JsonSerializer.Serialize(new {report.qualified,output=args[1]}));return report.qualified?0:2;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
