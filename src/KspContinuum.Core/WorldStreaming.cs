using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum RepresentationFidelity { Ephemeris, Proxy, Regional, Local }
    public enum WorkingSetAction { Admit, Retain, Evict }

    public sealed class SpatialKey : IComparable<SpatialKey>, IEquatable<SpatialKey>
    {
        readonly string[] segments;
        public int Depth { get { return segments.Length; } }
        public string Value { get; private set; }

        public SpatialKey(params string[] segments)
        {
            if(segments==null||segments.Length<1||segments.Length>32) throw new ArgumentException("Spatial keys require 1 through 32 segments.");
            this.segments=new string[segments.Length];
            for(int i=0;i<segments.Length;i++)
            {
                string segment=segments[i];
                if(string.IsNullOrWhiteSpace(segment)||segment.Length>128||segment.IndexOf('/')>=0) throw new ArgumentException("Spatial key segments must be nonempty and cannot contain '/'.");
                this.segments[i]=segment;
            }
            Value=string.Join("/",this.segments);
        }

        public bool IsAncestorOf(SpatialKey other)
        {
            if(other==null||Depth>other.Depth)return false;
            for(int i=0;i<Depth;i++)if(!string.Equals(segments[i],other.segments[i],StringComparison.Ordinal))return false;
            return true;
        }
        public int CompareTo(SpatialKey other) { return other==null?1:string.CompareOrdinal(Value,other.Value); }
        public bool Equals(SpatialKey other) { return other!=null&&string.Equals(Value,other.Value,StringComparison.Ordinal); }
        public override bool Equals(object obj) { return Equals(obj as SpatialKey); }
        public override int GetHashCode() { return StringComparer.Ordinal.GetHashCode(Value); }
        public override string ToString() { return Value; }
    }

    public sealed class StreamRepresentation
    {
        public SpatialKey Key { get; private set; }
        public RepresentationFidelity Fidelity { get; private set; }
        public long MemoryBytes { get; private set; }
        public int AdmissionWork { get; private set; }
        public string Identity { get { return Key.Value+":"+(int)Fidelity; } }
        public StreamRepresentation(SpatialKey key,RepresentationFidelity fidelity,long memoryBytes,int admissionWork)
        {
            if(key==null||!Enum.IsDefined(typeof(RepresentationFidelity),fidelity)||memoryBytes<1||admissionWork<0)throw new ArgumentException("A representation requires a key, valid fidelity, positive memory, and nonnegative admission work.");
            Key=key;Fidelity=fidelity;MemoryBytes=memoryBytes;AdmissionWork=admissionWork;
        }
    }

    public sealed class ViewpointInterest
    {
        public SpatialKey Target { get; private set; }
        public RepresentationFidelity DesiredFidelity { get; private set; }
        public int Importance { get; private set; }
        public ViewpointInterest(SpatialKey target,RepresentationFidelity desiredFidelity,int importance=1)
        {
            if(target==null||!Enum.IsDefined(typeof(RepresentationFidelity),desiredFidelity)||importance<1||importance>1000000)throw new ArgumentException("Interest requires a target, valid fidelity, and bounded positive importance.");
            Target=target;DesiredFidelity=desiredFidelity;Importance=importance;
        }
    }

    public sealed class StreamingBudget
    {
        public long MemoryBytes { get; private set; }
        public int AdmissionWork { get; private set; }
        public int EvaluationWork { get; private set; }
        public int RetentionBonus { get; private set; }
        public StreamingBudget(long memoryBytes,int admissionWork,int evaluationWork=100000,int retentionBonus=50)
        {
            if(memoryBytes<0||admissionWork<0||evaluationWork<0||retentionBonus<0||retentionBonus>999)
                throw new ArgumentException("Streaming budgets and retention bonus must be within supported bounds.");
            MemoryBytes=memoryBytes;AdmissionWork=admissionWork;EvaluationWork=evaluationWork;RetentionBonus=retentionBonus;
        }
    }

    public sealed class WorkingSetDecision
    {
        public StreamRepresentation Representation { get; internal set; }
        public WorkingSetAction Action { get; internal set; }
        public long Score { get; internal set; }
    }

    public sealed class WorkingSetPlan
    {
        public IReadOnlyList<StreamRepresentation> Resident { get; internal set; }
        public IReadOnlyList<WorkingSetDecision> Decisions { get; internal set; }
        public long MemoryUsed { get; internal set; }
        public int AdmissionWorkUsed { get; internal set; }
        public int EvaluationWorkUsed { get; internal set; }
        public bool EvaluationBudgetExhausted { get; internal set; }
    }

    public static class WorldStreamingPlanner
    {
        public const int MaximumCatalogItems=65536;
        public const int MaximumInterests=4096;
        sealed class Ranked
        {
            internal StreamRepresentation Item;
            internal long Score;
            internal bool WasResident;
        }

        public static WorkingSetPlan Plan(IReadOnlyList<StreamRepresentation> catalog,IReadOnlyList<ViewpointInterest> interests,
            IReadOnlyList<StreamRepresentation> previousResident,StreamingBudget budget)
        {
            if(catalog==null||interests==null||previousResident==null||budget==null)throw new ArgumentException("Catalog, interests, prior working set, and budget are required.");
            if(catalog.Count>MaximumCatalogItems||interests.Count>MaximumInterests||previousResident.Count>MaximumCatalogItems)throw new ArgumentException("Streaming input exceeds supported bounds.");
            for(int i=0;i<interests.Count;i++)if(interests[i]==null)throw new ArgumentException("Interests cannot contain null.");
            var byIdentity=new SortedDictionary<string,StreamRepresentation>(StringComparer.Ordinal);
            for(int i=0;i<catalog.Count;i++)
            {
                var item=catalog[i];
                if(item==null||byIdentity.ContainsKey(item.Identity))throw new ArgumentException("Catalog identities must be present and unique.");
                byIdentity.Add(item.Identity,item);
            }
            var prior=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<previousResident.Count;i++)
            {
                var item=previousResident[i];
                if(item==null||!byIdentity.ContainsKey(item.Identity)||!prior.Add(item.Identity))throw new ArgumentException("Prior residents must be unique catalog members.");
            }
            var ranked=new List<Ranked>();int evaluations=0;bool exhausted=false;
            foreach(var entry in byIdentity)
            {
                long best=0;bool complete=true;
                for(int i=0;i<interests.Count;i++)
                {
                    if(evaluations==budget.EvaluationWork){complete=false;exhausted=true;break;}
                    evaluations++;
                    var interest=interests[i];
                    if(!entry.Value.Key.IsAncestorOf(interest.Target)||entry.Value.Fidelity>interest.DesiredFidelity)continue;
                    long score=checked((long)interest.Importance*1000000L+entry.Value.Key.Depth*1000L+(int)entry.Value.Fidelity);
                    if(score>best)best=score;
                }
                if(complete&&best>0)
                {
                    bool retained=prior.Contains(entry.Key);
                    ranked.Add(new Ranked {Item=entry.Value,Score=checked(best+(retained?budget.RetentionBonus:0)),WasResident=retained});
                }
                if(exhausted)break;
            }
            ranked.Sort((a,b)=> {int order=b.Score.CompareTo(a.Score);return order!=0?order:string.CompareOrdinal(a.Item.Identity,b.Item.Identity);});
            long memory=0;int admission=0;var selected=new List<StreamRepresentation>();var selectedIds=new HashSet<string>(StringComparer.Ordinal);
            foreach(var candidate in ranked)
            {
                int work=candidate.WasResident?0:candidate.Item.AdmissionWork;
                if(candidate.Item.MemoryBytes>budget.MemoryBytes-memory||work>budget.AdmissionWork-admission)continue;
                memory+=candidate.Item.MemoryBytes;admission+=work;selected.Add(candidate.Item);selectedIds.Add(candidate.Item.Identity);
            }
            selected.Sort((a,b)=>string.CompareOrdinal(a.Identity,b.Identity));
            var decisions=new List<WorkingSetDecision>();
            foreach(var item in previousResident)if(!selectedIds.Contains(item.Identity))decisions.Add(new WorkingSetDecision {Representation=item,Action=WorkingSetAction.Evict,Score=0});
            foreach(var candidate in ranked)if(selectedIds.Contains(candidate.Item.Identity))decisions.Add(new WorkingSetDecision {Representation=candidate.Item,Action=candidate.WasResident?WorkingSetAction.Retain:WorkingSetAction.Admit,Score=candidate.Score});
            decisions.Sort((a,b)=> {int order=a.Action.CompareTo(b.Action);return order!=0?order:string.CompareOrdinal(a.Representation.Identity,b.Representation.Identity);});
            return new WorkingSetPlan {Resident=new ReadOnlyCollection<StreamRepresentation>(selected),Decisions=new ReadOnlyCollection<WorkingSetDecision>(decisions),
                MemoryUsed=memory,AdmissionWorkUsed=admission,EvaluationWorkUsed=evaluations,EvaluationBudgetExhausted=exhausted};
        }
    }
}
