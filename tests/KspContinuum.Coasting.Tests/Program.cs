using System;
using System.Collections.Generic;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static double Norm(Vec value) { return Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z); }
    static Vec Difference(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    static List<CoastingBody> Fixture(double mu, double radius)
    {
        double speed = Math.Sqrt(mu / radius); var result = new List<CoastingBody>();
        for (int i = 0; i < 37; i++)
        {
            double angle = i * 2 * Math.PI / 37, c = Math.Cos(angle), s = Math.Sin(angle);
            result.Add(new CoastingBody(i, new Vec(radius * c, radius * s, 0), new Vec(-speed * s, speed * c, 0)));
        }
        return result;
    }
    static void Same(CoastingSnapshot a, CoastingSnapshot b)
    {
        Check(a.TimeSeconds == b.TimeSeconds && a.Bodies.Count == b.Bodies.Count, "final clock or membership changed");
        for (int i = 0; i < a.Bodies.Count; i++)
        {
            CoastingBody x = a.Bodies[i], y = b.Bodies[i];
            Check(x.Id == y.Id && x.Position.X == y.Position.X && x.Position.Y == y.Position.Y && x.Position.Z == y.Position.Z &&
                x.Velocity.X == y.Velocity.X && x.Velocity.Y == y.Velocity.Y && x.Velocity.Z == y.Velocity.Z,
                "work batching or presentation cadence changed trajectory");
        }
    }
    static int Main()
    {
        const double mu = 3.986004418e14, radius = 7e6, epoch = 123456789;
        double period = 2 * Math.PI * Math.Sqrt(radius * radius * radius / mu), target = epoch + period;
        List<CoastingBody> fixture = Fixture(mu, radius);
        var finePresentation = new CoastingEngine(epoch, mu, fixture, 1).AdvanceTo(target, 17);
        var sparsePresentation = new CoastingEngine(epoch, mu, fixture, 7).AdvanceTo(target, 311);
        var noPresentation = new CoastingEngine(epoch, mu, fixture, 64).AdvanceTo(target);
        Check(finePresentation.StopEvent == "target-time" && finePresentation.Final.TimeSeconds == target, "target-time event did not own exact stop");
        Check(finePresentation.Publications.Count > sparsePresentation.Publications.Count && noPresentation.Publications.Count == 0,
            "presentation cadence was not independent");
        Same(finePresentation.Final, sparsePresentation.Final); Same(finePresentation.Final, noPresentation.Final);
        CoastingBody initial = fixture[0], final = finePresentation.Final.Bodies[0];
        double positionError = Norm(Difference(final.Position, initial.Position));
        double velocityError = Norm(Difference(final.Velocity, initial.Velocity));
        Check(positionError < 1e-3, "one-period position error exceeded one millimeter: " + positionError.ToString("R"));
        Check(velocityError < 1e-6, "one-period velocity error exceeded one micrometer per second: " + velocityError.ToString("R"));
        CoastingSnapshot detached = finePresentation.Publications[0];
        Same(finePresentation.Final, new CoastingEngine(epoch, mu, fixture, 13).SampleAt(target));
        CoastingSnapshot historical = new CoastingEngine(epoch, mu, fixture, 13).SampleAt(epoch - 10);
        Check(historical.TimeSeconds == epoch - 10, "absolute sample API incorrectly enforced frontier monotonicity");
        var continued = new CoastingEngine(epoch, mu, fixture, 3);
        continued.AdvanceTo(epoch + 100, 5);
        CoastingAdvanceResult continuation = continued.AdvanceTo(target, 1);
        Same(finePresentation.Final, continuation.Final);
        Check(detached.TimeSeconds < continuation.Final.TimeSeconds, "presentation snapshot was not detached from engine time");
        bool rejected = false;
        try { new CoastingEngine(epoch, mu, fixture, 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid work batch accepted");
        Console.WriteLine("PASS " + checks + " coasting-engine assertions");
        return 0;
    }
}
