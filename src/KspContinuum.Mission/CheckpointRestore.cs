using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MuMech;

namespace KspContinuum.Mission
{
    // A native reconstructed-vessel witness, not a serialized controller continuation.
    sealed class CheckpointRestore
    {
        sealed class Resource
        {
            public string Name;
            public double Amount, Maximum;
            public bool Flow;
        }
        sealed class SavedPart
        {
            public uint Id, Parent;
            public string Name;
            public int Stage;
            public string[] Modules;
            public Resource[] Resources;
        }
        readonly SavedPart[] parts;
        readonly Guid vesselId;
        readonly uint rootId, referenceId;
        readonly OrbitSnapshot orbit;
        readonly UnityEngine.Quaternion sourceRotation;
        public readonly double Epoch;

        public CheckpointRestore(Game game, double sourceUT)
        {
            if (game == null || !game.compatible || game.Mode != Game.Modes.SANDBOX || game.flightState == null ||
                !SurveyPolicy.Finite(sourceUT) || !SurveyPolicy.Finite(game.flightState.universalTime) || Math.Abs(game.flightState.universalTime - sourceUT) > 0.01 ||
                game.flightState.activeVesselIdx < 0 || game.flightState.activeVesselIdx >= game.flightState.protoVessels.Count)
                throw new InvalidOperationException("Checkpoint is not a compatible sandbox flight at its recorded epoch.");
            ProtoVessel saved = game.flightState.protoVessels[game.flightState.activeVesselIdx];
            if (saved.situation != Vessel.Situations.ORBITING || saved.stage != 2 || saved.protoPartSnapshots.Count != 17 ||
                saved.rootIndex < 0 || saved.rootIndex >= saved.protoPartSnapshots.Count || saved.GroupOverride != 0)
                throw new InvalidOperationException("Checkpoint is not the supported 17-part stage-2 orbital lander.");
            if (saved.protoPartSnapshots.Any(p => p.partInfo == null) || saved.protoPartSnapshots.Select(p => p.flightID).Distinct().Count() != 17 ||
                saved.protoPartSnapshots.Count(p => p.flightID == saved.refTransform) != 1)
                throw new InvalidOperationException("Checkpoint part definitions or reference identity are invalid.");
            for (int i = 0; i < saved.protoPartSnapshots.Count; i++)
            {
                int cursor = i;
                var visited = new HashSet<int>();
                while (cursor != saved.rootIndex)
                {
                    if (cursor < 0 || cursor >= 17 || !visited.Add(cursor)) throw new InvalidOperationException("Checkpoint part tree is invalid.");
                    cursor = saved.protoPartSnapshots[cursor].parentIdx;
                }
            }
            if (saved.ctrlState == null || ReadNumber(saved.ctrlState, "mainThrottle") != 0 ||
                ReadNumber(saved.ctrlState, "trimPitch") != 0 || ReadNumber(saved.ctrlState, "trimYaw") != 0 || ReadNumber(saved.ctrlState, "trimRoll") != 0 ||
                saved.actionGroups == null || !saved.actionGroups.GetValue("SAS").StartsWith("False,", StringComparison.Ordinal) ||
                !saved.actionGroups.GetValue("RCS").StartsWith("False,", StringComparison.Ordinal) || saved.flightPlan == null || saved.flightPlan.nodes.Count != 0)
                throw new InvalidOperationException("Checkpoint saved controls are not idle.");
            var mj = saved.protoPartSnapshots.SelectMany(p => p.modules).Where(m => m.moduleName == "MechJebCore").ToArray();
            if (mj.Length != 1) throw new InvalidOperationException("Checkpoint requires one MechJeb owner.");
            ConfigNode local = mj[0].moduleValues.GetNode("MechJebLocalSettings");
            if (local == null || local.GetNode("MechJebModuleSmartASS").GetValue("target") != "OFF")
                throw new InvalidOperationException("Checkpoint saved SmartASS owner is not idle.");
            ConfigNode airplane = local.GetNode("MechJebModuleAirplaneAutopilot");
            ConfigNode rover = local.GetNode("MechJebModuleRoverController");
            foreach (string key in new[] { "HeadingHoldEnabled", "AltitudeHoldEnabled", "VertSpeedHoldEnabled", "RollHoldEnabled", "SpeedHoldEnabled" }) RequireFalse(airplane, key);
            foreach (string key in new[] { "ControlHeading", "ControlSpeed", "WarpToDaylight", "StabilityControl" }) RequireFalse(rover, key);
            vesselId = saved.vesselID;
            sourceRotation = saved.rotation;
            rootId = saved.protoPartSnapshots[saved.rootIndex].flightID;
            referenceId = saved.refTransform;
            OrbitSnapshot o = saved.orbitSnapShot;
            if (new[] { o.semiMajorAxis, o.eccentricity, o.inclination, o.argOfPeriapsis, o.LAN, o.meanAnomalyAtEpoch, o.epoch }.Any(value => !SurveyPolicy.Finite(value)) ||
                o.ReferenceBodyIndex < 0 || o.ReferenceBodyIndex >= FlightGlobals.Bodies.Count || FlightGlobals.Bodies[o.ReferenceBodyIndex].bodyName != "Minmus" ||
                o.eccentricity < 0 || o.eccentricity >= 1 || o.semiMajorAxis * (1 - o.eccentricity) - FlightGlobals.Bodies[o.ReferenceBodyIndex].Radius <= 5000)
                throw new InvalidOperationException("Checkpoint source orbit is not safe and finite around Minmus.");
            orbit = new OrbitSnapshot(new ConfigNode()) { semiMajorAxis = o.semiMajorAxis, eccentricity = o.eccentricity,
                inclination = o.inclination, argOfPeriapsis = o.argOfPeriapsis, LAN = o.LAN,
                meanAnomalyAtEpoch = o.meanAnomalyAtEpoch, epoch = o.epoch, ReferenceBodyIndex = o.ReferenceBodyIndex };
            parts = saved.protoPartSnapshots.Select(p => new SavedPart {
                Id = p.flightID, Parent = p.flightID == rootId ? 0 : saved.protoPartSnapshots[p.parentIdx].flightID,
                Name = p.partInfo.name, Stage = p.inverseStageIndex,
                Modules = p.modules.Select(m => m.moduleName).ToArray(),
                Resources = p.resources.Select(r => new Resource { Name = r.resourceName, Amount = r.amount, Maximum = r.maxAmount, Flow = r.flowState }).ToArray()
            }).ToArray();
            if (parts.Single(p => p.Id == rootId).Name != "mk1-3pod" || !parts.Any(p => p.Name == "liquidEngine2-2.v2"))
                throw new InvalidOperationException("Checkpoint stock command/Poodle fingerprint missing.");
            Epoch = sourceUT + 60;
        }

        static double ReadNumber(ConfigNode node, string key)
        {
            double value;
            if (!double.TryParse(node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !SurveyPolicy.Finite(value))
                throw new InvalidOperationException("Invalid saved control value: " + key);
            return value;
        }

        static void RequireFalse(ConfigNode node, string key)
        {
            if (node == null || node.GetValue(key) != "False") throw new InvalidOperationException("Checkpoint saved controller is active or missing: " + key);
        }

        public void Verify(Vessel vessel, string report)
        {
            double now = Planetarium.GetUniversalTime();
            Orbit expected = orbit.Load();
            bool topology = vessel.id == vesselId && vessel.rootPart.flightID == rootId && vessel.GetReferenceTransformPart() != null &&
                vessel.GetReferenceTransformPart().flightID == referenceId && vessel.parts.Count == parts.Length;
            bool resources = true;
            using (var writer = new StreamWriter(report, false))
            {
                writer.WriteLine("part_id,resource,source_amount,loaded_amount,capacity,flow");
                foreach (SavedPart saved in parts)
                {
                    Part part = vessel.parts.SingleOrDefault(p => p.flightID == saved.Id);
                    if (part == null) { topology = false; continue; }
                    topology &= part.partInfo.name == saved.Name && (part.parent == null ? 0 : part.parent.flightID) == saved.Parent &&
                        part.inverseStage == saved.Stage && part.Modules.Cast<PartModule>().Select(m => m.moduleName).SequenceEqual(saved.Modules);
                    resources &= part.Resources.Count == saved.Resources.Length;
                    foreach (Resource savedResource in saved.Resources)
                    {
                        PartResource actual = part.Resources.Get(savedResource.Name);
                        if (actual == null) { resources = false; continue; }
                        resources &= actual.maxAmount == savedResource.Maximum && actual.flowState == savedResource.Flow &&
                            CheckpointPolicy.ResourceMatches(savedResource.Name, savedResource.Amount, actual.amount, actual.maxAmount);
                        writer.WriteLine(saved.Id + "," + savedResource.Name + "," + F(savedResource.Amount) + "," + F(actual.amount) + "," + F(actual.maxAmount) + "," + actual.flowState);
                    }
                }
            }
            double positionError = (expected.getRelativePositionAtUT(now) - vessel.orbit.getRelativePositionAtUT(now)).magnitude;
            double velocityError = (expected.getOrbitalVelocityAtUT(now) - vessel.orbit.getOrbitalVelocityAtUT(now)).magnitude;
            File.WriteAllText(Path.ChangeExtension(report, ".txt"), "observedUT=" + F(now) + "\npositionResidualM=" + F(positionError) +
                "\nvelocityResidualMps=" + F(velocityError) + "\ntopologyMatches=" + topology + "\nresourcesMatch=" + resources +
                "\nsourceRootId=" + rootId + "\nloadedRootId=" + vessel.rootPart.flightID + "\nsourceReferenceId=" + referenceId +
                "\nloadedReferenceId=" + (vessel.GetReferenceTransformPart() == null ? 0 : vessel.GetReferenceTransformPart().flightID) +
                "\nsourceBodyRotation=" + sourceRotation.ToString("R") + "\nloadedBodyRotation=" + (UnityEngine.Quaternion.Inverse(vessel.mainBody.bodyTransform.rotation) * vessel.transform.rotation).ToString("R") +
                "\nsourcePositionAtObservedUT=" + expected.getRelativePositionAtUT(now).ToString("R") + "\nloadedPositionAtObservedUT=" + vessel.orbit.getRelativePositionAtUT(now).ToString("R") +
                "\nsourceVelocityAtObservedUT=" + expected.getOrbitalVelocityAtUT(now).ToString("R") + "\nloadedVelocityAtObservedUT=" + vessel.orbit.getOrbitalVelocityAtUT(now).ToString("R") +
                "\nresourcePolicy=Exact capacity/flow; amounts within 1e-5+1e-8*source except ElectricCharge requires >=90% source and absolute drift <=10% capacity per resource.\nangularSpeedRadS=" + F(vessel.angularVelocity.magnitude) + "\n");
            if (vessel.situation != Vessel.Situations.ORBITING || expected.referenceBody != vessel.mainBody ||
                !CheckpointPolicy.Qualifies(vessel.mainBody.bodyName, vessel.currentStage, vessel.parts.Count, vessel.orbit.semiMajorAxis,
                    vessel.orbit.eccentricity, vessel.orbit.PeA, positionError, velocityError, topology, resources))
                throw new InvalidOperationException("Reconstructed checkpoint failed orbital/topology/resource witness.");
        }

        public static void RequireIdle(Vessel vessel, MechJebCore core, string report)
        {
            try { CheckIdle(vessel, core); }
            catch { File.WriteAllText(report, Describe(vessel, core)); throw; }
            if (!File.Exists(report)) File.WriteAllText(report, Describe(vessel, core));
        }

        static string Describe(Vessel v, MechJebCore c)
        {
            string state = "ut=" + F(Planetarium.GetUniversalTime()) + "\nSAS=" + v.ActionGroups[KSPActionGroup.SAS] + "\nRCS=" + v.ActionGroups[KSPActionGroup.RCS] +
                "\nautopilot=" + v.Autopilot.Enabled + "\ngroupOverride=" + v.GroupOverride + "\nthrottle=" + v.ctrlState.mainThrottle + "\ninputThrottle=" + FlightInputHandler.state.mainThrottle +
                "\nnodes=" + (v.patchedConicSolver == null ? -1 : v.patchedConicSolver.maneuverNodes.Count) + "\nmasterPresent=" + (c != null) + "\n";
            if (c != null) state += "node=" + c.Node.Enabled + "\nascent=" + c.Ascent.Enabled + "\nlanding=" + c.Landing.Enabled + "\nstaging=" + c.Staging.Enabled +
                "\nattitude=" + c.Attitude.Enabled + "\nsmartASS=" + c.GetComputerModule<MechJebModuleSmartASS>().target + "\nthrustMode=" + c.Thrust.Tmode +
                "\ntargetThrottle=" + c.Thrust.TargetThrottle + "\nminimumThrottleEnabled=" + c.Thrust.LimiterMinThrottle + "\nminimumThrottleFraction=" + F(c.Thrust.MinThrottle.Val) + "\n";
            Delegate[] groups = { v.OnPreAutopilotUpdate, v.OnAutopilotUpdate, v.OnPostAutopilotUpdate, v.OnFlyByWire };
            for (int i = 0; i < groups.Length; i++)
                if (groups[i] != null) foreach (Delegate callback in groups[i].GetInvocationList())
                    state += "callback" + i + "=" + callback.Method.DeclaringType.FullName + "." + callback.Method.Name + ":" + callback.Method.MetadataToken + ":" + callback.Method.Module.ModuleVersionId + "\n";
            return state;
        }

        static void CheckIdle(Vessel vessel, MechJebCore core)
        {
            if (core == null || vessel.GetMasterMechJeb() != core || core.AscentSettings == null ||
                core.Node.Enabled || core.Ascent.Enabled || core.Landing.Enabled || core.Staging.Enabled || core.Attitude.Enabled ||
                core.GetComputerModule<MechJebModuleSmartASS>().target.ToString() != "OFF" ||
                core.Thrust.Tmode.ToString() != "OFF" || core.Thrust.TargetThrottle != 0 ||
                vessel.ctrlState.mainThrottle != 0 || FlightInputHandler.state.mainThrottle != 0 ||
                vessel.ActionGroups[KSPActionGroup.SAS] || vessel.ActionGroups[KSPActionGroup.RCS] || vessel.Autopilot.Enabled || vessel.GroupOverride != 0 ||
                vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes.Count != 0)
                throw new InvalidOperationException("Checkpoint requires idle native and MechJeb control owners, zero throttle and no nodes.");
            Audit(vessel.OnPreAutopilotUpdate, core, 0x0600aa76, false);
            Audit(vessel.OnAutopilotUpdate, core, 0x0600aa77, false);
            Audit(vessel.OnPostAutopilotUpdate, core, 0x0600aa78, false);
            Audit(vessel.OnFlyByWire, core, 0x0600aa79, true);
        }

        static void Audit(Delegate callbacks, MechJebCore core, int token, bool allowCore)
        {
            if (callbacks == null) return;
            foreach (Delegate callback in callbacks.GetInvocationList())
            {
                if (allowCore && ReferenceEquals(callback.Target, core) && callback.Method.Name == "OnFlyByWire" && callback.Method.DeclaringType == typeof(MechJebCore)) continue;
                if (callback.Method.Module.ModuleVersionId == typeof(Vessel).Module.ModuleVersionId && callback.Method.MetadataToken == token) continue;
                throw new InvalidOperationException("Checkpoint refuses a foreign control callback: " + callback.Method.DeclaringType.Name + "." + callback.Method.Name);
            }
        }
        static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    }
}
