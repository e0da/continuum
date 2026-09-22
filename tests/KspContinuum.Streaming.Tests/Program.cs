using System;
using System.Linq;
using KspContinuum;

static class Program
{
    static int assertions;
    static void Check(bool value,string reason) { assertions++;if(!value)throw new Exception("Streaming assertion "+assertions+": "+reason); }
    static SpatialKey K(params string[] path)=>new SpatialKey(path);
    static StreamRepresentation R(SpatialKey key,RepresentationFidelity fidelity,long memory=10,int work=1)=>new StreamRepresentation(key,fidelity,memory,work);
    static ViewpointInterest I(SpatialKey key,RepresentationFidelity fidelity,int importance=1)=>new ViewpointInterest(key,fidelity,importance);
    static string Ids(WorkingSetPlan plan)=>string.Join(",",plan.Resident.Select(x=>x.Identity));

    static void Main()
    {
        var kerbin=K("system","kerbin");var tile=K("system","kerbin","tile-12");var vessel=K("system","kerbin","vessel-7");
        var catalog=new[]{R(K("system"),RepresentationFidelity.Ephemeris),R(kerbin,RepresentationFidelity.Proxy),R(tile,RepresentationFidelity.Regional),R(vessel,RepresentationFidelity.Local)};
        var budget=new StreamingBudget(30,10);
        var first=WorldStreamingPlanner.Plan(catalog,new[]{I(tile,RepresentationFidelity.Regional)},Array.Empty<StreamRepresentation>(),budget);
        var permuted=WorldStreamingPlanner.Plan(catalog.Reverse().ToArray(),new[]{I(tile,RepresentationFidelity.Regional)},Array.Empty<StreamRepresentation>(),budget);
        Check(Ids(first)==Ids(permuted),"catalog order cannot change the working set");
        Check(string.Join(",",first.Decisions.Select(x=>x.Action+":"+x.Representation.Identity))==string.Join(",",permuted.Decisions.Select(x=>x.Action+":"+x.Representation.Identity)),"decision receipt order is stable");
        Check(first.Resident.Count==3&&first.Resident.Any(x=>x.Key.Equals(tile)),"hierarchy supplies system, planet, and regional representations");

        var a=R(K("system","kerbin","sector-a"),RepresentationFidelity.Regional,10,1);
        var b=R(K("system","kerbin","sector-b"),RepresentationFidelity.Regional,10,1);
        var tight=new StreamingBudget(10,10,retentionBonus:50);
        var resident=WorldStreamingPlanner.Plan(new[]{a,b},new[]{I(a.Key,RepresentationFidelity.Regional,100),I(b.Key,RepresentationFidelity.Regional,100)},new[]{a},tight);
        Check(resident.Resident.Single().Identity==a.Identity&&resident.Decisions.Single(x=>x.Action==WorkingSetAction.Retain).Representation.Identity==a.Identity,"retention wins a tied boundary without thrash");
        resident=WorldStreamingPlanner.Plan(new[]{a,b},new[]{I(a.Key,RepresentationFidelity.Regional,100),I(b.Key,RepresentationFidelity.Regional,101)},new[]{a},tight);
        Check(resident.Resident.Single().Identity==b.Identity,"materially higher interest crosses hysteresis");

        var mun=R(K("system","mun"),RepresentationFidelity.Proxy,10,2);
        var oldPlanet=WorldStreamingPlanner.Plan(new[]{catalog[1],mun,catalog[3]},new[]{I(kerbin,RepresentationFidelity.Proxy)},Array.Empty<StreamRepresentation>(),new StreamingBudget(10,2));
        var jump=WorldStreamingPlanner.Plan(new[]{catalog[1],mun,catalog[3]},new[]{I(vessel,RepresentationFidelity.Local)},oldPlanet.Resident,new StreamingBudget(10,2));
        Check(jump.Resident.Single().Identity==catalog[3].Identity,"planet-to-vessel jump admits destination immediately");
        Check(jump.Decisions.Any(x=>x.Action==WorkingSetAction.Evict&&x.Representation.Identity==catalog[1].Identity),"jump evicts irrelevant origin in the same plan");

        var bounded=WorldStreamingPlanner.Plan(catalog,new[]{I(tile,RepresentationFidelity.Regional),I(vessel,RepresentationFidelity.Local)},Array.Empty<StreamRepresentation>(),new StreamingBudget(20,1,3));
        Check(bounded.MemoryUsed<=20&&bounded.AdmissionWorkUsed<=1&&bounded.EvaluationWorkUsed<=3,"all reported resource use respects budgets");
        Check(bounded.EvaluationBudgetExhausted,"truncated evaluation is explicit");
        Check(bounded.Resident.Sum(x=>x.MemoryBytes)==bounded.MemoryUsed,"memory receipt matches selected representations");
        Check(new SpatialKey("system","kerbin").IsAncestorOf(new SpatialKey("system","kerbin","tile")),"hierarchical ancestry");
        bool rejected=false;try{new SpatialKey("system/kerbin");}catch(ArgumentException){rejected=true;}Check(rejected,"ambiguous key segment rejected");
        Console.WriteLine("Streaming: "+assertions+" assertions passed.");
    }
}
