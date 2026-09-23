using System;

namespace KspContinuum
{
    public readonly struct AeroFaceValues
    {
        public readonly double PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ;
        public AeroFaceValues(double positiveX, double negativeX, double positiveY, double negativeY,
            double positiveZ, double negativeZ)
        {
            PositiveX = Require(positiveX); NegativeX = Require(negativeX);
            PositiveY = Require(positiveY); NegativeY = Require(negativeY);
            PositiveZ = Require(positiveZ); NegativeZ = Require(negativeZ);
        }
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
            Axis(0, direction.X, ref area, ref areaDrag, ref section, ref exposure, ref positiveDotSum,
                ref depth, ref taper, ref liftX, ref liftY, ref liftZ, areaOccluded, weightedDrag,
                weightedDepth, dragCd, bodyLift, tail, surface, multiplier, tip, dragCdPower);
            Axis(1, direction.Y, ref area, ref areaDrag, ref section, ref exposure, ref positiveDotSum,
                ref depth, ref taper, ref liftX, ref liftY, ref liftZ, areaOccluded, weightedDrag,
                weightedDepth, dragCd, bodyLift, tail, surface, multiplier, tip, dragCdPower);
            Axis(2, direction.Z, ref area, ref areaDrag, ref section, ref exposure, ref positiveDotSum,
                ref depth, ref taper, ref liftX, ref liftY, ref liftZ, areaOccluded, weightedDrag,
                weightedDepth, dragCd, bodyLift, tail, surface, multiplier, tip, dragCdPower);
            if (positiveDotSum > 0) { depth /= positiveDotSum; taper /= positiveDotSum; }
            double coefficient = area > 0 ? areaDrag / area : 0;
            if (area <= 0) areaDrag = 0;
            if (!Finite(area) || !Finite(areaDrag) || !Finite(section) || !Finite(exposure) || !Finite(depth) ||
                !Finite(taper) || !Finite(coefficient) || !Finite(liftX) || !Finite(liftY) || !Finite(liftZ))
                throw new ArithmeticException("SetDrag produced a nonfinite output.");
            return new AeroCompleteSetDragResult(direction, new Vec(liftX, liftY, liftZ), area, areaDrag,
                depth, section, exposure, coefficient, taper);
        }

        static void Axis(int axis, double component, ref double area, ref double areaDrag, ref double section,
            ref double exposure, ref double dotSum, ref double depth, ref double taper, ref double liftX,
            ref double liftY, ref double liftZ, AeroFaceValues areas, AeroFaceValues drags,
            AeroFaceValues depths, AeroFaceValues dragCd, AeroFaceValues bodyLift, double tail, double surface,
            double multiplier, double tip, double power)
        {
            Accumulate(axis * 2, component, axis, ref area, ref areaDrag, ref section, ref exposure, ref dotSum,
                ref depth, ref taper, ref liftX, ref liftY, ref liftZ, areas, drags, depths, dragCd, bodyLift,
                tail, surface, multiplier, tip, power);
            Accumulate(axis * 2 + 1, -component, axis, ref area, ref areaDrag, ref section, ref exposure, ref dotSum,
                ref depth, ref taper, ref liftX, ref liftY, ref liftZ, areas, drags, depths, dragCd, bodyLift,
                tail, surface, multiplier, tip, power);
        }

        static void Accumulate(int face, double dot, int axis, ref double area, ref double areaDrag,
            ref double section, ref double exposure, ref double dotSum, ref double depth, ref double taper,
            ref double liftX, ref double liftY, ref double liftZ, AeroFaceValues areas, AeroFaceValues drags,
            AeroFaceValues depths, AeroFaceValues dragCd, AeroFaceValues bodyLift, double tail, double surface,
            double multiplier, double tip, double power)
        {
            double drag = drags.At(face);
            double directionalArea = areas.At(face) * (dot < 0 ? Mix(surface, tail, -dot) : Mix(surface, tip, dot)) * multiplier;
            area += directionalArea;
            areaDrag += directionalArea * (drag < 1 ? Math.Pow(dragCd.At(face), power) : drag);
            section += areas.At(face) * Clamp(dot);
            double inverseDrag = drag > .01 && drag < 1 ? 1 / drag : 1;
            exposure += directionalArea / multiplier * inverseDrag;
            if (dot <= 0) return;
            dotSum += dot; depth += dot * depths.At(face); taper += dot * inverseDrag;
            double force = -dot * areas.At(face) * drag * bodyLift.At(face);
            double signed = (face & 1) == 0 ? force : -force;
            if (axis == 0) liftX += signed; else if (axis == 1) liftY += signed; else liftZ += signed;
        }
        static double Mix(double left, double right, double amount) => left + (right - left) * Clamp(amount);
        static double Clamp(double value) => Math.Max(0, Math.Min(1, value));
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
