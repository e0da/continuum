using System;
using System.Threading;
using KspContinuum;
static class Program
{
    static int checks;
    static void Check(bool value,string message) {checks++;if(!value)throw new Exception(message);}
    static void Near(double actual,double expected,string message,double tolerance=1e-10) => Check(Math.Abs(actual-expected)<=tolerance,message+": "+actual+" != "+expected);
    static SphereContactBody B(int id,double x,double vx,double mass=1,double y=0,double vy=0) => new SphereContactBody(id,new Vec(x,y,0),new Vec(vx,vy,0),1,mass);
    static void Main()
    {
        var a=B(1,-5,1);var b=B(2,5,-1);
        var r=SphereContactSolver.Solve(a,b,6);
        Check(r.Status==SphereContactStatus.Complete && r.Collided,"approaching spheres collide");
        Near(r.ContactSeconds.Value,4,"actual contact time");Near(r.First.Position.X,-1,"first contact position");Near(r.Second.Position.X,1,"second contact position");
        Near(r.First.Velocity.X,-1,"elastic first velocity");Near(r.Second.Velocity.X,1,"elastic second velocity");
        var unequal=SphereContactSolver.Solve(B(1,-5,2,2),B(2,5,0,1),5);
        Check(unequal.Collided,"unequal masses collide");
        Near(2*unequal.First.Velocity.X+unequal.Second.Velocity.X,4,"momentum");
        Near(unequal.First.Velocity.X*unequal.First.Velocity.X+.5*unequal.Second.Velocity.X*unequal.Second.Velocity.X,4,"energy");
        var miss=SphereContactSolver.Solve(B(1,-5,1,y:3),B(2,0,0),10);
        Check(miss.Status==SphereContactStatus.Complete && !miss.Collided,"positive clearance miss");Near(miss.First.Position.X,5,"miss coasts");
        var tangent=SphereContactSolver.Solve(B(1,-5,1,y:2),B(2,0,0),10);
        Check(tangent.Status==SphereContactStatus.Complete && tangent.Collided,"tangent contact");Near(tangent.First.Velocity.X,1,"tangent no impulse");
        Check(SphereContactSolver.Solve(B(1,0,0),B(2,1,0),1).Status==SphereContactStatus.InitialOverlap,"overlap rejected");
        Check(!SphereContactSolver.Solve(B(1,-1,-1),B(2,1,1),1).Collided,"touch separating no impulse");
        Near(SphereContactSolver.Solve(B(1,-1,1),B(2,1,-1),1).ContactSeconds.Value,0,"touch approaching immediate");
        Check(!SphereContactSolver.Solve(a,b,3).Collided,"contact beyond horizon");
        Near(SphereContactSolver.Solve(a,b,4).ContactSeconds.Value,4,"endpoint contact");
        var cancel=new CancellationTokenSource();cancel.Cancel();
        var cancelled=SphereContactSolver.Solve(a,b,6,cancel.Token);
        Check(cancelled.Status==SphereContactStatus.Cancelled && object.ReferenceEquals(a,cancelled.First),"cancel leaves input unchanged");
        bool rejected=false;try{SphereContactSolver.Solve(a,b,double.NaN);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid duration");
        rejected=false;try{B(1,0,0,mass:0);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid mass");
        var overflow=SphereContactSolver.Solve(B(1,-1e200,1),B(2,1e200,-1),1);
        Check(overflow.Status==SphereContactStatus.NumericalFailure && overflow.ElapsedSeconds==0,"overflow fails closed");
        var grazing=SphereContactSolver.Solve(B(1,-5,1,y:1),B(2,0,0),10);
        Check(grazing.Collided,"oblique collision");
        var v1=grazing.First.Velocity;var v2=grazing.Second.Velocity;
        Near(v1.X+v2.X,1,"oblique momentum x");Near(v1.Y+v2.Y,0,"oblique momentum y");
        Near(v1.X*v1.X+v1.Y*v1.Y+v2.X*v2.X+v2.Y*v2.Y,1,"oblique energy");
        var shifted=SphereContactSolver.Solve(B(1,1024-5,257),B(2,1024+5,255),6);
        Near(shifted.First.Position.X-(1024+256*4),r.First.Position.X,"representable translating frame position");
        Near(shifted.First.Velocity.X-256,r.First.Velocity.X,"representable translating frame velocity");
        var tinyA=new SphereContactBody(1,new Vec(),new Vec(),1e-200,1);
        var tinyB=new SphereContactBody(2,new Vec(1e-199,0,0),new Vec(),1e-200,1);
        Check(SphereContactSolver.Solve(tinyA,tinyB,1).Status==SphereContactStatus.NumericalFailure,"underflow cannot masquerade as overlap");
        var hugeA=new SphereContactBody(1,new Vec(),new Vec(),5e153,1);
        var hugeB=new SphereContactBody(2,new Vec(1.3e154,0,0),new Vec(-1.3e154,0,0),5e153,1);
        Check(SphereContactSolver.Solve(hugeA,hugeB,1).Status==SphereContactStatus.NumericalFailure,"overflow denominator must not invent immediate contact");
        Console.WriteLine("Contact assertions: "+checks);
    }
}
