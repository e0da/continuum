using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KspContinuum
{
    static class StructuralVesselCensusCapture
    {
        public static StructuralVesselCensusReport Capture(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded || vessel.parts == null)
                throw new InvalidOperationException("A loaded active vessel is required.");
            StructuralCensusResult census = StructuralCensusCapture.Capture(vessel);
            var parts = new List<StructuralPartObservation>();
            var attachments = new List<StructuralAttachmentObservation>();
            var logicalIdByPart = new Dictionary<Part, string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                string id = "part:" + part.flightID.ToString(CultureInfo.InvariantCulture);
                if (!seen.Add(id)) throw new InvalidOperationException("Active vessel contains duplicate persistent part IDs.");
                logicalIdByPart.Add(part, id);
                parts.Add(new StructuralPartObservation(id, part.rb == null ? (int?)null : part.rb.GetInstanceID(),
                    null, part.rb == null ? "missing-part-rigidbody" : null));
            }

            var jointsByBodyPair = JointIndex(vessel);
            foreach (Part child in vessel.parts)
            {
                if (child == null || child.parent == null) continue;
                string childId, parentId;
                if (!logicalIdByPart.TryGetValue(child, out childId) || !logicalIdByPart.TryGetValue(child.parent, out parentId))
                    continue;
                int? jointId = null; string omission = null;
                if (child.rb == null || child.parent.rb == null) omission = "attachment-endpoint-without-rigidbody";
                else
                {
                    string pair = Pair(child.rb.GetInstanceID(), child.parent.rb.GetInstanceID());
                    List<int> matches;
                    if (!jointsByBodyPair.TryGetValue(pair, out matches) || matches.Count == 0)
                        omission = child.rb == child.parent.rb ? "same-native-body-no-joint-required" : "no-native-joint-observed";
                    else if (matches.Count == 1) jointId = matches[0];
                    else omission = "ambiguous-native-joints:" + matches.Count.ToString(CultureInfo.InvariantCulture);
                }
                attachments.Add(new StructuralAttachmentObservation("parent:" + childId, parentId, childId,
                    jointId, null, omission));
            }
            double fixedTime = Time.fixedTime;
            double fixedDelta = Time.fixedDeltaTime;
            long physicsTick = fixedDelta > 0 ? (long)Math.Round(fixedTime / fixedDelta) : 0;
            var context = new StructuralVesselCaptureContext(vessel.id.ToString("D"),
                HighLogic.LoadedScene.ToString(), Time.frameCount, physicsTick, fixedTime,
                Planetarium.GetUniversalTime(), HighLogic.SaveFolder, null);
            return StructuralVesselCensus.Build(context, census, parts, attachments);
        }

        static Dictionary<string, List<int>> JointIndex(Vessel vessel)
        {
            var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var joints = new List<Joint>();
            var seen = new HashSet<int>();
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                Add(part.GetComponentsInChildren<Joint>(true), joints, seen);
            }
            Add(vessel.GetComponentsInChildren<Joint>(true), joints, seen);
            foreach (Joint joint in joints)
            {
                if (joint == null) continue;
                Rigidbody host = joint.GetComponent<Rigidbody>();
                Rigidbody connected = joint.connectedBody;
                if (host == null || connected == null || host == connected) continue;
                string pair = Pair(host.GetInstanceID(), connected.GetInstanceID());
                List<int> ids;
                if (!result.TryGetValue(pair, out ids)) { ids = new List<int>(); result.Add(pair, ids); }
                ids.Add(joint.GetInstanceID());
            }
            foreach (List<int> ids in result.Values) ids.Sort();
            return result;
        }

        static void Add(Joint[] source, List<Joint> target, HashSet<int> seen)
        {
            if (source == null) return;
            foreach (Joint joint in source)
                if (joint != null && seen.Add(joint.GetInstanceID())) target.Add(joint);
        }

        static string Pair(int a, int b)
        {
            return a < b ? a.ToString(CultureInfo.InvariantCulture) + ":" + b.ToString(CultureInfo.InvariantCulture)
                : b.ToString(CultureInfo.InvariantCulture) + ":" + a.ToString(CultureInfo.InvariantCulture);
        }
    }
}
