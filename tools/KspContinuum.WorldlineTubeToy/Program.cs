using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KspContinuum;

sealed class Case
{
    public string Name=""; public WorldlineTube A=null!,B=null!; public double Horizon;
    public Func<double,Vec> ActualA=null!,ActualB=null!; public bool OracleApplicable=true;
}
static class Program
{
    static readonly TubeOrientation Identity=new TubeOrientation(0,0,0,1);
    static Vec V(double x,double y=0,double z=0)=>new Vec(x,y,z);
    static WorldlineTube T(int id,Vec p,Vec v,Vec a,double radius=1,double pos=0,double vel=0,double? accel=0,double valid=10,bool predicate=true)
        =>new WorldlineTube(id,1,1000,"toy-inertial",p,v,a,V(radius,radius,radius),Identity,0,pos,vel,accel,valid,predicate);
    static Vec At(Vec p,Vec v,Vec a,double t)=>p+v*t+a*(.5*t*t);
    static double D(Vec a,Vec b){double x=a.X-b.X,y=a.Y-b.Y,z=a.Z-b.Z;return Math.Sqrt(x*x+y*y+z*z);}
    static bool Dense(Case c)
    {
        double radiusA=Math.Sqrt(c.A.SupportHalfExtents.X*c.A.SupportHalfExtents.X+c.A.SupportHalfExtents.Y*c.A.SupportHalfExtents.Y+c.A.SupportHalfExtents.Z*c.A.SupportHalfExtents.Z);
        double radiusB=Math.Sqrt(c.B.SupportHalfExtents.X*c.B.SupportHalfExtents.X+c.B.SupportHalfExtents.Y*c.B.SupportHalfExtents.Y+c.B.SupportHalfExtents.Z*c.B.SupportHalfExtents.Z);
        for(int i=0;i<=200000;i++){double t=c.Horizon*i/200000.0;if(D(c.ActualA(t),c.ActualB(t))<=radiusA+radiusB+1e-10)return true;}return false;
    }
    static Case C(string name,WorldlineTube a,WorldlineTube b,double h,Func<double,Vec> aa,Func<double,Vec> bb,bool oracle=true)
        =>new Case{Name=name,A=a,B=b,Horizon=h,ActualA=aa,ActualB=bb,OracleApplicable=oracle};
    public static int Main(string[] args)
    {
        string output=null;for(int i=0;i<args.Length;i++){if(args[i]=="--output"&&++i<args.Length)output=args[i];else throw new ArgumentException("Usage: --output NEW_PATH");}
        if(output==null||File.Exists(output))throw new ArgumentException("A new output path is required.");
        var cases=new List<Case>();
        Vec zero=V(0); double r=1;
        cases.Add(C("straight-clear",T(1,V(0),V(1),zero,r),T(2,V(20),V(1),zero,r),10,t=>V(t),t=>V(20+t)));
        cases.Add(C("accelerated-crossing",T(1,V(-10),zero,V(2),r),T(2,V(10),zero,V(-2),r),4,t=>At(V(-10),zero,V(2),t),t=>At(V(10),zero,V(-2),t)));
        cases.Add(C("crossing",T(1,V(-10),V(2),zero,r),T(2,V(10),V(-2),zero,r),6,t=>V(-10+2*t),t=>V(10-2*t)));
        cases.Add(C("tangent",T(1,V(-10),V(2),zero,r),T(2,V(0,2*Math.Sqrt(3)),zero,zero,r),6,t=>V(-10+2*t),t=>V(0,2*Math.Sqrt(3))));
        cases.Add(C("near-miss",T(1,V(-10),V(2),zero,r),T(2,V(0,3.6),zero,zero,r),6,t=>V(-10+2*t),t=>V(0,3.6)));
        var uncertain=T(1,V(-10),V(1),zero,r,0,0,2,6);
        cases.Add(C("uncertain-burn",uncertain,T(2,V(10),V(-1),zero,r),6,t=>At(V(-10),V(1),V(2),t),t=>V(10-t)));
        cases.Add(C("conservative-false-positive",T(1,V(-10),V(1),zero,r,5),T(2,V(10),V(-1),zero,r,5),4,t=>V(-10+t),t=>V(10-t)));
        cases.Add(C("expired",T(1,V(0),zero,zero,r,0,0,0,1),T(2,V(10),zero,zero,r),2,t=>V(0),t=>V(10),false));
        cases.Add(C("expired-and-unknown",T(1,V(0),zero,zero,r,0,0,null,1,false),T(2,V(10),zero,zero,r),2,t=>V(0),t=>V(10),false));
        cases.Add(C("unknown-bound",T(1,V(0),zero,zero,r,0,0,null),T(2,V(10),zero,zero,r),2,t=>V(0),t=>V(10),false));
        cases.Add(C("unknown-predicate",T(1,V(0),zero,zero,r,0,0,0,10,false),T(2,V(10),zero,zero,r),2,t=>V(0),t=>V(10),false));
        var rows=new List<object>();int positives=0,falsePositives=0,falseNegatives=0,totalIntervals=0;
        foreach(var c in cases){var s=WorldlineTubeScreener.Screen(c.A,c.B,c.Horizon,new EncounterBudget(timeToleranceSeconds:1e-5));bool oracle=c.OracleApplicable&&Dense(c);bool candidate=s.Status==WorldlineTubeStatus.Candidate;if(oracle)positives++;if(c.OracleApplicable&&candidate&&!oracle)falsePositives++;if(oracle&&!candidate)falseNegatives++;totalIntervals+=s.IntervalTests;rows.Add(new{name=c.Name,oracleApplicable=c.OracleApplicable,oracleContact=oracle,status=s.Status.ToString().ToLowerInvariant(),s.PairTests,s.IntervalTests,s.CandidateLowerSeconds,s.CandidateUpperSeconds});}
        bool qualified=falseNegatives==0;
        var receipt=new{schema="ksp-continuum-worldline-tube-toy/v1",qualified,oracle="dense 200001-sample center-distance check; analytic polynomial fixtures",oraclePositiveCases=positives,falseNegatives,falsePositives,totalIntervalTests=totalIntervals,deterministicInputs=true,productionSafetyQualified=false,cases=rows};
        var json=JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true})+"\n";Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,json);
        Console.Write(json);return qualified?0:1;
    }
}
