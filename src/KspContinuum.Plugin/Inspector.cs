using System;
using System.Collections.Generic;
using UnityEngine;

namespace KspContinuum
{
    [Serializable] public sealed class VesselReport
    {
        public string schema = "ksp-continuum-vessel/v1";
        public string status = "read-only-candidate-inventory-not-merge-approval";
        public string utc = DateTime.UtcNow.ToString("o");
        public int parts, rigidbodies, joints, colliders, candidateParts;
        public bool packed;
        public PartReport[] inventory;
    }
    [Serializable] public sealed class PartReport
    {
        public string partType, exclusion;
        public int index, parentIndex;
        public float dryMass;
    }
    public static class Inspector
    {
        static readonly HashSet<string> Structural = new HashSet<string> { "structuralPanel1", "structuralPanel2", "structuralIBeam1", "structuralIBeam2", "structuralIBeam3", "strutCube", "strutOcto" };
        public static VesselReport Capture(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded) throw new InvalidOperationException("A loaded vessel is required");
            var report = new VesselReport { parts = vessel.parts.Count, packed = vessel.packed };
            var rows = new List<PartReport>();
            var bodies = new HashSet<Rigidbody>(); var joints = new HashSet<Joint>(); var colliders = new HashSet<Collider>();
            foreach (var part in vessel.parts)
            {
                foreach (var rb in part.GetComponentsInChildren<Rigidbody>(true)) bodies.Add(rb);
                foreach (var joint in part.GetComponentsInChildren<Joint>(true)) joints.Add(joint);
                foreach (var collider in part.GetComponentsInChildren<Collider>(true)) colliders.Add(collider);
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
            report.rigidbodies = bodies.Count; report.joints = joints.Count; report.colliders = colliders.Count;
            report.inventory = rows.ToArray(); return report;
        }
    }
}
