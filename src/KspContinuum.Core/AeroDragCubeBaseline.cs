using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KspContinuum
{
    public enum AeroBaselineDisposition { Valid, Abstained }
    public enum AeroBaselineReason { None, NoDragCubes }

    public sealed class AeroBaselineResult
    {
        internal AeroBaselineResult(AeroBaselineDisposition disposition, AeroBaselineReason reason,
            long flightId, Vec force, Vec position, Vec torque, double dynamicPressure, double projectedArea)
        {
            Disposition = disposition; Reason = reason; FlightId = flightId; ForceNewtons = force;
            WorldApplicationPosition = position; TorqueAboutPartCenterOfMassNewtonMeters = torque;
            DynamicPressurePascals = dynamicPressure; WeightedProjectedAreaSquareMeters = projectedArea;
        }

        public AeroBaselineDisposition Disposition { get; private set; }
        public AeroBaselineReason Reason { get; private set; }
        public long FlightId { get; private set; }
        public Vec ForceNewtons { get; private set; }
        public Vec WorldApplicationPosition { get; private set; }
        public Vec TorqueAboutPartCenterOfMassNewtonMeters { get; private set; }
        public double DynamicPressurePascals { get; private set; }
        public double WeightedProjectedAreaSquareMeters { get; private set; }
    }

    public static class AeroDragCubeBaseline
    {
        public const string Strategy = "continuum-drag-cube-projection/v2";

        public static AeroBaselineResult Evaluate(AeroPartContext input)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (input.dragCubes.Count == 0)
                return new AeroBaselineResult(AeroBaselineDisposition.Abstained, AeroBaselineReason.NoDragCubes,
                    input.flightId, new Vec(), input.worldCenterOfMass, new Vec(), 0, 0);

            double speedSquared = Dot(input.relativeAirVelocity, input.relativeAirVelocity);
            if (input.shielded || input.densityKilogramsPerCubicMeter == 0 || speedSquared == 0)
                return new AeroBaselineResult(AeroBaselineDisposition.Valid, AeroBaselineReason.None,
                    input.flightId, new Vec(), input.worldCenterOfMass, new Vec(),
                    .5 * input.densityKilogramsPerCubicMeter * speedSquared, 0);

            double speed = Math.Sqrt(speedSquared);
            Vec worldDirection = input.relativeAirVelocity * (-1 / speed);
            Rotation attitude = new Rotation(input.worldAttitudeXYZ.X, input.worldAttitudeXYZ.Y,
                input.worldAttitudeXYZ.Z, input.worldAttitudeW);
            Vec localDirection = attitude.Inverse.Rotate(worldDirection);
            double projected = 0, centerWeight = 0;
            Vec weightedCenter = new Vec();
            foreach (AeroDragCubeState cube in input.dragCubes)
            {
                double contribution = cube.weight * FaceContribution(cube, localDirection);
                projected += contribution;
                weightedCenter += cube.center * contribution;
                centerWeight += contribution;
            }

            double dynamicPressure = .5 * input.densityKilogramsPerCubicMeter * speedSquared;
            Vec force = worldDirection * (dynamicPressure * projected);
            Vec position = input.worldCenterOfMass;
            if (centerWeight > 0) position += attitude.Rotate(weightedCenter * (1 / centerWeight));
            Vec arm = new Vec(position.X - input.worldCenterOfMass.X, position.Y - input.worldCenterOfMass.Y,
                position.Z - input.worldCenterOfMass.Z);
            return new AeroBaselineResult(AeroBaselineDisposition.Valid, AeroBaselineReason.None, input.flightId,
                force, position, Vec.Cross(arm, force), dynamicPressure, projected);
        }

        public static AeroBaselineResult[] EvaluateScalar(IReadOnlyList<AeroPartContext> inputs)
        {
            ValidateBatch(inputs);
            var results = new AeroBaselineResult[inputs.Count];
            for (int index = 0; index < results.Length; index++) results[index] = Evaluate(inputs[index]);
            return results;
        }

        public static AeroBaselineResult[] EvaluateParallel(IReadOnlyList<AeroPartContext> inputs, int maximumDegreeOfParallelism)
        {
            ValidateBatch(inputs);
            if (maximumDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException("maximumDegreeOfParallelism");
            var results = new AeroBaselineResult[inputs.Count];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = maximumDegreeOfParallelism },
                index => results[index] = Evaluate(inputs[index]));
            return results;
        }

        static double FaceContribution(AeroDragCubeState cube, Vec direction)
        {
            return AxisContribution(cube, direction.X, 0, 1) + AxisContribution(cube, direction.Y, 2, 3) +
                AxisContribution(cube, direction.Z, 4, 5);
        }

        static double AxisContribution(AeroDragCubeState cube, double component, int positiveFace, int negativeFace)
        {
            int face = component >= 0 ? positiveFace : negativeFace;
            return Math.Abs(component) * cube.area[face] * cube.drag[face];
        }

        static void ValidateBatch(IReadOnlyList<AeroPartContext> inputs)
        {
            if (inputs == null) throw new ArgumentNullException("inputs");
            if (inputs.Count > 1_000_000) throw new ArgumentException("Baseline batch exceeds its experiment bound.", "inputs");
            for (int index = 0; index < inputs.Count; index++)
                if (inputs[index] == null) throw new ArgumentException("Baseline batch contains a null input.", "inputs");
        }

        static double Dot(Vec left, Vec right) { return left.X * right.X + left.Y * right.Y + left.Z * right.Z; }
    }
}
