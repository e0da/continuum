using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ActiveVesselWriterCensus : IDisposable
    {
        long originGeneration;
        bool registered;
        public WriterCensus Census { get; private set; }

        public ActiveVesselWriterCensus()
        {
            Census = new WriterCensus(Capture);
            GameEvents.onFloatingOriginShift.Add(OnOriginShift); registered = true;
        }

        void OnOriginShift(Vector3d ignoredOffset, Vector3d ignoredNonFrame) { originGeneration++; }

        WriterCensusSnapshot Capture()
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.parts == null)
                throw new InvalidOperationException("Eligible active vessel unavailable.");
            var representatives = new Dictionary<Rigidbody, Part>();
            var topologyParts = new List<Part>(vessel.parts);
            topologyParts.Sort((a, b) => a == null ? (b == null ? 0 : -1) : b == null ? 1 : a.flightID.CompareTo(b.flightID));
            var topology = new string[topologyParts.Count];
            for (int i = 0; i < topologyParts.Count; i++)
            {
                Part part = topologyParts[i];
                Rigidbody body = Body(part);
                if (body == null) throw new InvalidOperationException("Part physical owner unavailable.");
                topology[i] = part.flightID.ToString(CultureInfo.InvariantCulture) + ":" +
                    body.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            }
            foreach (Part part in vessel.parts)
            {
                Rigidbody body = Body(part);
                if (body == null) throw new InvalidOperationException("Part physical owner unavailable.");
                Part existing;
                if (!representatives.TryGetValue(body, out existing) || part.flightID < existing.flightID) representatives[body] = part;
            }
            var parts = new List<Part>(representatives.Values);
            parts.Sort((a, b) => a.flightID.CompareTo(b.flightID));
            if (parts.Count == 0) throw new InvalidOperationException("No active-vessel rigidbodies.");
            Rigidbody referenceBody = Body(parts[0]);
            Vector3 reference = referenceBody.worldCenterOfMass;
            Quaternion inverseReferenceRotation = Quaternion.Inverse(referenceBody.rotation);
            Vector3 referenceVelocity = referenceBody.velocity;
            Vector3 referenceAngularVelocity = referenceBody.angularVelocity;
            Vector3d frame = Krakensbane.GetFrameVelocity();
            var bodies = new WriterCensusBody[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i]; Rigidbody body = Body(part);
                Vector3 position = body.worldCenterOfMass; Vector3 velocity = body.velocity;
                Vector3 angularVelocity = body.angularVelocity; Quaternion rotation = body.rotation;
                Vector3 internalPosition = inverseReferenceRotation * (position - reference);
                Vector3 internalVelocity = inverseReferenceRotation * (velocity - referenceVelocity);
                Vector3 internalAngularVelocity = inverseReferenceRotation * (angularVelocity - referenceAngularVelocity);
                Quaternion internalRotation = inverseReferenceRotation * rotation;
                bodies[i] = new WriterCensusBody {
                    id = part.flightID.ToString(CultureInfo.InvariantCulture) + ":" + body.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                    relativePosition = new Vec(position.x - reference.x, position.y - reference.y, position.z - reference.z),
                    normalizedVelocity = new Vec(velocity.x + frame.x, velocity.y + frame.y, velocity.z + frame.z),
                    orientation = new double[] { rotation.x, rotation.y, rotation.z, rotation.w },
                    angularVelocity = new Vec(angularVelocity.x, angularVelocity.y, angularVelocity.z),
                    internalPosition = new Vec(internalPosition.x, internalPosition.y, internalPosition.z),
                    internalVelocity = new Vec(internalVelocity.x, internalVelocity.y, internalVelocity.z),
                    internalAngularVelocity = new Vec(internalAngularVelocity.x, internalAngularVelocity.y, internalAngularVelocity.z),
                    internalOrientation = new double[] { internalRotation.x, internalRotation.y, internalRotation.z, internalRotation.w }
                };
            }
            return new WriterCensusSnapshot {
                vesselId = vessel.id.ToString("D"), originGeneration = originGeneration,
                frameVelocity = new Vec(frame.x, frame.y, frame.z), bodies = bodies,
                topologyKey = string.Join("|", topology)
            };
        }

        static Rigidbody Body(Part part)
        {
            if (part == null) return null;
            if (part.rb != null) return part.rb;
            return part.RigidBodyPart == null ? null : part.RigidBodyPart.rb;
        }

        public void Dispose()
        {
            if (registered) { GameEvents.onFloatingOriginShift.Remove(OnOriginShift); registered = false; }
            Census.Finish();
        }
    }
}
