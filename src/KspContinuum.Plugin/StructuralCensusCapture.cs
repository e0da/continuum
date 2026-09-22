using UnityEngine;

namespace KspContinuum
{
    static class StructuralCensusCapture
    {
        public static StructuralCensusResult Capture(Vessel vessel)
        {
            var census = new StructuralCensusAccumulator();
            if (vessel.parts != null)
                foreach (Part part in vessel.parts)
                {
                    if (part == null) continue;
                    Add(part.rb, census.AddBody);
                    Add(part.GetComponentsInChildren<Rigidbody>(true), census.AddBody);
                    Add(part.GetComponentsInChildren<Joint>(true), census.AddJoint);
                    Add(part.GetComponentsInChildren<Collider>(true), census.AddCollider);
                }

            // KSP's Part ownership graph is not guaranteed to be the Vessel component's
            // transform subtree. Keep the root walk only to include vessel-owned helpers
            // which are not below a Part; native instance IDs deduplicate both views.
            Add(vessel.GetComponentsInChildren<Rigidbody>(true), census.AddBody);
            Add(vessel.GetComponentsInChildren<Joint>(true), census.AddJoint);
            Add(vessel.GetComponentsInChildren<Collider>(true), census.AddCollider);
            return census.Snapshot();
        }

        static void Add<T>(T component, System.Action<int> add) where T : Component
        {
            if (component != null) add(component.GetInstanceID());
        }

        static void Add<T>(T[] components, System.Action<int> add) where T : Component
        {
            if (components == null) return;
            foreach (T component in components) Add(component, add);
        }
    }
}
