using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum AeroSetDragReproductionDisposition { Sufficient, Insufficient }

    public sealed class AeroSetDragInputAudit
    {
        internal AeroSetDragInputAudit(double directionalAreaDragProxy, string[] missingRuntimeInputs)
        {
            DirectionalAreaDragProxy = directionalAreaDragProxy;
            MissingRuntimeInputs = Array.AsReadOnly(missingRuntimeInputs);
            Disposition = missingRuntimeInputs.Length == 0
                ? AeroSetDragReproductionDisposition.Sufficient
                : AeroSetDragReproductionDisposition.Insufficient;
        }

        public AeroSetDragReproductionDisposition Disposition { get; private set; }
        public double DirectionalAreaDragProxy { get; private set; }
        public ReadOnlyCollection<string> MissingRuntimeInputs { get; private set; }
    }

    public sealed class AeroSetDragComparison
    {
        internal AeroSetDragComparison(double directionDelta, double machDelta, double proxyRelativeDelta,
            double observedRelativeDelta, bool exposesHiddenRuntimeState)
        {
            DirectionDelta = directionDelta; MachDelta = machDelta; ProxyRelativeDelta = proxyRelativeDelta;
            ObservedRelativeDelta = observedRelativeDelta; ExposesHiddenRuntimeState = exposesHiddenRuntimeState;
        }

        public double DirectionDelta { get; private set; }
        public double MachDelta { get; private set; }
        public double ProxyRelativeDelta { get; private set; }
        public double ObservedRelativeDelta { get; private set; }
        public bool ExposesHiddenRuntimeState { get; private set; }
    }

    public static class AeroSetDragDiagnostic
    {
        static readonly string[] Missing = {
            "DragCubeList.areaOccluded[6]",
            "DragCubeList.weightedDrag[6] after attachment occlusion",
            "DragCubeList.SurfaceCurves",
            "DragCubeList.DragCurveCd",
            "DragCubeList.DragCurveCdPower"
        };

        public static AeroSetDragInputAudit Audit(AeroPartContext input)
        {
            if (input == null) throw new ArgumentNullException("input");
            double speed = Math.Sqrt(Dot(input.relativeAirVelocity, input.relativeAirVelocity));
            if (speed == 0) return new AeroSetDragInputAudit(0, (string[])Missing.Clone());
            var attitude = new Rotation(input.worldAttitudeXYZ.X, input.worldAttitudeXYZ.Y,
                input.worldAttitudeXYZ.Z, input.worldAttitudeW);
            Vec localDirection = attitude.Inverse.Rotate(input.relativeAirVelocity * (-1 / speed));
            return Audit(localDirection, input.dragCubes);
        }

        public static AeroSetDragInputAudit Audit(Vec localDirection, IReadOnlyList<AeroDragCubeState> cubes)
        {
            AeroCaptureValidation.Vector(localDirection);
            if (cubes == null) throw new ArgumentNullException("cubes");
            double proxy = 0;
            foreach (AeroDragCubeState cube in cubes)
            {
                if (cube == null) throw new ArgumentException("Drag cube list contains a null cube.", "cubes");
                proxy += cube.weight * (Face(cube, 0, localDirection.X) + Face(cube, 1, -localDirection.X) +
                    Face(cube, 2, localDirection.Y) + Face(cube, 3, -localDirection.Y) +
                    Face(cube, 4, localDirection.Z) + Face(cube, 5, -localDirection.Z));
            }
            return new AeroSetDragInputAudit(proxy, (string[])Missing.Clone());
        }

        public static AeroSetDragComparison Compare(Vec leftDirection, double leftMach, double leftObservedAreaDrag,
            Vec rightDirection, double rightMach, double rightObservedAreaDrag, IReadOnlyList<AeroDragCubeState> cubes,
            double maximumInputDelta, double minimumOutputRelativeDelta)
        {
            AeroCaptureValidation.Number(leftMach); AeroCaptureValidation.Number(rightMach);
            AeroCaptureValidation.Number(leftObservedAreaDrag); AeroCaptureValidation.Number(rightObservedAreaDrag);
            AeroCaptureValidation.Number(maximumInputDelta); AeroCaptureValidation.Number(minimumOutputRelativeDelta);
            if (leftMach < 0 || rightMach < 0 || leftObservedAreaDrag < 0 || rightObservedAreaDrag < 0 ||
                maximumInputDelta < 0 || minimumOutputRelativeDelta < 0) throw new ArgumentException("Invalid diagnostic bound.");
            AeroSetDragInputAudit left = Audit(leftDirection, cubes), right = Audit(rightDirection, cubes);
            var directionDifference = new Vec(leftDirection.X - rightDirection.X,
                leftDirection.Y - rightDirection.Y, leftDirection.Z - rightDirection.Z);
            double directionDelta = Math.Sqrt(Dot(directionDifference, directionDifference));
            double machDelta = Math.Abs(leftMach - rightMach);
            double proxyDelta = RelativeDelta(left.DirectionalAreaDragProxy, right.DirectionalAreaDragProxy);
            double observedDelta = RelativeDelta(leftObservedAreaDrag, rightObservedAreaDrag);
            bool hidden = directionDelta <= maximumInputDelta && machDelta <= maximumInputDelta &&
                proxyDelta <= maximumInputDelta && observedDelta >= minimumOutputRelativeDelta;
            return new AeroSetDragComparison(directionDelta, machDelta, proxyDelta, observedDelta, hidden);
        }

        static double Face(AeroDragCubeState cube, int index, double dot)
        {
            return Math.Max(0, dot) * cube.area[index] * cube.drag[index];
        }

        static double RelativeDelta(double left, double right)
        {
            return Math.Abs(left - right) / Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 1e-30);
        }

        static double Dot(Vec left, Vec right) { return left.X * right.X + left.Y * right.Y + left.Z * right.Z; }
    }
}
