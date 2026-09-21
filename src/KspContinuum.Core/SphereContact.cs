using System;
using System.Threading;
namespace KspContinuum
{
    public enum SphereContactStatus { Complete, InitialOverlap, NumericalFailure, Cancelled }
    public sealed class SphereContactBody
    {
        public int Id {get;private set;}
        public Vec Position {get;private set;}
        public Vec Velocity {get;private set;}
        public double Radius {get;private set;}
        public double Mass {get;private set;}
        public SphereContactBody(int id,Vec position,Vec velocity,double radius,double mass)
        {
            if(id<0)throw new ArgumentException("Negative sphere identity.");
            Validate(position);Validate(velocity);AssemblyModel.Positive(radius);AssemblyModel.Positive(mass);
            Id=id;Position=position;Velocity=velocity;Radius=radius;Mass=mass;
        }
        internal static void Validate(Vec value) {AssemblyModel.Finite(value.X);AssemblyModel.Finite(value.Y);AssemblyModel.Finite(value.Z);}
    }
    public sealed class SphereContactResult
    {
        public SphereContactStatus Status {get;internal set;}
        public SphereContactBody First {get;internal set;}
        public SphereContactBody Second {get;internal set;}
        public double ElapsedSeconds {get;internal set;}
        public bool Collided {get {return ContactSeconds.HasValue;}}
        public double? ContactSeconds {get;internal set;}
    }
    public static class SphereContactSolver
    {
        public static SphereContactResult Solve(SphereContactBody first,SphereContactBody second,double duration,CancellationToken cancellation=default(CancellationToken))
        {
            if(first==null||second==null||first.Id==second.Id)throw new ArgumentException("Two distinct spheres required.");
            AssemblyModel.Finite(duration);if(duration<0)throw new ArgumentException("Negative duration.");
            var failed=new SphereContactResult {Status=SphereContactStatus.NumericalFailure,First=first,Second=second};
            if(cancellation.IsCancellationRequested) {failed.Status=SphereContactStatus.Cancelled;return failed;}
            Vec q=second.Position+first.Position*-1, v=second.Velocity+first.Velocity*-1;
            double distance2=Dot(q,q),speed2=Dot(v,v),radius=first.Radius+second.Radius;
            double distance=Math.Sqrt(distance2),speed=Math.Sqrt(speed2),approach=Dot(q,v);
            if(!Finite(distance)||!Finite(speed)||!Finite(radius)||!Finite(approach)||!Finite(radius*radius)||radius*radius==0) return failed;
            if(distance<radius) {failed.Status=SphereContactStatus.InitialOverlap;return failed;}
            // This analytic fixture rejects badly conditioned long-baseline contacts; it is not a certified CCD backend.
            if(radius==0 || distance/radius>1e8 || (speed2==0 && (v.X!=0||v.Y!=0||v.Z!=0)))return failed;
            double elapsed=duration;double? contact=null;
            Vec firstVelocity=first.Velocity,secondVelocity=second.Velocity;
            if(speed>0 && approach<0)
            {
                Vec perpendicular=Vec.Cross(q,v)*(1/speed);
                double miss2=Dot(perpendicular,perpendicular);
                if(!Finite(miss2))return failed;
                if(miss2<=radius*radius)
                {
                    double miss=Math.Sqrt(miss2);
                    double halfChord=Math.Sqrt((radius-miss)*(radius+miss));
                    double denominator=-approach+speed*halfChord;
                    if(!Finite(denominator)||denominator<=0)return failed;
                    double entry=((distance-radius)*(distance+radius))/denominator;
                    if(!Finite(entry)||entry<0)return failed;
                    if(entry<=duration)
                    {
                        elapsed=entry;contact=entry;
                        Vec separation=q+v*entry;
                        double separationLength=Math.Sqrt(Dot(separation,separation));
                        if(!Finite(separationLength)||separationLength<=0)return failed;
                        Vec normal=separation*(1/separationLength);
                        double normalSpeed=Dot(v,normal);
                        if(!Finite(normalSpeed)||normalSpeed>1e-10*speed)return failed;
                        normalSpeed=Math.Min(0,normalSpeed);
                        double massSum=first.Mass+second.Mass;
                        if(!Finite(massSum))return failed;
                        firstVelocity=first.Velocity+normal*(2*normalSpeed*(second.Mass/massSum));
                        secondVelocity=second.Velocity+normal*(-2*normalSpeed*(first.Mass/massSum));
                    }
                }
            }
            Vec firstPosition=first.Position+first.Velocity*elapsed,secondPosition=second.Position+second.Velocity*elapsed;
            if(!Finite(firstPosition)||!Finite(secondPosition)||!Finite(firstVelocity)||!Finite(secondVelocity))return failed;
            if(cancellation.IsCancellationRequested) {failed.Status=SphereContactStatus.Cancelled;return failed;}
            return new SphereContactResult {Status=SphereContactStatus.Complete,ElapsedSeconds=elapsed,ContactSeconds=contact,
                First=new SphereContactBody(first.Id,firstPosition,firstVelocity,first.Radius,first.Mass),
                Second=new SphereContactBody(second.Id,secondPosition,secondVelocity,second.Radius,second.Mass)};
        }
        static double Dot(Vec a,Vec b) {return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
        static bool Finite(double value) {return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static bool Finite(Vec value) {return Finite(value.X)&&Finite(value.Y)&&Finite(value.Z);}
    }
}
