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
    static RadiusCrossingEvent Run(int count, int batch, double scan, double publication, out double elapsedMilliseconds)
    {
        const double mu = 3.5316e12, semiMajor = 732639.5703, eccentricity = .00888570, epoch = 123456789;
        double meanMotion = Math.Sqrt(mu / (semiMajor * semiMajor * semiMajor));
        double expected = epoch + (Math.PI / 2 - eccentricity) / meanMotion;
        var bodies = new List<CoastingBody>();
        for (int i = 0; i < count; i++) bodies.Add(Periapsis(i, mu, semiMajor, eccentricity, i * 2 * Math.PI / count));
        var engine = new CoastingEngine(epoch, mu, bodies, batch); var clock = Stopwatch.StartNew();
        RadiusCrossingEvent found = CoastingEventScheduler.FindFirst(engine, new RadiusCrossingSearch(epoch, expected + 200,
            semiMajor, RadiusCrossingDirection.Outward, scan, 1e-7));
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
        RadiusCrossingEvent one = Run(1, 1, 113, 3, out oneMs);
        RadiusCrossingEvent seven = Run(1, 7, 113, 17, out sevenMs);
        RadiusCrossingEvent all = Run(1, int.MaxValue, 113, 0, out allMs);
        Check(one.TimeSeconds == seven.TimeSeconds && one.TimeSeconds == all.TimeSeconds,
            "work batch or presentation cadence changed refined event time");
        double alternateScanMs;
        RadiusCrossingEvent alternateScan = Run(1, 1, 251, 0, out alternateScanMs);
        Check(Math.Abs(one.TimeSeconds - alternateScan.TimeSeconds) <= 2e-7,
            "alternate valid scan changed event beyond refinement tolerance");
        const double kerbinMu = 3.5316e12, semiMajor = 732639.5703, eccentricity = .00888570, epoch = 123456789;
        double meanMotion = Math.Sqrt(kerbinMu / (semiMajor * semiMajor * semiMajor));
        double inwardExpected = epoch + (3 * Math.PI / 2 + eccentricity) / meanMotion;
        var inwardEngine = new CoastingEngine(epoch, kerbinMu,
            new[] { Periapsis(3, kerbinMu, semiMajor, eccentricity) }, 1);
        RadiusCrossingEvent inward = CoastingEventScheduler.FindFirst(inwardEngine, new RadiusCrossingSearch(epoch,
            inwardExpected + 100, semiMajor, RadiusCrossingDirection.Inward, 113, 1e-7));
        Check(inward != null && inward.BodyId == 3 && Math.Abs(inward.TimeSeconds - inwardExpected) <= 2e-7,
            "inward crossing missed analytic eccentric-anomaly time");
        double count64, count512;
        Run(64, 7, 113, 0, out count64); Run(512, 64, 113, 0, out count512);
        Check(one.Evaluations > 2 && count64 >= 0 && count512 >= 0, "event search or benchmark did not execute");
        var circular = new CoastingEngine(0, 3.5316e12, new[] { Periapsis(0, 3.5316e12, 700000, 0) }, 1);
        Check(CoastingEventScheduler.FindFirst(circular, new RadiusCrossingSearch(0, 1000, 710000,
            RadiusCrossingDirection.Outward, 10)) == null, "non-crossing circular orbit produced an event");
        bool rejected = false;
        try { new RadiusCrossingSearch(0, 5000, 700000, RadiusCrossingDirection.Outward, 1); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "unbounded scan accepted");
        rejected = false;
        try { new RadiusCrossingSearch(0, 10, 700000, (RadiusCrossingDirection)99, 1); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "unknown crossing direction accepted");
        Console.WriteLine("PASS " + checks + " coasting-event assertions; elapsed ms 1=" + oneMs.ToString("F3") +
            " 64=" + count64.ToString("F3") + " 512=" + count512.ToString("F3"));
        return 0;
    }
}
