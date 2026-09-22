using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Near(double expected, double actual, string message, double tolerance = 1e-10)
    { Check(Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)), message + ": " + actual); }
    static void Near(Vec expected, Vec actual, string message, double tolerance = 1e-10)
    { Near(expected.X, actual.X, message + " x", tolerance); Near(expected.Y, actual.Y, message + " y", tolerance); Near(expected.Z, actual.Z, message + " z", tolerance); }
    static Rotation Axis(Vec axis, double radians) { return Rotation.AxisAngle(axis, radians); }
    static RigidBody6Dof Body(int id, double mass, Vec position, Rotation rotation, Vec velocity, Vec angularVelocity, Vec inertia)
    { return new RigidBody6Dof(id, mass, position, rotation, velocity, angularVelocity, inertia); }

    static void UnequalMassesAndAsymmetricOffsets()
    {
        var cluster = RigidCluster6Dof.Capture(new[] {
            Body(1, 1, new Vec(-2, 1, 0), Rotation.Identity, new Vec(3, 0, 0), new Vec(), new Vec(1, 2, 3)),
            Body(2, 3, new Vec(4, -1, 2), Axis(new Vec(0, 0, 1), .4), new Vec(-1, 2, 0), new Vec(), new Vec(2, 4, 5))
        });
        Near(new Vec(2.5, -.5, 1.5), cluster.CenterOfMass, "mass-weighted COM");
        Near(new Vec(0, 6, 0), cluster.LinearMomentum, "linear momentum");
        var poses = cluster.Reconstruct();
        Near(new Vec(-2, 1, 0), poses[0].Position, "first reconstructed position");
        Near(new Vec(4, -1, 2), poses[1].Position, "second reconstructed position");
        Near(4, cluster.TotalMass, "total mass");
    }

    static void OffCenterImpulse()
    {
        var cluster = RigidCluster6Dof.Capture(new[] {
            Body(1, 2, new Vec(-1, 0, 0), Rotation.Identity, new Vec(), new Vec(), new Vec(1, 1, 1)),
            Body(2, 2, new Vec(1, 0, 0), Rotation.Identity, new Vec(), new Vec(), new Vec(1, 1, 1))
        });
        var kicked = cluster.ApplyImpulse(new Vec(1, 0, 0), new Vec(0, 4, 0));
        Near(new Vec(0, 4, 0), kicked.LinearMomentum, "impulse changes linear momentum");
        Near(new Vec(0, 0, 4), kicked.AngularMomentum, "off-center impulse changes angular momentum");
        Near(new Vec(0, 1, 0), kicked.LinearVelocity, "aggregate linear velocity");
        Near(2, kicked.LocalInertia.XX, "aggregate inertia xx");
        Near(6, kicked.LocalInertia.YY, "aggregate inertia yy");
        Near(6, kicked.LocalInertia.ZZ, "aggregate inertia zz");
        Check(kicked.AngularVelocity.Z > 0, "off-center impulse did not induce spin");
        var advanced = kicked.Advance(.25);
        Near(.25, advanced.CenterOfMass.Y, "COM advance");
        var poses = advanced.Reconstruct();
        double angle = 1.0 / 6;
        Near(new Vec(-Math.Cos(angle), .25 - Math.Sin(angle), 0), poses[0].Position, "orientation advance", 2e-10);
        Near(2, Length(Subtract(poses[1].Position, poses[0].Position)), "rigid separation", 2e-10);
    }

    static void Covariance()
    {
        var bodies = new[] {
            Body(7, 2, new Vec(-2, .5, 1), Axis(new Vec(1, 0, 0), .2), new Vec(.2, 1, -.4), new Vec(.1, .3, -.2), new Vec(1, 3, 4)),
            Body(9, 5, new Vec(1, 2, -1), Axis(new Vec(0, 1, 0), -.5), new Vec(-.3, .4, .7), new Vec(-.2, .1, .4), new Vec(2, 5, 6))
        };
        Rotation frame = Axis(new Vec(1, 2, 3), .8);
        Vec shift = new Vec(10, -6, 4);
        var transformed = new RigidBody6Dof[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) transformed[i] = new RigidBody6Dof(bodies[i].Id, bodies[i].Mass,
            frame.Rotate(bodies[i].Position) + shift, frame * bodies[i].Orientation,
            frame.Rotate(bodies[i].LinearVelocity), frame.Rotate(bodies[i].AngularVelocity), bodies[i].PrincipalInertia);
        var a = RigidCluster6Dof.Capture(bodies).ApplyImpulse(new Vec(2, -1, .5), new Vec(.3, 2, -1)).Advance(.1);
        var b = RigidCluster6Dof.Capture(transformed).ApplyImpulse(frame.Rotate(new Vec(2, -1, .5)) + shift, frame.Rotate(new Vec(.3, 2, -1))).Advance(.1);
        Near(frame.Rotate(a.CenterOfMass) + shift, b.CenterOfMass, "translated/rotated COM covariance", 2e-10);
        Near(frame.Rotate(a.LinearMomentum), b.LinearMomentum, "linear momentum covariance", 2e-10);
        Near(frame.Rotate(a.AngularMomentum), b.AngularMomentum, "angular momentum covariance", 2e-10);
        var ap = a.Reconstruct(); var bp = b.Reconstruct();
        for (int i = 0; i < ap.Count; i++)
        {
            Near(frame.Rotate(ap[i].Position) + shift, bp[i].Position, "pose position covariance", 3e-10);
            Near(frame.Rotate(ap[i].LinearVelocity), bp[i].LinearVelocity, "pose velocity covariance", 3e-10);
            Near(1, Math.Abs(Rotation.Dot(frame * ap[i].Orientation, bp[i].Orientation)), "pose rotation covariance", 3e-10);
        }
    }

    static void DeterministicReplay()
    {
        var bodies = new[] {
            Body(1, 1, new Vec(-1, 0, 0), Rotation.Identity, new Vec(0, .2, 0), new Vec(0, 0, .1), new Vec(1, 2, 3)),
            Body(2, 2, new Vec(2, 1, 0), Axis(new Vec(0, 1, 0), .3), new Vec(.1, 0, 0), new Vec(.2, 0, 0), new Vec(2, 3, 4))
        };
        RigidCluster6Dof a = RigidCluster6Dof.Capture(bodies), b = RigidCluster6Dof.Capture(bodies);
        for (int i = 0; i < 100; i++)
        {
            Vec point = new Vec(.01 * i, -.02 * i, .005 * i);
            Vec impulse = new Vec(.001, -.002, .003);
            a = a.ApplyImpulse(point, impulse).Advance(.01);
            b = b.ApplyImpulse(point, impulse).Advance(.01);
        }
        Near(a.CenterOfMass, b.CenterOfMass, "replay COM", 0);
        Near(a.LinearMomentum, b.LinearMomentum, "replay momentum", 0);
        Near(a.AngularMomentum, b.AngularMomentum, "replay angular momentum", 0);
        Near(a.Orientation.X, b.Orientation.X, "replay orientation x", 0);
        Near(a.Orientation.Y, b.Orientation.Y, "replay orientation y", 0);
        Near(a.Orientation.Z, b.Orientation.Z, "replay orientation z", 0);
        Near(a.Orientation.W, b.Orientation.W, "replay orientation w", 0);
    }

    static Vec Subtract(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    static double Length(Vec value) { return Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z); }

    static int Main()
    {
        UnequalMassesAndAsymmetricOffsets(); OffCenterImpulse(); Covariance(); DeterministicReplay();
        Console.WriteLine("PASS " + checks + " six-DOF rigid-cluster assertions");
        return 0;
    }
}
