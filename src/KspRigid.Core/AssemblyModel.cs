using System;
using System.Collections.Generic;

namespace KspRigid
{
    public struct Vec
    {
        public double X, Y, Z;
        public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec operator +(Vec a, Vec b) { return new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec operator *(Vec a, double b) { return new Vec(a.X * b, a.Y * b, a.Z * b); }
        public static Vec Product(Vec a, Vec b) { return new Vec(a.X * b.X, a.Y * b.Y, a.Z * b.Z); }
        public static Vec Cross(Vec a, Vec b) { return new Vec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    }

    public struct Box
    {
        public readonly double Mass, CenterX, X, Y, Z;
        public Box(double mass, double centerX, double x, double y, double z)
        {
            AssemblyModel.Positive(mass); AssemblyModel.Positive(x); AssemblyModel.Positive(y); AssemblyModel.Positive(z);
            AssemblyModel.Finite(centerX);
            Mass = mass; CenterX = centerX; X = x; Y = y; Z = z;
        }
        public Vec Inertia { get { return new Vec(Mass * (Y * Y + Z * Z) / 12, Mass * (X * X + Z * Z) / 12, Mass * (X * X + Y * Y) / 12); } }
    }

    public struct MassProperties
    {
        public double Mass, CenterX;
        public Vec Inertia;
    }

    public static class AssemblyModel
    {
        public static void Finite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Value must be finite");
        }
        public static void Positive(double value)
        {
            Finite(value);
            if (value <= 0) throw new ArgumentException("Value must be positive");
        }
        public static MassProperties Combine(IList<Box> boxes)
        {
            if (boxes == null || boxes.Count == 0) throw new ArgumentException("At least one box is required");
            double mass = 0, moment = 0;
            foreach (var b in boxes)
            {
                Positive(b.Mass); Positive(b.X); Positive(b.Y); Positive(b.Z); Finite(b.CenterX);
                mass += b.Mass; moment += b.Mass * b.CenterX;
            }
            Positive(mass); Finite(moment);
            double center = moment / mass;
            Vec inertia = new Vec();
            foreach (var b in boxes)
            {
                double d = b.CenterX - center;
                inertia += b.Inertia + new Vec(0, b.Mass * d * d, b.Mass * d * d);
            }
            Positive(inertia.X); Positive(inertia.Y); Positive(inertia.Z);
            return new MassProperties { Mass = mass, CenterX = center, Inertia = inertia };
        }
        public static Vec PointVelocity(Vec linear, Vec angular, Vec offset)
        {
            return linear + Vec.Cross(angular, offset);
        }
    }
}
