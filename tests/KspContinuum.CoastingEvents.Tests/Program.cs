using System;
using System.Collections.Generic;
using System.Diagnostics;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static double Radius(Vec v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }
    static CoastingBody Periapsis(int id, double mu, double semiMajor, double eccentricity, double phase = 0)
    {
        double radius = semiMajor * (1 - eccentricity), speed = Math.Sqrt(mu * (2 / radius - 1 / semiMajor));
        double c = Math.Cos(phase), s = Math.Sin(phase);
        return new CoastingBody(id, new Vec(radius * c, radius * s, 0), new Vec(-speed * s, speed * c, 0));
    }
    static CoastingBody Shifted(int id, double mu, double semiMajor, double eccentricity, double seconds)
    {
        CoastingBody state = new CoastingEngine(0, mu, new[] { Periapsis(id, mu, semiMajor, eccentricity) }, 1)
            .SampleBodyAt(id, seconds);
        return new CoastingBody(id, state.Position, state.Velocity);
    }
    static RadiusCrossingEvent Run(int count, int batch, double publication, out double elapsedMilliseconds)
    {
        const double mu = 3.5316e12, semiMajor = 732639.5703, eccentricity = .00888570, epoch = 123456789;
        double meanMotion = Math.Sqrt(mu / (semiMajor * semiMajor * semiMajor));
        double expected = epoch + (Math.PI / 2 - eccentricity) / meanMotion;
        var bodies = new List<CoastingBody>();
        for (int i = 0; i < count; i++) bodies.Add(Periapsis(i, mu, semiMajor, eccentricity, i * 2 * Math.PI / count));
        var engine = new CoastingEngine(epoch, mu, bodies, batch); var clock = Stopwatch.StartNew();
        RadiusCrossingEvent found = CoastingEventScheduler.FindFirst(engine, new RadiusCrossingSearch(epoch, expected + 200,
            semiMajor, RadiusCrossingDirection.Outward, 1e-7));
        clock.Stop(); elapsedMilliseconds = clock.Elapsed.TotalMilliseconds;
        Check(found != null && found.Kind == "radius-crossing" && found.BodyId == 0, "first outward crossing changed identity");
        Check(Math.Abs(found.TimeSeconds - expected) <= 2e-7, "refined crossing missed analytic eccentric anomaly time");
        Check(Math.Abs(Radius(found.Body.Position) - semiMajor) < .002, "refined event state missed target radius");
        CoastingAdvanceResult advanced = engine.AdvanceTo(found.TimeSeconds, publication);
        Check(advanced.StopEvent == "target-time" && advanced.Final.TimeSeconds == found.TimeSeconds,
            "engine did not stop exactly at predicted boundary");
        Check(engine.SampleBodyAt(found.BodyId, found.TimeSeconds).Position.X == found.Body.Position.X,
            "event refinement diverged from direct sample");
        return found;
    }
    static int Main()
    {
        double oneMs, sevenMs, allMs;
        RadiusCrossingEvent one = Run(1, 1, 3, out oneMs);
        RadiusCrossingEvent seven = Run(1, 7, 17, out sevenMs);
        RadiusCrossingEvent all = Run(1, int.MaxValue, 0, out allMs);
        Check(one.TimeSeconds == seven.TimeSeconds && one.TimeSeconds == all.TimeSeconds,
            "work batch or presentation cadence changed refined event time");
        const double kerbinMu = 3.5316e12, semiMajor = 732639.5703, eccentricity = .00888570, epoch = 123456789;
        double meanMotion = Math.Sqrt(kerbinMu / (semiMajor * semiMajor * semiMajor));
        double inwardExpected = epoch + (3 * Math.PI / 2 + eccentricity) / meanMotion;
        var inwardEngine = new CoastingEngine(epoch, kerbinMu,
            new[] { Periapsis(3, kerbinMu, semiMajor, eccentricity) }, 1);
        RadiusCrossingEvent inward = CoastingEventScheduler.FindFirst(inwardEngine, new RadiusCrossingSearch(epoch,
            inwardExpected + 100, semiMajor, RadiusCrossingDirection.Inward, 1e-7));
        Check(inward != null && inward.BodyId == 3 && Math.Abs(inward.TimeSeconds - inwardExpected) <= 2e-7,
            "inward crossing missed analytic eccentric-anomaly time");
        double outwardAfterPeriapsis = (Math.PI / 2 - eccentricity) / meanMotion;
        var staggered = new CoastingEngine(epoch, kerbinMu, new[] {
            Shifted(8, kerbinMu, semiMajor, eccentricity, 0),
            Shifted(4, kerbinMu, semiMajor, eccentricity, 100)
        }, 2);
        RadiusCrossingEvent earliest = CoastingEventScheduler.FindFirst(staggered, new RadiusCrossingSearch(epoch,
            epoch + outwardAfterPeriapsis + 50, semiMajor, RadiusCrossingDirection.Outward, 1e-7));
        Check(earliest != null && earliest.BodyId == 4 &&
            Math.Abs(earliest.TimeSeconds - (epoch + outwardAfterPeriapsis - 100)) <= 2e-7,
            "scheduler did not choose the earliest staggered crossing");
        var tied = new CoastingEngine(epoch, kerbinMu, new[] {
            Periapsis(5, kerbinMu, semiMajor, eccentricity), Periapsis(2, kerbinMu, semiMajor, eccentricity)
        }, 1);
        RadiusCrossingEvent tie = CoastingEventScheduler.FindFirst(tied, new RadiusCrossingSearch(epoch,
            epoch + outwardAfterPeriapsis + 50, semiMajor, RadiusCrossingDirection.Outward, 1e-7));
        Check(tie != null && tie.BodyId == 2, "equal-time crossing did not use stable body-ID tie break");
        double ellipsePeriod = 2 * Math.PI / meanMotion;
        var longHorizon = new CoastingEngine(epoch, kerbinMu,
            new[] { Periapsis(7, kerbinMu, semiMajor, eccentricity) }, 1);
        RadiusCrossingEvent manyCrossings = CoastingEventScheduler.FindFirst(longHorizon, new RadiusCrossingSearch(epoch,
            epoch + 3.3 * ellipsePeriod, semiMajor, RadiusCrossingDirection.Outward, 1e-7));
        Check(manyCrossings != null && Math.Abs(manyCrossings.TimeSeconds - (epoch + outwardAfterPeriapsis)) <= 2e-7,
            "long horizon with repeated crossings did not select the first event");
        double count64, count512;
        Run(64, 7, 0, out count64); Run(512, 64, 0, out count512);
        Check(one.Evaluations > 2 && count64 >= 0 && count512 >= 0, "event search or benchmark did not execute");
        var circular = new CoastingEngine(0, 3.5316e12, new[] { Periapsis(0, 3.5316e12, 700000, 0) }, 1);
        Check(CoastingEventScheduler.FindFirst(circular, new RadiusCrossingSearch(0, 1000, 710000,
            RadiusCrossingDirection.Outward)) == null, "non-crossing circular orbit produced an event");
        bool rejected = false;
        try { new RadiusCrossingSearch(0, 0, 700000, RadiusCrossingDirection.Outward); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "non-advancing search accepted");
        rejected = false;
        try { new RadiusCrossingSearch(0, 10, 700000, (RadiusCrossingDirection)99, 1); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "unknown crossing direction accepted");
        Console.WriteLine("PASS " + checks + " coasting-event assertions; elapsed ms 1=" + oneMs.ToString("F3") +
            " 64=" + count64.ToString("F3") + " 512=" + count512.ToString("F3"));
        return 0;
    }
}
