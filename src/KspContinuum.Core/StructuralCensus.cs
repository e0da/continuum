using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class StructuralCensusAccumulator
    {
        readonly HashSet<int> bodies = new HashSet<int>();
        readonly HashSet<int> joints = new HashSet<int>();
        readonly HashSet<int> colliders = new HashSet<int>();

        public void AddBody(int nativeInstanceId) { Add(bodies, nativeInstanceId); }
        public void AddJoint(int nativeInstanceId) { Add(joints, nativeInstanceId); }
        public void AddCollider(int nativeInstanceId) { Add(colliders, nativeInstanceId); }

        public StructuralCensusResult Snapshot()
        {
            return new StructuralCensusResult(bodies.Count, joints.Count, colliders.Count);
        }

        static void Add(HashSet<int> identities, int nativeInstanceId)
        {
            if (nativeInstanceId == 0) throw new ArgumentException("A native structural identity cannot be zero.");
            identities.Add(nativeInstanceId);
        }
    }

    public sealed class StructuralCensusResult
    {
        public readonly int rigidbodies, joints, colliders;
        public StructuralCensusResult(int rigidbodies, int joints, int colliders)
        {
            if (rigidbodies < 0 || joints < 0 || colliders < 0) throw new ArgumentException("Structural counts cannot be negative.");
            this.rigidbodies = rigidbodies;
            this.joints = joints;
            this.colliders = colliders;
        }
    }
}
