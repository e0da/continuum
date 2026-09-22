using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public static class Inspector
    {
        static readonly HashSet<string> Structural = new HashSet<string> { "structuralPanel1", "structuralPanel2", "structuralIBeam1", "structuralIBeam2", "structuralIBeam3", "strutCube", "strutOcto" };
        public static VesselReport Capture(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) throw new InvalidOperationException("A loaded vessel is required");
            var report = new VesselReport { parts = vessel.parts.Count, packed = vessel.packed };
            var rows = new List<PartReport>();
            foreach (var part in vessel.parts)
            {
                string name = part.partInfo == null ? "unknown" : part.partInfo.name;
                string exclusion = !Structural.Contains(name) ? "outside-structural-allowlist" :
                    vessel.packed ? "packed-vessel" : part.parent == null ? "root" :
                    part.rb == null ? "no-independent-body" : part.Resources.Count > 0 ? "resources" : "";
                if (exclusion.Length == 0)
                    foreach (PartModule module in part.Modules)
                        if (module.moduleName != "ModuleCargoPart") { exclusion = "module:" + module.moduleName; break; }
                if (exclusion.Length == 0) report.candidateParts++;
                rows.Add(new PartReport { partType = name, index = vessel.parts.IndexOf(part),
                    parentIndex = part.parent == null ? -1 : vessel.parts.IndexOf(part.parent), dryMass = part.mass, exclusion = exclusion });
            }
            StructuralCensusResult census = StructuralCensusCapture.Capture(vessel);
            report.rigidbodies = census.rigidbodies; report.joints = census.joints; report.colliders = census.colliders;
            report.inventory = rows.ToArray(); return report;
        }
    }
}
