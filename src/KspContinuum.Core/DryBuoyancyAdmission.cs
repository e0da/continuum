using System;

namespace KspContinuum
{
    public enum DryBuoyancyDisposition { RunStock, SkipStock }

    public struct DryBuoyancyVesselState
    {
        public bool flightReady, active, loaded, packed, orbiting, bodyPresent, bodyHasOcean;
        public double altitudeMeters, vesselBoundMeters, radialSpeedMetersPerSecond, fixedDeltaSeconds;
    }

    public struct DryBuoyancyPartState
    {
        public bool bodyInitialized, bodyMatchesVessel, splashed, settledDry;
        public double depthMeters;
    }

    public static class DryBuoyancyAdmission
    {
        public const double MinimumClearanceMeters = 10000.0;

        public static DryBuoyancyDisposition Decide(DryBuoyancyVesselState vessel, DryBuoyancyPartState part)
        {
            return VesselEligible(vessel) && PartEligible(part) ? DryBuoyancyDisposition.SkipStock : DryBuoyancyDisposition.RunStock;
        }

        public static bool VesselEligible(DryBuoyancyVesselState vessel)
        {
            if (!vessel.flightReady || !vessel.active || !vessel.loaded || vessel.packed || !vessel.orbiting ||
                !vessel.bodyPresent || !vessel.bodyHasOcean)
                return false;
            if (!Finite(vessel.altitudeMeters) || !Finite(vessel.vesselBoundMeters) || vessel.vesselBoundMeters < 0 ||
                !Finite(vessel.radialSpeedMetersPerSecond) || !Finite(vessel.fixedDeltaSeconds) || vessel.fixedDeltaSeconds <= 0)
                return false;

            double approach = Math.Max(0, -vessel.radialSpeedMetersPerSecond) * vessel.fixedDeltaSeconds * 2;
            double clearance = Math.Max(MinimumClearanceMeters, vessel.vesselBoundMeters + approach);
            return vessel.altitudeMeters > clearance;
        }

        public static bool PartEligible(DryBuoyancyPartState part) => part.bodyInitialized && part.bodyMatchesVessel &&
            !part.splashed && part.settledDry && Finite(part.depthMeters) && part.depthMeters <= 0;

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
