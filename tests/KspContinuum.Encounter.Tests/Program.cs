using System;
using System.Linq;
using System.Collections.Generic;
using KspContinuum;
static class Program
{
    static int assertions;
    static void Check(bool condition,string reason) { assertions++; if(!condition) throw new Exception("Encounter assertion "+assertions+": "+reason); }
    static EncounterMotion M(int id, Vec p,Vec v,double radius=1,double? acceleration=0,double lookahead=20,double validity=20,long generation=0)
        =>new EncounterMotion(id,generation,0,"inertial",p,v,radius,validity,lookahead,accelerationBound:acceleration);
    static double Advance(EncounterPlan p,int id)=>p.Advances.Single(a=>a.ObjectId==id).SafeAdvanceSeconds;
    static EncounterPlan P(params EncounterMotion[] motions)=>EncounterPlanner.Plan(motions,20,new EncounterBudget());
    static bool Reject(Action action) {try {action();return false;}catch(ArgumentException){return true;}}
    static void CurvedTests()
    {
        var legacyConstructor=typeof(EncounterMotion).GetConstructor(new[]{typeof(int),typeof(long),typeof(double),typeof(string),typeof(Vec),typeof(Vec),
            typeof(double),typeof(double),typeof(double),typeof(double),typeof(double),typeof(double?)});
        Check(legacyConstructor!=null,"legacy twelve-parameter CLR constructor remains available");
        var legacy=(EncounterMotion)legacyConstructor.Invoke(new object[]{1,0L,0d,"inertial",new Vec(),new Vec(),1d,20d,20d,0d,0d,(double?)0});
        Check(legacy.NominalAcceleration.X==0 && legacy.NominalAcceleration.Y==0 && legacy.NominalAcceleration.Z==0,"legacy constructor forwards to zero nominal acceleration");
        var anchor=M(1,new Vec(),new Vec());
        var curved=new EncounterMotion(2,4,0,"inertial",new Vec(-10,100,0),new Vec(1,-20,0),1,20,20,
            accelerationBound:0,nominalAcceleration:new Vec(0,2,0));
        var plan=P(anchor,curved);
        double entry=10-Math.Sqrt((Math.Sqrt(17)-1)*.5);
        Check(plan.Status==EncounterPlanStatus.Complete && plan.Candidates.Count==1,"curved interior approach survives separated endpoints and missed linear path");
        Check(Advance(plan,1)<=entry && Advance(plan,1)>entry-.002,"curved first approach localized without late stop");
        Check(plan.Candidates[0].UpperSeconds-plan.Candidates[0].LowerSeconds<=.0001,"curved localization width");
        var miss=new EncounterMotion(2,4,0,"inertial",new Vec(-10,103,0),new Vec(1,-20,0),1,20,20,
            accelerationBound:0,nominalAcceleration:new Vec(0,2,0));
        plan=P(anchor,miss);
        Check(plan.Status==EncounterPlanStatus.Complete && plan.Candidates.Count==0,"curved positive-clearance miss");
        var unknown=new EncounterMotion(2,4,0,"inertial",new Vec(-10,100,0),new Vec(1,-20,0),1,20,20,
            nominalAcceleration:new Vec(0,2,0));
        plan=P(anchor,unknown);
        Check(plan.Status==EncounterPlanStatus.UnknownBounds && plan.Advances.All(x=>x.SafeAdvanceSeconds==0),"known nominal term does not supply missing residual bound");
        var common=new Vec(2,-4,6);
        plan=P(new EncounterMotion(1,0,0,"inertial",new Vec(),new Vec(),1,20,20,accelerationBound:0,nominalAcceleration:common),
            new EncounterMotion(2,0,0,"inertial",new Vec(3,0,0),new Vec(),1,20,20,accelerationBound:0,nominalAcceleration:common));
        Check(plan.Status==EncounterPlanStatus.Complete && plan.Candidates.Count==0,"common acceleration cancels in relative motion");
        var linear=P(anchor,M(2,new Vec(10,0,0),new Vec(-1,0,0)));
        var zero=P(anchor,new EncounterMotion(2,0,0,"inertial",new Vec(10,0,0),new Vec(-1,0,0),1,20,20,accelerationBound:0,nominalAcceleration:new Vec()));
        Check(linear.Candidates[0].LowerSeconds==zero.Candidates[0].LowerSeconds && linear.WorkUsed.IntervalTests==zero.WorkUsed.IntervalTests,"explicit zero preserves linear path");
        Check(Reject(()=>new EncounterMotion(2,0,0,"inertial",new Vec(),new Vec(),1,20,20,accelerationBound:0,nominalAcceleration:new Vec(0,double.NaN,0))),"nonfinite nominal acceleration rejected");
        var scheduler=new EncounterScheduler();scheduler.Replace(new[]{anchor,curved});var previous=scheduler.Plan(20,new EncounterBudget());
        scheduler.Replace(new[]{anchor,miss});Check(!scheduler.IsCurrent(previous),"edited curved prediction invalidates prior plan");
    }
    static void Main()
    {
        var a=M(1,new Vec(-25000,0,0),new Vec(5000,0,0));
        var b=M(2,new Vec(25000,0,0),new Vec(-5000,0,0));
        var quiet=M(3,new Vec(1e6,0,0),new Vec());
        var p=P(a,b,quiet);
        Check(p.Candidates.Count==1,"opposing fast paths must not tunnel between endpoints");
        var c=p.Candidates[0];
        Check(c.LowerSeconds<=4.9998 && c.UpperSeconds>=4.9998,"analytic entry enclosed");
        Check(c.UpperSeconds-c.LowerSeconds<=.000101,"localized interval");
        Check(Advance(p,1)<=4.9998 && Advance(p,2)<=4.9998 && Advance(p,3)==20,"quiet independence");
        Check(p.Groups.Count==2,"one candidate group plus quiet body");
        var permuted=P(quiet,b,a);
        Check(permuted.Candidates[0].FirstId==c.FirstId && permuted.Candidates[0].LowerSeconds==c.LowerSeconds,"stable ordering");
        p=P(M(1,new Vec(),new Vec()),M(2,new Vec(-10,2,0),new Vec(1,0,0)));
        Check(p.Candidates.Count==1 && p.Candidates[0].LowerSeconds<=10 && p.Candidates[0].UpperSeconds>=10,"tangent retained");
        p=P(M(1,new Vec(),new Vec()),M(2,new Vec(-10,2.000001,0),new Vec(1,0,0)));
        Check(p.Candidates.Count==0 && Advance(p,1)==20,"near tangent miss clears");
        p=P(M(1,new Vec(),new Vec()),M(2,new Vec(1,0,0),new Vec()));
        Check(Advance(p,1)==0,"initial overlap blocks advance");
        p=P(M(1,new Vec(),new Vec(),acceleration:4),M(2,new Vec(10,0,0),new Vec()));
        Check(p.Candidates.Count==1 && Advance(p,1)<=2,"acceleration envelope prevents false linear clearance");
        p=P(M(1,new Vec(),new Vec(),acceleration:null),quiet);
        Check(p.Status==EncounterPlanStatus.UnknownBounds && p.Advances.All(x=>x.SafeAdvanceSeconds==0),"unknown bound prevents advance");
        p=P(M(1,new Vec(),new Vec(),lookahead:0),M(2,new Vec(1,0,0),new Vec()),quiet);
        Check(Advance(p,1)==0 && Advance(p,2)==0 && Advance(p,3)==20,"zero lookahead propagates only modeled candidate group");
        foreach(var budget in new[]{new EncounterBudget(maxPairTests:0),new EncounterBudget(maxCandidates:0),new EncounterBudget(maxIntervalTests:0)})
        {
            p=EncounterPlanner.Plan(new[]{a,b,quiet},20,budget);
            Check(p.Status==EncounterPlanStatus.BudgetExhausted && p.Advances.All(x=>x.SafeAdvanceSeconds==0),"budget never drops pairs as safe");
        }
        var scheduler=new EncounterScheduler(); scheduler.Replace(new[]{a,b}); var previous=scheduler.Plan(20,new EncounterBudget());
        Check(scheduler.IsCurrent(previous),"current scene plan"); scheduler.Replace(new[]{a,b,quiet});
        Check(!scheduler.IsCurrent(previous),"new debris invalidates prior plan");
        var other=new EncounterScheduler(); other.Replace(new[]{a,b});
        Check(!other.IsCurrent(previous),"scheduler instance identity");
        p=P(M(1,new Vec(),new Vec(),radius:1.5),M(2,new Vec(-10,2,2),new Vec(1,0,0),radius:1.5));
        Check(p.Candidates.Count==1 && p.Candidates[0].LowerSeconds<=9 && p.Candidates[0].UpperSeconds>=9,"genuine3D oracle");
        p=P(M(1,new Vec(),new Vec()),M(2,new Vec(-10,2,2),new Vec(1,0,0)));
        Check(p.Candidates.Count==0,"axis overlap can still exclude a sphere pair");
        double shift=Math.Pow(2,40);
        p=P(M(1,new Vec(shift-25000,shift,shift),new Vec(5000,0,0)),M(2,new Vec(shift+25000,shift,shift),new Vec(-5000,0,0)));
        Check(p.Candidates[0].LowerSeconds<=4.9998 && p.Candidates[0].UpperSeconds>=4.9998,"large common origin");
        p=EncounterPlanner.Plan(new[]{M(1,new Vec(),new Vec(),lookahead:1e12,validity:1e12),M(2,new Vec(1e12,0,0),new Vec(-1,0,0),lookahead:1e12,validity:1e12)},1e12,new EncounterBudget());
        Check(Advance(p,1)<=999999999998d,"quadratic cancellation adversary safe stop");
        Check(p.Status==EncounterPlanStatus.NumericalUncertainty,"unattainable requested time resolution explicit");
        p=EncounterPlanner.Plan(new[]{new EncounterMotion(1,0,0,"inertial",new Vec(),new Vec(),1,20,20,3,2,4),M(2,new Vec(10,0,0),new Vec())},20,new EncounterBudget());
        double earliest=(Math.Sqrt(11)-1)/2;
        Check(Advance(p,1)<=earliest && p.Candidates[0].UpperSeconds>=earliest,"combined position velocity acceleration envelope");
        p=P(M(1,new Vec(double.MaxValue,0,0),new Vec(double.MaxValue,0,0)),quiet);
        Check(p.Status==EncounterPlanStatus.NumericalUncertainty && p.Advances.All(x=>x.SafeAdvanceSeconds==0),"overflow never certifies clearance");
        p=P(M(1,new Vec(),new Vec(),validity:0),quiet);
        Check(p.HorizonSeconds==0 && p.Advances.All(x=>x.SafeAdvanceSeconds==0),"expired validity");
        Check(Reject(()=>P(a,a)),"duplicate identity rejected");
        Check(Reject(()=>P(a,new EncounterMotion(2,0,1,"inertial",new Vec(),new Vec(),1,20,20,accelerationBound:0))),"mixed epoch rejected");
        Check(Reject(()=>P(a,new EncounterMotion(2,0,0,"rotating",new Vec(),new Vec(),1,20,20,accelerationBound:0))),"mixed frame rejected");
        Check(Reject(()=>M(1,new Vec(double.NaN,0,0),new Vec())),"nonfinite input rejected");
        Check(Reject(()=>new EncounterBudget(timeToleranceSeconds:0)),"positive localization tolerance");
        var input=new[]{a,b};scheduler.Replace(input);input[0]=quiet;previous=scheduler.Plan(20,new EncounterBudget());
        Check(previous.Advances[0].ObjectId==1,"scheduler copies input membership");
        scheduler.Replace(new[]{M(1,a.Position,a.Velocity,acceleration:1),b});scheduler.Replace(new[]{a,b});
        Check(!scheduler.IsCurrent(previous),"edit then return invalidates original");
        Check(!scheduler.IsCurrent(P(a,b)),"stateless plan cannot masquerade as scheduled");
        bool immutable=false;try{((IList<EncounterCandidate>)previous.Candidates).Clear();}catch(NotSupportedException){immutable=true;}
        Check(immutable,"published candidates immutable");
        var chain=P(M(5,new Vec(),new Vec()),M(8,new Vec(1,0,0),new Vec()),M(2,new Vec(2,0,0),new Vec()),quiet);
        Check(chain.Groups[0].Id==2 && chain.Groups[0].ObjectIds.SequenceEqual(new[]{2,5,8}) && Advance(chain,3)==20,"transitive deterministic grouping");
        var prefix=EncounterPlanner.Plan(new[]{M(1,new Vec(),new Vec()),M(2,new Vec(1,0,0),new Vec()),M(3,new Vec(2,0,0),new Vec())},20,new EncounterBudget(maxCandidates:1));
        Check(prefix.Status==EncounterPlanStatus.BudgetExhausted && prefix.Candidates.Count==1 && prefix.Groups.Count==1 && prefix.Advances.All(x=>x.SafeAdvanceSeconds==0),"retained prefix never partial safe publication");
        var crossAxis=Enumerable.Range(0,1024).Select(i=>M(i,new Vec(0,10*i,0),new Vec())).ToArray();
        p=EncounterPlanner.Plan(crossAxis,20,new EncounterBudget());
        Check(p.Status==EncounterPlanStatus.Complete && p.WorkUsed.PairTests<4096 && p.Advances.All(x=>x.SafeAdvanceSeconds==20),"coordinate orientation must not defeat sparse pruning");
        p=P(M(1,a.Position,a.Velocity,generation:5,lookahead:0),M(2,b.Position,b.Velocity,generation:9),quiet);
        Check(p.Candidates[0].FirstGeneration==5 && p.Candidates[0].SecondGeneration==9,"candidate retains participant generations");
        Check(Advance(p,1)==0 && Advance(p,2)==0 && Advance(p,3)==20,"zero incoming lookahead limits future encounter partner");
        p=P(a,b,M(3,quiet.Position,quiet.Velocity,validity:1));
        Check(p.HorizonSeconds==1 && p.Advances.All(x=>x.SafeAdvanceSeconds==1),"shared minimum validity is explicit");
        p=P(M(1,new Vec(),new Vec(),lookahead:5),M(2,new Vec(10,0,0),new Vec(-1,0,0)),M(3,new Vec(20,0,0),new Vec(-2,0,0)));
        Check(p.Groups.Count==1 && p.Advances.All(x=>x.SafeAdvanceSeconds==5),"transitive group takes earliest incoming limit");
        scheduler.Replace(new[]{a,b});previous=scheduler.Plan(20,new EncounterBudget());scheduler.Replace(new[]{a,b});
        Check(!scheduler.IsCurrent(previous),"identical publication also invalidates prior recommendations");
        immutable=false;try{((IList<int>)previous.Groups[0].ObjectIds)[0]=999;}catch(NotSupportedException){immutable=true;}
        Check(immutable,"group membership immutable");
        CurvedTests();
        Console.WriteLine("Encounter: "+assertions+" assertions passed.");
    }
}
