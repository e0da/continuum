using System;

namespace KspContinuum
{
    public readonly struct AeroFaceValues
    {
        public readonly double PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ;
        public AeroFaceValues(double positiveX, double negativeX, double positiveY, double negativeY,
            double positiveZ, double negativeZ)
            : this(positiveX, negativeX, positiveY, negativeY, positiveZ, negativeZ, true) { }
        AeroFaceValues(double positiveX, double negativeX, double positiveY, double negativeY,
            double positiveZ, double negativeZ, bool validate)
        {
            PositiveX = validate ? Require(positiveX) : positiveX; NegativeX = validate ? Require(negativeX) : negativeX;
            PositiveY = validate ? Require(positiveY) : positiveY; NegativeY = validate ? Require(negativeY) : negativeY;
            PositiveZ = validate ? Require(positiveZ) : positiveZ; NegativeZ = validate ? Require(negativeZ) : negativeZ;
        }
        internal static AeroFaceValues Trusted(double positiveX, double negativeX, double positiveY, double negativeY,
            double positiveZ, double negativeZ) => new AeroFaceValues(positiveX, negativeX, positiveY, negativeY,
                positiveZ, negativeZ, false);
        public double At(int face)
        {
            switch (face)
            {
                case 0: return PositiveX; case 1: return NegativeX;
                case 2: return PositiveY; case 3: return NegativeY;
                case 4: return PositiveZ; case 5: return NegativeZ;
                default: throw new ArgumentOutOfRangeException("face");
            }
        }
        static double Require(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new ArgumentOutOfRangeException("value");
            return value;
        }
    }

    public readonly struct AeroCompleteSetDragResult
    {
        internal AeroCompleteSetDragResult(Vec direction, Vec lift, double area, double areaDrag, double depth,
            double crossSection, double exposedArea, double dragCoefficient, double taperDot)
        {
            DragVector = direction; LiftForce = lift; AreaSquareMeters = area;
            AreaDragSquareMeters = areaDrag; DepthMeters = depth;
            CrossSectionalAreaSquareMeters = crossSection; ExposedAreaSquareMeters = exposedArea;
            DragCoefficient = dragCoefficient; TaperDot = taperDot;
        }
        public Vec DragVector { get; }
        public Vec LiftForce { get; }
        public double AreaSquareMeters { get; }
        public double AreaDragSquareMeters { get; }
        public double DepthMeters { get; }
        public double CrossSectionalAreaSquareMeters { get; }
        public double ExposedAreaSquareMeters { get; }
        public double DragCoefficient { get; }
        public double TaperDot { get; }
    }

    public static class AeroCompleteSetDrag
    {
        // Curve evaluation belongs to the provider adapter. The portable kernel owns the
        // complete six-face reduction and has no dependency on Unity's FloatCurve type.
        public static AeroCompleteSetDragResult Evaluate(Vec direction, AeroFaceValues areaOccluded,
            AeroFaceValues weightedDrag, AeroFaceValues weightedDepth, AeroFaceValues dragCd,
            AeroFaceValues bodyLift, double tail, double surface, double multiplier, double tip,
            double dragCdPower)
        {
            double magnitudeSquared = direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z;
            if (!Finite(magnitudeSquared)) throw new ArgumentException("Direction must be finite.");
            if (magnitudeSquared != 0 && Math.Abs(magnitudeSquared - 1) > 1e-4)
                throw new ArgumentException("Direction must be unit length or zero.");
            if (!Finite(tail) || !Finite(surface) || !Finite(multiplier) || !Finite(tip) || !Finite(dragCdPower) || multiplier == 0)
                throw new ArgumentException("Curve samples must be finite and the surface multiplier must be nonzero.");

            double area = 0, areaDrag = 0, section = 0, exposure = 0, positiveDotSum = 0;
            double depth = 0, taper = 0, liftX = 0, liftY = 0, liftZ = 0;
            Accumulate(direction.X, 1, 0, areaOccluded.PositiveX, weightedDrag.PositiveX, weightedDepth.PositiveX,
                dragCd.PositiveX, bodyLift.PositiveX, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            Accumulate(-direction.X, -1, 0, areaOccluded.NegativeX, weightedDrag.NegativeX, weightedDepth.NegativeX,
                dragCd.NegativeX, bodyLift.NegativeX, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            Accumulate(direction.Y, 1, 1, areaOccluded.PositiveY, weightedDrag.PositiveY, weightedDepth.PositiveY,
                dragCd.PositiveY, bodyLift.PositiveY, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            Accumulate(-direction.Y, -1, 1, areaOccluded.NegativeY, weightedDrag.NegativeY, weightedDepth.NegativeY,
                dragCd.NegativeY, bodyLift.NegativeY, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            Accumulate(direction.Z, 1, 2, areaOccluded.PositiveZ, weightedDrag.PositiveZ, weightedDepth.PositiveZ,
                dragCd.PositiveZ, bodyLift.PositiveZ, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            Accumulate(-direction.Z, -1, 2, areaOccluded.NegativeZ, weightedDrag.NegativeZ, weightedDepth.NegativeZ,
                dragCd.NegativeZ, bodyLift.NegativeZ, ref area, ref areaDrag, ref section, ref exposure,
                ref positiveDotSum, ref depth, ref taper, ref liftX, ref liftY, ref liftZ, tail, surface, multiplier, tip, dragCdPower);
            if (positiveDotSum > 0) { depth /= positiveDotSum; taper /= positiveDotSum; }
            double coefficient = area > 0 ? areaDrag / area : 0;
            if (area <= 0) areaDrag = 0;
            if (!Finite(area) || !Finite(areaDrag) || !Finite(section) || !Finite(exposure) || !Finite(depth) ||
                !Finite(taper) || !Finite(coefficient) || !Finite(liftX) || !Finite(liftY) || !Finite(liftZ))
                throw new ArithmeticException("SetDrag produced a nonfinite output.");
            return new AeroCompleteSetDragResult(direction, new Vec(liftX, liftY, liftZ), area, areaDrag,
                depth, section, exposure, coefficient, taper);
        }

        static void Accumulate(double dot, double sign, int axis, double faceArea, double drag, double faceDepth,
            double dragCd, double bodyLift, ref double area, ref double areaDrag, ref double section,
            ref double exposure, ref double dotSum, ref double depth, ref double taper, ref double liftX,
            ref double liftY, ref double liftZ, double tail, double surface, double multiplier, double tip, double power)
        {
            double directionalArea = faceArea * (dot < 0 ? Mix(surface, tail, -dot) : Mix(surface, tip, dot)) * multiplier;
            area += directionalArea;
            areaDrag += directionalArea * (drag < 1 ? Math.Pow(dragCd, power) : drag);
            section += faceArea * Clamp(dot);
            double inverseDrag = drag > .01 && drag < 1 ? 1 / drag : 1;
            exposure += directionalArea / multiplier * inverseDrag;
            if (dot <= 0) return;
            dotSum += dot; depth += dot * faceDepth; taper += dot * inverseDrag;
            double signed = -dot * faceArea * drag * bodyLift * sign;
            if (axis == 0) liftX += signed; else if (axis == 1) liftY += signed; else liftZ += signed;
        }
        static double Mix(double left, double right, double amount) => left + (right - left) * Clamp(amount);
        static double Clamp(double value) => Math.Max(0, Math.Min(1, value));
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
