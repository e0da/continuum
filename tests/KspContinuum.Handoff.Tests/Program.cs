using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KspContinuum;
class Program
{
 static int assertions;
 static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
 static HandoffSnapshot Initial(string frame="local",double epoch=1e20){return new HandoffSnapshot(frame,epoch,0,new[]{new HandoffBody(1,0,1,1,new Vec(-10,0,0),new Vec(1,0,0)),new HandoffBody(2,0,1,1,new Vec(10,0,0),new Vec(-1,0,0)),new HandoffBody(3,0,1,1,new Vec(1000,0,0),new Vec(1,0,0))});}
 static HandoffSnapshot Coast(HandoffInput input,CancellationToken token){return At(input,input.TargetOffset);}
 static HandoffSnapshot At(HandoffInput input,double offset){var bodies=new List<HandoffBody>();foreach(var b in input.Snapshot.Bodies)bodies.Add(new HandoffBody(b.Id,b.Generation,b.Mass,b.Radius,b.Position+b.Velocity*(offset-input.Snapshot.Offset),b.Velocity));return new HandoffSnapshot(input.Snapshot.Frame,input.Snapshot.BaseEpoch,offset,bodies);}
 static HandoffSnapshot ContactSolve(HandoffInput input,CancellationToken token)
 {
  var a=input.Snapshot.Bodies[0];var b=input.Snapshot.Bodies[1];
  var result=SphereContactSolver.Solve(new SphereContactBody(a.Id,a.Position,a.Velocity,a.Radius,a.Mass),new SphereContactBody(b.Id,b.Position,b.Velocity,b.Radius,b.Mass),input.TargetOffset-input.Snapshot.Offset,token);
  if(result.Status!=SphereContactStatus.Complete)throw new InvalidOperationException("Contact solve failed.");
  return new HandoffSnapshot(input.Snapshot.Frame,input.Snapshot.BaseEpoch,input.Snapshot.Offset+result.ElapsedSeconds,new[]{new HandoffBody(a.Id,a.Generation,a.Mass,a.Radius,result.First.Position,result.First.Velocity),new HandoffBody(b.Id,b.Generation,b.Mass,b.Radius,result.Second.Position,result.Second.Velocity)});
 }
 static void Contact()
 {
  var world=new HandoffWorld(Initial());var checkpoint=world.Capture();
  Check(world.Execute(world.Plan(20,new EncounterBudget()),1,20,ContactSolve,CancellationToken.None)==HandoffStatus.Committed,"actual elastic contact committed");
  var first=world.Capture();Check(first.Offset==9,"first contact time, no remainder coast");
  Check(first.Bodies[0].Velocity.X==-1&&first.Bodies[1].Velocity.X==1,"elastic velocity exchange");
  Check(first.Bodies[0].Position.X==-1&&first.Bodies[1].Position.X==1,"contact endpoint");
  var next=world.Plan(5,new EncounterBudget());Check(next.Revision==world.Revision,"fresh plan after response");
  world.Replace(checkpoint);
  Check(world.Execute(world.Plan(20,new EncounterBudget()),1,20,ContactSolve,CancellationToken.None)==HandoffStatus.Committed,"elastic replay");
  Check(world.Capture().Bodies[0].Velocity.X==first.Bodies[0].Velocity.X&&world.Capture().Offset==first.Offset,"elastic exact same runtime replay");
 }
 static void Adversaries()
 {
  var world=new HandoffWorld(Initial());var checkpoint=world.Capture();var plan=world.Plan(20,new EncounterBudget());
  var foreign=new HandoffWorld(Initial()).Plan(20,new EncounterBudget());
  Check(world.Execute(foreign,1,20,Coast,CancellationToken.None)==HandoffStatus.Rejected,"foreign rejected");
  Check(world.Execute(plan,999,20,Coast,CancellationToken.None)==HandoffStatus.Rejected,"missing group rejected");
  var exhausted=world.Plan(20,new EncounterBudget(0));
  Check(world.Execute(exhausted,1,20,Coast,CancellationToken.None)==HandoffStatus.Rejected,"incomplete rejected");
  var cancel=new CancellationTokenSource();cancel.Cancel();
  Check(world.Execute(plan,1,20,Coast,cancel.Token)==HandoffStatus.Cancelled,"pre-cancel");
  Check(world.Execute(plan,1,20,(i,t)=>{throw new Exception();},CancellationToken.None)==HandoffStatus.Faulted,"fault");
  Check(object.ReferenceEquals(world.Capture(),checkpoint),"fault leaves snapshot unchanged");
  Check(world.Execute(plan,1,20,(i,t)=>null,CancellationToken.None)==HandoffStatus.InvalidResult,"null invalid");
  for(int mode=0;mode<9;mode++)
  {
   int m=mode;var result=world.Execute(plan,1,20,(i,t)=>{
    var valid=At(i,9);var a=valid.Bodies[0];var b=valid.Bodies[1];
    if(m==0)return new HandoffSnapshot("wrong",valid.BaseEpoch,9,valid.Bodies);
    if(m==1)return new HandoffSnapshot(valid.Frame,0,9,valid.Bodies);
    if(m==2)return new HandoffSnapshot(valid.Frame,valid.BaseEpoch,21,valid.Bodies);
    if(m==3)return new HandoffSnapshot(valid.Frame,valid.BaseEpoch,9,new[]{b,a});
    if(m==4)return new HandoffSnapshot(valid.Frame,valid.BaseEpoch,9,new[]{a});
    if(m==5)b=new HandoffBody(b.Id,b.Generation+1,b.Mass,b.Radius,b.Position,b.Velocity);
    if(m==6)b=new HandoffBody(b.Id,b.Generation,b.Mass+1,b.Radius,b.Position,b.Velocity);
    if(m==7)b=new HandoffBody(b.Id,b.Generation,b.Mass,b.Radius+1,b.Position,b.Velocity);
    if(m==8)b=new HandoffBody(b.Id,b.Generation,b.Mass,b.Radius,new Vec(999,0,0),b.Velocity);
    return new HandoffSnapshot(valid.Frame,valid.BaseEpoch,9,new[]{a,b});
   },CancellationToken.None);
   Check(result==HandoffStatus.InvalidResult,"bad envelope "+mode);
   Check(object.ReferenceEquals(world.Capture(),checkpoint),"no partial writes "+mode);
  }
  using(var entered=new ManualResetEventSlim())using(var resume=new ManualResetEventSlim())
  {
   var task=Task.Run(()=>world.Execute(plan,1,20,(i,t)=>{entered.Set();resume.Wait();return At(i,9);},CancellationToken.None));
   entered.Wait();
   Check(world.Execute(plan,1,20,Coast,CancellationToken.None)==HandoffStatus.Busy,"single active transaction");
   world.Replace(Initial("changed"));world.Replace(checkpoint);resume.Set();
   Check(task.GetAwaiter().GetResult()==HandoffStatus.Stale,"change-return invalidates");
  }
  Check(world.Execute(plan,1,20,Coast,CancellationToken.None)==HandoffStatus.Stale,"restoring checkpoint never revives plan");
  plan=world.Plan(20,new EncounterBudget());cancel=new CancellationTokenSource();
  Check(world.Execute(plan,1,20,(i,t)=>{cancel.Cancel();return At(i,9);},cancel.Token)==HandoffStatus.Cancelled,"uncooperative solver cancelled");
  Check(world.Capture().Offset==0,"cancelled no publication");
  Check(world.Execute(plan,1,20,(i,t)=>{var capture=Task.Run(()=>world.Capture());Check(capture.Wait(5000),"solver outside lock permits another thread");Check(capture.Result.Offset==0,"solver can capture owner");return At(i,9);},CancellationToken.None)==HandoffStatus.Committed,"owner available inside solver");
  var first=world.Capture();world.Replace(checkpoint);plan=world.Plan(20,new EncounterBudget());
  Check(world.Execute(plan,1,20,(i,t)=>At(i,9),CancellationToken.None)==HandoffStatus.Committed,"replay commit");
  var replay=world.Capture();for(int j=0;j<first.Bodies.Count;j++)Check(first.Bodies[j].Position.X==replay.Bodies[j].Position.X,"same-runtime exact replay "+j);
  var multi=new List<HandoffBody>(Initial().Bodies);
  multi.Add(new HandoffBody(4,0,1,1,new Vec(0,100,0),new Vec(1,0,0)));
  multi.Add(new HandoffBody(5,0,1,1,new Vec(4,100,0),new Vec(-1,0,0)));
  var capped=new HandoffWorld(new HandoffSnapshot("local",0,0,multi));var capPlan=capped.Plan(20,new EncounterBudget());double seenCap=-1;
  Check(capped.Execute(capPlan,1,20,(i,t)=>{seenCap=i.TargetOffset;return Coast(i,t);},CancellationToken.None)==HandoffStatus.Committed,"other group permits bounded coast");
  Check(seenCap<1&&seenCap>0.99,"earlier other encounter caps publication");
  Check(capped.Capture().Offset==seenCap,"common time equals conservative cap");
  var overflowing=new HandoffWorld(new HandoffSnapshot("overflow",0,0,new[]{new HandoffBody(9,0,1,1,new Vec(1e308,0,0),new Vec(1e308,0,0))}));
  var overflowCheckpoint=overflowing.Capture();bool called=false;
  Check(overflowing.Execute(overflowing.Plan(20,new EncounterBudget()),9,20,(i,t)=>{called=true;return Coast(i,t);},CancellationToken.None)==HandoffStatus.Rejected,"nonfinite quiet forecast rejected before admission");
  Check(!called&&object.ReferenceEquals(overflowCheckpoint,overflowing.Capture()),"overflow cannot cause partial publication");
  var source=new[]{new HandoffBody(8,0,1,1,new Vec(),new Vec())};var snapshot=new HandoffSnapshot("copy",0,0,source);source[0]=null;
  Check(snapshot.Bodies[0]!=null,"defensive input membership copy");
  bool rejected=false;try{((IList<HandoffBody>)snapshot.Bodies)[0]=null;}catch(NotSupportedException){rejected=true;}Check(rejected,"read only capture");
 }
 static void Main()
 {
  var world=new HandoffWorld(Initial());
  Check(world.Capture().Bodies.Count==3,"capture initial scene");
  var plan=world.Plan(20,new EncounterBudget());long revision=world.Revision;
  Check(plan.Encounter.Epoch==0&&plan.Snapshot.BaseEpoch==1e20&&plan.Snapshot.Offset==0,"plan explicitly relative to split time snapshot");
  var status=world.Execute(plan,1,20,(input,token)=>At(input,9),CancellationToken.None);
  Check(status==HandoffStatus.Committed,"commit first event");Check(world.Revision==revision+1,"one revision per commit");
  Check(world.Capture().Offset==9&&world.Capture().BaseEpoch==1e20,"split time retained");
  Check(world.Capture().Bodies[2].Position.X==1009,"quiet body coasts to barrier");
  Check(world.Execute(plan,1,20,Coast,CancellationToken.None)==HandoffStatus.Stale,"old plan invalid");
  Adversaries();
  Contact();
  Console.WriteLine("Handoff assertions: "+assertions);
 }
}
