using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace KspContinuum
{
 public sealed class HandoffBody
 {
  public int Id {get;private set;} public long Generation {get;private set;}
  public double Mass {get;private set;} public double Radius {get;private set;}
  public Vec Position {get;private set;} public Vec Velocity {get;private set;}
  public HandoffBody(int id,long generation,double mass,double radius,Vec position,Vec velocity)
  {
   if(id<0||generation<0)throw new ArgumentException("Invalid identity.");
   AssemblyModel.Positive(mass);AssemblyModel.Positive(radius);
   AssemblyModel.Finite(position.X);AssemblyModel.Finite(position.Y);AssemblyModel.Finite(position.Z);
   AssemblyModel.Finite(velocity.X);AssemblyModel.Finite(velocity.Y);AssemblyModel.Finite(velocity.Z);
   Id=id;Generation=generation;Mass=mass;Radius=radius;Position=position;Velocity=velocity;
  }
 }
 public sealed class HandoffSnapshot
 {
  public string Frame {get;private set;} public double BaseEpoch {get;private set;} public double Offset {get;private set;}
  public IReadOnlyList<HandoffBody> Bodies {get;private set;}
  public HandoffSnapshot(string frame,double baseEpoch,double offset,IEnumerable<HandoffBody> bodies)
  {
   if(string.IsNullOrEmpty(frame)||frame.Length>256||bodies==null)throw new ArgumentException("Frame and bodies required.");
   AssemblyModel.Finite(baseEpoch);AssemblyModel.Finite(offset);if(offset<0)throw new ArgumentException("Negative offset.");
   var copy=new List<HandoffBody>();var ids=new HashSet<int>();
   foreach(var b in bodies){if(copy.Count==EncounterPlanner.MaximumBodies||b==null||!ids.Add(b.Id))throw new ArgumentException("Invalid membership.");copy.Add(b);}
   if(copy.Count==0)throw new ArgumentException("Empty snapshot.");
   Frame=frame;BaseEpoch=baseEpoch;Offset=offset;Bodies=new ReadOnlyCollection<HandoffBody>(copy);
  }
 }
 // SafeAdvanceSeconds is a possible-encounter lower bound measured from Snapshot, not a confirmed contact time.
 public sealed class HandoffInput
 {
  public HandoffSnapshot Snapshot {get;private set;} public double TargetOffset {get;private set;} public double SafeAdvanceSeconds {get;private set;}
  internal HandoffInput(HandoffSnapshot snapshot,double target,double safe){Snapshot=snapshot;TargetOffset=target;SafeAdvanceSeconds=safe;}
 }
 // Encounter times are relative to Snapshot; its epoch is zero. Physical time remains the split BaseEpoch/Offset.
 public sealed class HandoffPlan
 {
  public HandoffSnapshot Snapshot {get;private set;}
  public EncounterPlan Encounter {get;private set;}
  public long Revision {get {return Encounter.Generation;}}
  internal HandoffPlan(HandoffSnapshot snapshot,EncounterPlan encounter){Snapshot=snapshot;Encounter=encounter;}
 }
 public enum HandoffStatus { Committed, Rejected, Busy, Stale, Cancelled, Faulted, InvalidResult }
}
