using System;

namespace KspContinuum
{
    public enum AeroSetDragDisposition { Valid, Abstained }
    public enum AeroSetDragReason { None, ZeroFlow, UnsupportedWeightedCurve, OutsideCurveDomain, NonfiniteResult }

    public sealed class AeroSetDragResult
    {
        internal AeroSetDragResult(AeroSetDragDisposition disposition, AeroSetDragReason reason, double areaDrag)
        { Disposition = disposition; Reason = reason; AreaDragSquareMeters = areaDrag; }

        public AeroSetDragDisposition Disposition { get; private set; }
        public AeroSetDragReason Reason { get; private set; }
        public double AreaDragSquareMeters { get; private set; }
    }

    public static class AeroSetDragReconstruction
    {
        static readonly Vec[] FaceDirections = {
            new Vec(1, 0, 0), new Vec(-1, 0, 0), new Vec(0, 1, 0),
            new Vec(0, -1, 0), new Vec(0, 0, 1), new Vec(0, 0, -1)
        };

        public static AeroSetDragResult Evaluate(AeroPartContext input)
        {
            if (input == null) throw new ArgumentNullException("input");
            AeroSetDragReason support = Support(input.setDragInputs);
            if (support != AeroSetDragReason.None)
                return new AeroSetDragResult(AeroSetDragDisposition.Abstained, support, 0);
            double speedSquared = Dot(input.relativeAirVelocity, input.relativeAirVelocity);
            if (speedSquared == 0)
                return new AeroSetDragResult(AeroSetDragDisposition.Valid, AeroSetDragReason.ZeroFlow, 0);
            var attitude = new Rotation(input.worldAttitudeXYZ.X, input.worldAttitudeXYZ.Y,
                input.worldAttitudeXYZ.Z, input.worldAttitudeW);
            Vec worldDragDirection = input.relativeAirVelocity * (-1 / Math.Sqrt(speedSquared));
            return Evaluate(attitude.Inverse.Rotate(worldDragDirection), input.mach, input.setDragInputs);
        }

        public static AeroSetDragResult Evaluate(Vec localDragDirection, double mach, AeroSetDragInputs inputs)
        {
            AeroCaptureValidation.Vector(localDragDirection); AeroCaptureValidation.Number(mach);
            if (mach < 0) throw new ArgumentOutOfRangeException("mach");
            if (inputs == null) throw new ArgumentNullException("inputs");
            AeroSetDragReason support = Support(inputs);
            if (support != AeroSetDragReason.None)
                return new AeroSetDragResult(AeroSetDragDisposition.Abstained, support, 0);
            double lengthSquared = Dot(localDragDirection, localDragDirection);
            if (lengthSquared == 0)
                return new AeroSetDragResult(AeroSetDragDisposition.Valid, AeroSetDragReason.ZeroFlow, 0);
            Vec direction = localDragDirection * (1 / Math.Sqrt(lengthSquared));

            double areaDrag = 0;
            try
            {
                for (int face = 0; face < FaceDirections.Length; face++)
                {
                    double dot = Dot(direction, FaceDirections[face]);
                    double normalized = (dot + 1) * .5;
                    double surface = SurfaceValue(inputs.surfaceCurves, normalized, mach);
                    double drag = inputs.weightedDragCoefficients[face];
                    if (drag < 1)
                        drag = Math.Pow(EvaluateCurve(inputs.dragCurveCd, drag), EvaluateCurve(inputs.dragCurveCdPower, mach));
                    areaDrag += inputs.areaOccludedSquareMeters[face] * surface * drag;
                }
            }
            catch (CurveDomainException)
            { return new AeroSetDragResult(AeroSetDragDisposition.Abstained, AeroSetDragReason.OutsideCurveDomain, 0); }
            if (double.IsNaN(areaDrag) || double.IsInfinity(areaDrag) || areaDrag < 0)
                return new AeroSetDragResult(AeroSetDragDisposition.Abstained, AeroSetDragReason.NonfiniteResult, 0);
            return new AeroSetDragResult(AeroSetDragDisposition.Valid, AeroSetDragReason.None, areaDrag);
        }

        static AeroSetDragReason Support(AeroSetDragInputs inputs)
        {
            foreach (AeroFloatCurveDefinition curve in new[] { inputs.surfaceCurves.tail, inputs.surfaceCurves.surface,
                inputs.surfaceCurves.multiplier, inputs.surfaceCurves.tip, inputs.dragCurveCd, inputs.dragCurveCdPower })
            {
                foreach (AeroCurveKey key in curve.keys)
                    if (key.weightedMode != 0) return AeroSetDragReason.UnsupportedWeightedCurve;
            }
            return AeroSetDragReason.None;
        }

        static double SurfaceValue(AeroSurfaceCurveDefinitions curves, double normalizedDot, double mach)
        {
            double multiplier = EvaluateCurve(curves.multiplier, mach);
            if (normalizedDot <= .5)
                return Lerp(EvaluateCurve(curves.tail, mach), EvaluateCurve(curves.surface, mach), normalizedDot * 2) * multiplier;
            return Lerp(EvaluateCurve(curves.surface, mach), EvaluateCurve(curves.tip, mach), (normalizedDot - .5) * 2) * multiplier;
        }

        static double EvaluateCurve(AeroFloatCurveDefinition curve, double time)
        {
            if (curve.keys.Count == 1) return curve.keys[0].value;
            if (time < curve.keys[0].time || time > curve.keys[curve.keys.Count - 1].time)
                throw new CurveDomainException();
            int right = 1;
            while (right < curve.keys.Count - 1 && time > curve.keys[right].time) right++;
            AeroCurveKey leftKey = curve.keys[right - 1], rightKey = curve.keys[right];
            double duration = rightKey.time - leftKey.time;
            double t = (time - leftKey.time) / duration;
            double t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * leftKey.value + (t3 - 2 * t2 + t) * duration * leftKey.outTangent +
                (-2 * t3 + 3 * t2) * rightKey.value + (t3 - t2) * duration * rightKey.inTangent;
        }

        static double Lerp(double left, double right, double amount) => left + (right - left) * Math.Max(0, Math.Min(1, amount));
        static double Dot(Vec left, Vec right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

        sealed class CurveDomainException : Exception { }
    }
}
