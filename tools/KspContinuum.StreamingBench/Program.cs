using System;
using System.Collections.Generic;
using System.Diagnostics;
using KspContinuum;

var catalog=new List<StreamRepresentation>();
for(int body=0;body<64;body++)
for(int tile=0;tile<64;tile++)
    catalog.Add(new StreamRepresentation(new SpatialKey("system","body-"+body,"tile-"+tile),RepresentationFidelity.Regional,65536,4));
var budget=new StreamingBudget(64*65536,256,8192);
IReadOnlyList<StreamRepresentation> resident=Array.Empty<StreamRepresentation>();
var stopwatch=Stopwatch.StartNew();
const int plans=1000;
for(int i=0;i<plans;i++)
{
    var target=new SpatialKey("system","body-"+(i%64),"tile-"+((i*17)%64));
    var plan=WorldStreamingPlanner.Plan(catalog,new[]{new ViewpointInterest(target,RepresentationFidelity.Regional)},resident,budget);
    resident=plan.Resident;
}
stopwatch.Stop();
Console.WriteLine("Streaming bench: "+plans+" plans over "+catalog.Count+" representations in "+stopwatch.Elapsed.TotalMilliseconds.ToString("F1")+" ms; final residents "+resident.Count+".");
