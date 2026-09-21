using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace KspContinuum
{
    public static class EncounterPlanner
    {
        public const int MaximumBodies=4096;
        sealed class BudgetStop:Exception {}
        sealed class Envelope
        {
            internal EncounterMotion Motion;
            internal EncounterInterval X,Y,Z;
            internal EncounterInterval Axis(int axis) {return axis==0?X:axis==1?Y:Z;}
        }
        struct Window
        {
            internal double Lo,Hi;
            internal Window(double lo,double hi) { Lo=lo;Hi=hi; }
        }
        public static EncounterPlan Plan(IReadOnlyList<EncounterMotion> motions,double requestedSeconds,EncounterBudget budget)
        { return Compute(Snapshot(motions),requestedSeconds,budget,Guid.Empty,0); }

        internal static EncounterMotion[] Snapshot(IReadOnlyList<EncounterMotion> motions)
        {
            if(motions==null||motions.Count<1||motions.Count>MaximumBodies) throw new ArgumentException("Supply 1 through 4096 motions.");
            var copy=new EncounterMotion[motions.Count]; var ids=new HashSet<int>();
            for(int i=0;i<copy.Length;i++)
            {
                var motion=motions[i];
                if(motion==null||!ids.Add(motion.Id)) throw new ArgumentException("Motion IDs must be present and unique.");
                if(i>0&&(motion.Epoch!=copy[0].Epoch||motion.Frame!=copy[0].Frame)) throw new ArgumentException("Common epoch and frame required.");
                copy[i]=motion;
            }
            Array.Sort(copy,(a,b)=>a.Id.CompareTo(b.Id)); return copy;
        }
        internal static EncounterPlan Compute(EncounterMotion[] motions,double requested,EncounterBudget budget,Guid scheduler,long generation)
        {
            AssemblyModel.Finite(requested);
            if(requested<0||budget==null) throw new ArgumentException("Nonnegative requested horizon and a budget are required.");
            double horizon=requested;
            bool known=true;
            foreach(var motion in motions) { horizon=Math.Min(horizon,motion.ValidForSeconds); known &= motion.AccelerationBound.HasValue; }
            var candidates=new List<EncounterCandidate>();
            var work=new EncounterWork();
            var status=known?EncounterPlanStatus.Complete:EncounterPlanStatus.UnknownBounds;
            string detail=known?"Screening complete; retained brackets are possible interactions, not confirmed impacts.":"Unknown acceleration bound prevents whole-scene advance.";
            if(known)
            {
                try
                {
                    var entries=new Envelope[motions.Length];
                    for(int i=0;i<motions.Length;i++) entries[i]=Sweep(motions[i],horizon);
                    int axis=ChooseAxis(entries); work.SweepAxis=axis;
                    Array.Sort(entries,(a,b)=> {int order=a.Axis(axis).Lo.CompareTo(b.Axis(axis).Lo);return order==0?a.Motion.Id.CompareTo(b.Motion.Id):order;});
                    var active=new List<Envelope>();
                    foreach(var entry in entries)
                    {
                        for(int i=active.Count-1;i>=0;i--) if(active[i].Axis(axis).Hi<entry.Axis(axis).Lo) active.RemoveAt(i);
                        foreach(var other in active)
                        {
                            if(work.PairTests>=budget.MaxPairTests) throw new BudgetStop(); work.PairTests++;
                            if(other.X.Hi<entry.X.Lo||entry.X.Hi<other.X.Lo||other.Y.Hi<entry.Y.Lo||entry.Y.Hi<other.Y.Lo||other.Z.Hi<entry.Z.Lo||entry.Z.Hi<other.Z.Lo) continue;
                            if(work.BroadphaseCandidates>=budget.MaxCandidates) throw new BudgetStop(); work.BroadphaseCandidates++;
                            var first=other.Motion.Id<entry.Motion.Id?other.Motion:entry.Motion;
                            var second=other.Motion.Id<entry.Motion.Id?entry.Motion:other.Motion;
                            EncounterCandidate candidate=Localize(first,second,horizon,budget,work);
                            if(candidate!=null) candidates.Add(candidate);
                        }
                        active.Add(entry);
                    }
                }
                catch(BudgetStop) { status=EncounterPlanStatus.BudgetExhausted;detail="Incomplete screening/localization; all independent advance withheld. Retained candidate prefix is incomplete."; }
                catch(ArithmeticException) { status=EncounterPlanStatus.NumericalUncertainty;detail="Arithmetic range or requested time resolution is insufficient; all independent advance withheld."; }
            }
            candidates.Sort((a,b)=> {
                int order=a.LowerSeconds.CompareTo(b.LowerSeconds);if(order!=0)return order;
                order=a.UpperSeconds.CompareTo(b.UpperSeconds);if(order!=0)return order;
                order=a.FirstId.CompareTo(b.FirstId);return order!=0?order:a.SecondId.CompareTo(b.SecondId);
            });
            var plan=new EncounterPlan { Status=status,Detail=detail,SchedulerId=scheduler,Generation=generation,
                Epoch=motions[0].Epoch,Frame=motions[0].Frame,HorizonSeconds=horizon,
                Candidates=new ReadOnlyCollection<EncounterCandidate>(candidates),WorkUsed=work };
            BuildAdvances(plan,motions,candidates); return plan;
        }

        static int ChooseAxis(Envelope[] entries)
        {
            int selected=0;double largest=-1;
            for(int axis=0;axis<3;axis++)
            {
                double lo=entries[0].Axis(axis).Lo,hi=entries[0].Axis(axis).Hi;
                foreach(var entry in entries) {lo=Math.Min(lo,entry.Axis(axis).Lo);hi=Math.Max(hi,entry.Axis(axis).Hi);}
                double spread=hi*.5-lo*.5;
                if(spread>largest) {largest=spread;selected=axis;}
            }
            return selected;
        }

        static EncounterInterval Radius(EncounterMotion motion,double upperTime)
        {
            var time=EncounterInterval.Point(upperTime);
            var radius=EncounterInterval.Add(EncounterInterval.Point(motion.Radius),EncounterInterval.Point(motion.PositionError));
            radius=EncounterInterval.Add(radius,EncounterInterval.Multiply(EncounterInterval.Point(motion.VelocityError),time));
            var quadratic=EncounterInterval.Multiply(EncounterInterval.Multiply(EncounterInterval.Point(.5),EncounterInterval.Point(motion.AccelerationBound.Value)),EncounterInterval.Square(time));
            return EncounterInterval.Add(radius,quadratic);
        }
        static EncounterInterval Axis(double p,double v,double acceleration,double horizon,double inflation)
        {
            var center=EncounterInterval.Add(EncounterInterval.Point(p),EncounterInterval.Multiply(EncounterInterval.Point(v),new EncounterInterval(0,horizon)));
            if(acceleration!=0)
                center=EncounterInterval.Add(center,EncounterInterval.Multiply(
                    EncounterInterval.Multiply(EncounterInterval.Point(.5),EncounterInterval.Point(acceleration)),
                    EncounterInterval.Square(new EncounterInterval(0,horizon))));
            return new EncounterInterval(EncounterInterval.Down(center.Lo-inflation),EncounterInterval.Up(center.Hi+inflation));
        }
        static Envelope Sweep(EncounterMotion motion,double horizon)
        {
            double radius=Radius(motion,horizon).Hi;
            return new Envelope {Motion=motion,X=Axis(motion.Position.X,motion.Velocity.X,motion.NominalAcceleration.X,horizon,radius),
                Y=Axis(motion.Position.Y,motion.Velocity.Y,motion.NominalAcceleration.Y,horizon,radius),Z=Axis(motion.Position.Z,motion.Velocity.Z,motion.NominalAcceleration.Z,horizon,radius)};
        }
        static EncounterInterval RelativeAxis(double pa,double pb,double va,double vb,double aa,double ab,Window window)
        {
            var relative=EncounterInterval.Add(EncounterInterval.Subtract(EncounterInterval.Point(pa),EncounterInterval.Point(pb)),
                EncounterInterval.Multiply(EncounterInterval.Subtract(EncounterInterval.Point(va),EncounterInterval.Point(vb)),new EncounterInterval(window.Lo,window.Hi)));
            if(aa==ab) return relative;
            var acceleration=EncounterInterval.Subtract(EncounterInterval.Point(aa),EncounterInterval.Point(ab));
            return EncounterInterval.Add(relative,EncounterInterval.Multiply(
                EncounterInterval.Multiply(EncounterInterval.Point(.5),acceleration),
                EncounterInterval.Square(new EncounterInterval(window.Lo,window.Hi))));
        }
        static bool Excluded(EncounterMotion a,EncounterMotion b,Window window)
        {
            var x=RelativeAxis(a.Position.X,b.Position.X,a.Velocity.X,b.Velocity.X,a.NominalAcceleration.X,b.NominalAcceleration.X,window);
            var y=RelativeAxis(a.Position.Y,b.Position.Y,a.Velocity.Y,b.Velocity.Y,a.NominalAcceleration.Y,b.NominalAcceleration.Y,window);
            var z=RelativeAxis(a.Position.Z,b.Position.Z,a.Velocity.Z,b.Velocity.Z,a.NominalAcceleration.Z,b.NominalAcceleration.Z,window);
            var distance2=EncounterInterval.Add(EncounterInterval.Add(EncounterInterval.Square(x),EncounterInterval.Square(y)),EncounterInterval.Square(z));
            var radius=EncounterInterval.Add(Radius(a,window.Hi),Radius(b,window.Hi));
            return distance2.Lo>EncounterInterval.Square(radius).Hi;
        }
        static EncounterCandidate Localize(EncounterMotion a,EncounterMotion b,double horizon,EncounterBudget budget,EncounterWork work)
        {
            var pending=new Stack<Window>(); pending.Push(new Window(0,horizon));
            while(pending.Count!=0)
            {
                if(work.IntervalTests>=budget.MaxIntervalTests) throw new BudgetStop(); work.IntervalTests++;
                Window window=pending.Pop();
                if(Excluded(a,b,window)) continue;
                double middle=window.Lo+(window.Hi-window.Lo)*.5;
                if(window.Hi-window.Lo<=budget.TimeToleranceSeconds)
                    return new EncounterCandidate { FirstId=a.Id,SecondId=b.Id,FirstGeneration=a.Generation,SecondGeneration=b.Generation,
                        LowerSeconds=window.Lo,UpperSeconds=window.Hi };
                if(middle==window.Lo||middle==window.Hi) throw new ArithmeticException("Requested time resolution cannot be represented.");
                pending.Push(new Window(middle,window.Hi)); pending.Push(new Window(window.Lo,middle));
            }
            return null;
        }
        static int Root(int[] parents,int index)
        {
            while(parents[index]!=index) { parents[index]=parents[parents[index]];index=parents[index]; } return index;
        }
        static void BuildAdvances(EncounterPlan plan,EncounterMotion[] motions,List<EncounterCandidate> candidates)
        {
            var index=new Dictionary<int,int>();var parents=new int[motions.Length];var safe=new double[motions.Length];
            bool complete=plan.Status==EncounterPlanStatus.Complete;
            for(int i=0;i<motions.Length;i++) { index.Add(motions[i].Id,i); parents[i]=complete?i:0; safe[i]=complete?Math.Min(plan.HorizonSeconds,motions[i].LookaheadSeconds):0; }
            foreach(var candidate in candidates)
            {
                int a=index[candidate.FirstId],b=index[candidate.SecondId];
                int ra=Root(parents,a),rb=Root(parents,b); parents[Math.Max(ra,rb)]=Math.Min(ra,rb);
                safe[a]=Math.Min(safe[a],candidate.LowerSeconds);safe[b]=Math.Min(safe[b],candidate.LowerSeconds);
            }
            var members=new SortedDictionary<int,List<int>>();var limits=new Dictionary<int,double>();
            for(int i=0;i<motions.Length;i++)
            {
                int group=motions[Root(parents,i)].Id;
                if(!members.ContainsKey(group)) {members.Add(group,new List<int>());limits.Add(group,safe[i]);}
                members[group].Add(motions[i].Id);limits[group]=Math.Min(limits[group],safe[i]);
            }
            var groups=new List<EncounterGroup>();var advances=new List<EncounterAdvance>();
            foreach(var entry in members) groups.Add(new EncounterGroup {Id=entry.Key,ObjectIds=new ReadOnlyCollection<int>(entry.Value),SafeAdvanceSeconds=limits[entry.Key]});
            foreach(var motion in motions)
            {
                int group=motions[Root(parents,index[motion.Id])].Id;
                advances.Add(new EncounterAdvance {ObjectId=motion.Id,GroupId=group,SafeAdvanceSeconds=limits[group]});
            }
            plan.Groups=new ReadOnlyCollection<EncounterGroup>(groups);plan.Advances=new ReadOnlyCollection<EncounterAdvance>(advances);
        }
    }
    public sealed class EncounterScheduler
    {
        readonly object sync=new object();
        readonly Guid id=Guid.NewGuid();
        EncounterMotion[] motions;
        long generation;
        public long Generation {get {lock(sync)return generation;}}
        public void Replace(IReadOnlyList<EncounterMotion> motions)
        {
            var snapshot=EncounterPlanner.Snapshot(motions);
            lock(sync) {long next=checked(generation+1);this.motions=snapshot;generation=next;}
        }
        public EncounterPlan Plan(double requestedSeconds,EncounterBudget budget)
        {
            EncounterMotion[] snapshot;long captured;
            lock(sync) {snapshot=motions;captured=generation;}
            if(snapshot==null)throw new InvalidOperationException("Publish a motion snapshot before planning.");
            return EncounterPlanner.Compute(snapshot,requestedSeconds,budget,id,captured);
        }
        public bool IsCurrent(EncounterPlan plan)
        {lock(sync)return plan!=null&&plan.SchedulerId==id&&plan.Generation==generation;}
    }
}
