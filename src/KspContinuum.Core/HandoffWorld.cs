using System;
using System.Collections.Generic;
using System.Threading;
namespace KspContinuum
{
 public sealed class HandoffWorld
 {
  readonly object sync=new object(); readonly Guid id=Guid.NewGuid();
  HandoffSnapshot state; long revision; bool busy;
  public HandoffWorld(HandoffSnapshot initial){Replace(initial);}
  public HandoffSnapshot Capture(){lock(sync)return state;}
  public long Revision {get {lock(sync)return revision;}}
  public void Replace(HandoffSnapshot snapshot)
  {
   if(snapshot==null)throw new ArgumentNullException("snapshot");
   var sorted=new List<HandoffBody>(snapshot.Bodies);sorted.Sort((a,b)=>a.Id.CompareTo(b.Id));
   var copy=new HandoffSnapshot(snapshot.Frame,snapshot.BaseEpoch,snapshot.Offset,sorted);
   lock(sync){long next=checked(revision+1);state=copy;revision=next;}
  }
  public HandoffPlan Plan(double horizon,EncounterBudget budget)
  {
   HandoffSnapshot captured;long version;
   lock(sync){captured=state;version=revision;}
   var motions=new EncounterMotion[captured.Bodies.Count];
   for(int i=0;i<motions.Length;i++){var b=captured.Bodies[i];motions[i]=new EncounterMotion(b.Id,b.Generation,0,captured.Frame,b.Position,b.Velocity,b.Radius,horizon,horizon,0,0,0);}
   return new HandoffPlan(captured,EncounterPlanner.Compute(motions,horizon,budget,id,version));
  }
  public HandoffStatus Execute(HandoffPlan work,int groupId,double targetOffset,Func<HandoffInput,CancellationToken,HandoffSnapshot> solver,CancellationToken cancellation)
  {
   var plan=work==null?null:work.Encounter;
   HandoffSnapshot captured;HandoffInput input;long version;
   lock(sync)
   {
    if(cancellation.IsCancellationRequested)return HandoffStatus.Cancelled;
    if(busy)return HandoffStatus.Busy;
    if(plan==null||plan.SchedulerId!=id||solver==null)return HandoffStatus.Rejected;
    if(plan.Generation!=revision)return HandoffStatus.Stale;
    if(plan.Status!=EncounterPlanStatus.Complete||double.IsNaN(targetOffset)||double.IsInfinity(targetOffset)||targetOffset<state.Offset)return HandoffStatus.Rejected;
    EncounterGroup selected=null;foreach(var g in plan.Groups)if(g.Id==groupId)selected=g;
    if(selected==null)return HandoffStatus.Rejected;
    double duration=Math.Min(targetOffset-state.Offset,plan.HorizonSeconds);
    foreach(var g in plan.Groups)if(g.Id!=groupId)duration=Math.Min(duration,g.SafeAdvanceSeconds);
    double cap=state.Offset+duration;
    if(double.IsInfinity(cap)||cap<state.Offset||cap-state.Offset>duration)return HandoffStatus.Rejected;
    var members=new HashSet<int>(selected.ObjectIds);var bodies=new List<HandoffBody>();
    foreach(var b in state.Bodies)if(members.Contains(b.Id))bodies.Add(b);
    captured=state;version=revision;
    input=new HandoffInput(new HandoffSnapshot(state.Frame,state.BaseEpoch,state.Offset,bodies),cap,selected.SafeAdvanceSeconds);
    busy=true;
   }
   HandoffSnapshot output=null;HandoffStatus outcome=HandoffStatus.Committed;
   try{output=solver(input,cancellation);}
   catch(OperationCanceledException){outcome=cancellation.IsCancellationRequested?HandoffStatus.Cancelled:HandoffStatus.Faulted;}
   catch(Exception){outcome=HandoffStatus.Faulted;}
   lock(sync)
   {
    try
    {
     if(cancellation.IsCancellationRequested)return HandoffStatus.Cancelled;
     if(revision!=version)return HandoffStatus.Stale;
     if(outcome!=HandoffStatus.Committed)return outcome;
     if(!Valid(input,output))return HandoffStatus.InvalidResult;
     var replacements=new Dictionary<int,HandoffBody>();foreach(var b in output.Bodies)replacements.Add(b.Id,b);
     var merged=new List<HandoffBody>();double dt=output.Offset-captured.Offset;
     foreach(var b in captured.Bodies)
     {
      HandoffBody next;
      if(!replacements.TryGetValue(b.Id,out next))next=new HandoffBody(b.Id,b.Generation,b.Mass,b.Radius,b.Position+b.Velocity*dt,b.Velocity);
      merged.Add(next);
     }
     var candidate=new HandoffSnapshot(captured.Frame,captured.BaseEpoch,output.Offset,merged);
     long nextRevision=checked(revision+1);state=candidate;revision=nextRevision;
     return HandoffStatus.Committed;
    }
    catch(ArgumentException){return HandoffStatus.InvalidResult;}
    catch(OverflowException){return HandoffStatus.InvalidResult;}
    finally{busy=false;}
   }
  }
  static bool Valid(HandoffInput input,HandoffSnapshot output)
  {
   if(output==null||output.Frame!=input.Snapshot.Frame||output.BaseEpoch!=input.Snapshot.BaseEpoch||output.Offset<input.Snapshot.Offset||output.Offset>input.TargetOffset||output.Bodies.Count!=input.Snapshot.Bodies.Count)return false;
   double dt=output.Offset-input.Snapshot.Offset;
   for(int i=0;i<output.Bodies.Count;i++)
   {
    var before=input.Snapshot.Bodies[i];var after=output.Bodies[i];var expected=before.Position+before.Velocity*dt;
    if(before.Id!=after.Id||before.Generation!=after.Generation||before.Mass!=after.Mass||before.Radius!=after.Radius||after.Position.X!=expected.X||after.Position.Y!=expected.Y||after.Position.Z!=expected.Z)return false;
   }
   return true;
  }
 }
}
