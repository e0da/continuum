using System;
using KspRigid;

static class Program
{
    static int count;
    static void Near(double expected, double actual)
    {
        count++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > 1e-9 * Math.Max(1, Math.Abs(expected)))
            throw new Exception("Expected " + expected + ", got " + actual);
    }
    static void Reject(Action action)
    {
        count++;
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid input accepted");
    }
    static Box B(double mass, double x) { return new Box(mass, x, 1, 1, 1); }
    static void Main()
    {
        var a = AssemblyModel.Combine(new[] { B(1, -1), B(3, 1) });
        Near(4, a.Mass); Near(0.5, a.CenterX);
        Near(4.0 / 6, a.Inertia.X); Near(4.0 / 6 + 3, a.Inertia.Y); Near(a.Inertia.Y, a.Inertia.Z);
        var translated = AssemblyModel.Combine(new[] { B(1, 99), B(3, 101) });
        Near(a.CenterX + 100, translated.CenterX); Near(a.Inertia.Y, translated.Inertia.Y);
        var single = AssemblyModel.Combine(new[] { new Box(12, 0, 2, 4, 6) });
        Near(52, single.Inertia.X); Near(40, single.Inertia.Y); Near(20, single.Inertia.Z);
        var v = new Vec(2, 3, 4); var omega = new Vec(0.2, -0.3, 0.4);
        var p = new Vec(); var angular = new Vec();
        foreach (var b in new[] { B(1, -1), B(3, 1) })
        {
            var offset = new Vec(b.CenterX - a.CenterX, 0, 0);
            var velocity = AssemblyModel.PointVelocity(v, omega, offset);
            p += velocity * b.Mass;
            angular += Vec.Cross(offset, velocity * b.Mass) + Vec.Product(b.Inertia, omega);
        }
        Near(a.Mass * v.X, p.X); Near(a.Mass * v.Y, p.Y); Near(a.Mass * v.Z, p.Z);
        Near(a.Inertia.X * omega.X, angular.X); Near(a.Inertia.Y * omega.Y, angular.Y); Near(a.Inertia.Z * omega.Z, angular.Z);
        Reject(() => AssemblyModel.Combine(new Box[0]));
        Reject(() => AssemblyModel.Combine(null));
        foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Reject(() => new Box(bad, 0, 1, 1, 1));
            Reject(() => new Box(1, 0, bad, 1, 1));
        }
        Reject(() => new Box(1, double.NaN, 1, 1, 1));
        Reject(() => AssemblyModel.Combine(new[] { B(double.MaxValue, 0), B(double.MaxValue, 1) }));
        Reject(() => AssemblyModel.Combine(new[] { default(Box) }));
        Console.WriteLine("PASS: " + count + " analytic assertions (no Unity or KSP runtime exercised).");
    }
}
