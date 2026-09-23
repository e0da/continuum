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
        var cadence = new CoastPresentationCadence(2);
        int cadenceSamples = 0;
        Func<double, CoastingBody> linear = time => { cadenceSamples++; return new CoastingBody(7, new Vec(time, -2 * time, 3), new Vec(1, -2, 0)); };
        CoastingBody firstPresentation = cadence.Evaluate(100, linear);
        CoastingBody middlePresentation = cadence.Evaluate(100.5, linear);
        Check(firstPresentation.Position.X == 100 && middlePresentation.Position.X == 100.5,
            "Hermite presentation did not preserve linear motion");
        Check(cadenceSamples == 2 && cadence.EngineSampleCount == 2, "callback cadence leaked into engine sample cadence");
        Check(cadence.Evaluate(102, linear).Position.X == 102 && cadenceSamples == 3 && cadence.EngineSampleCount == 3,
            "cadence missed its endpoint or miscounted its new sample");
        Check(cadence.Evaluate(102.1, linear).Position.X == 102.1 && cadenceSamples == 3,
            "intermediate callback sampled the engine");
        Check(cadence.Evaluate(106.5, linear).Position.X == 106.5 && cadenceSamples == 5 && cadence.EngineSampleCount == 5,
            "cadence did not re-anchor or count samples after a skipped interval");
        bool backwardsRejected = false;
        try { cadence.Evaluate(106.4, linear); } catch (InvalidOperationException) { backwardsRejected = true; }
        Check(backwardsRejected, "backwards presentation time accepted");
        bool cadenceRejected = false;
        try { new CoastPresentationCadence(0); } catch (ArgumentOutOfRangeException) { cadenceRejected = true; }
        Check(cadenceRejected, "invalid publication cadence accepted");
        int directSamples = 0;
        var directCadence = CoastPresentationCadence.Direct();
        directCadence.Evaluate(10, time => { directSamples++; return new CoastingBody(8, new Vec(time, 0, 0), new Vec(1, 0, 0)); });
        directCadence.Evaluate(10.1, time => { directSamples++; return new CoastingBody(8, new Vec(time, 0, 0), new Vec(1, 0, 0)); });
        Check(directSamples == 2 && directCadence.EngineSampleCount == 2, "direct comparator did not sample every callback");
        const double mu = 3.986004418e14, radius = 7e6, epoch = 123456789;
        double period = 2 * Math.PI * Math.Sqrt(radius * radius * radius / mu), target = epoch + period;
        List<CoastingBody> fixture = Fixture(mu, radius);
        double speed = Math.Sqrt(mu / radius);
        CoastingBody quarter = new CoastingEngine(epoch, mu, fixture, 5).SampleAt(epoch + period / 4).Bodies[0];
        Check(Norm(Difference(quarter.Position, new Vec(0, radius, 0))) < 1e-3,
            "quarter-period position did not traverse the orbit");
        Check(Norm(Difference(quarter.Velocity, new Vec(-speed, 0, 0))) < 1e-6,
            "quarter-period velocity did not rotate with the orbit");
        const double kerbinMu = 3.5316e12, semiMajor = 732639.5703, eccentricity = 0.00888570;
        double periapsis = semiMajor * (1 - eccentricity), apoapsis = semiMajor * (1 + eccentricity);
        double ellipsePeriod = 2 * Math.PI * Math.Sqrt(semiMajor * semiMajor * semiMajor / kerbinMu);
        double periapsisSpeed = Math.Sqrt(kerbinMu * (2 / periapsis - 1 / semiMajor));
        double apoapsisSpeed = Math.Sqrt(kerbinMu * (2 / apoapsis - 1 / semiMajor));
        var ellipse = new[] { new CoastingBody(90, new Vec(periapsis, 0, 0), new Vec(0, periapsisSpeed, 0)) };
        CoastingBody oppositeApsis = new CoastingEngine(epoch, kerbinMu, ellipse, 1).SampleAt(epoch + ellipsePeriod / 2).Bodies[0];
        Check(Norm(Difference(oppositeApsis.Position, new Vec(-apoapsis, 0, 0))) < 1e-5,
            "eccentric half-period position missed analytic apoapsis");
        Check(Norm(Difference(oppositeApsis.Velocity, new Vec(0, -apoapsisSpeed, 0))) < 1e-8,
            "eccentric half-period velocity missed analytic apoapsis");
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
        CoastingAdvanceResult continuation = continued.AdvanceTo(target, 2);
        Same(finePresentation.Final, continuation.Final);
        Check(detached.TimeSeconds < continuation.Final.TimeSeconds, "presentation snapshot was not detached from engine time");
        bool rejected = false;
        try { new CoastingEngine(epoch, mu, fixture, 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid work batch accepted");
        rejected = false;
        try { new CoastingEngine(epoch, mu, fixture, 1).AdvanceTo(epoch + 5000, 1); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unbounded presentation request accepted");
        Same(finePresentation.Final, new CoastingEngine(epoch, mu, fixture, int.MaxValue).SampleAt(target));
        double largeEpoch = 1e20, representableStep = Math.BitIncrement(largeEpoch) - largeEpoch;
        rejected = false;
        try { new CoastingEngine(largeEpoch, mu, fixture, 1).AdvanceTo(largeEpoch + representableStep, representableStep / 4); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "non-advancing first publication was staged");
        Console.WriteLine("PASS " + checks + " coasting-engine assertions");
        return 0;
    }
}
