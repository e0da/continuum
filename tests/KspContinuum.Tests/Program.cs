using System;
using System.Globalization;
using System.Text.Json;
using KspContinuum;

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
        var dryVessel = new DryBuoyancyVesselState { flightReady = true, active = true, loaded = true, orbiting = true,
            bodyPresent = true, bodyHasOcean = true, altitudeMeters = 100000, vesselBoundMeters = 100, radialSpeedMetersPerSecond = 0, fixedDeltaSeconds = .02 };
        var dryPart = new DryBuoyancyPartState { bodyInitialized = true, bodyMatchesVessel = true, settledDry = true };
        if (DryBuoyancyAdmission.Decide(dryVessel, dryPart) != DryBuoyancyDisposition.SkipStock) throw new Exception("High orbit rejected");
        count++;
        var unsafeVessels = new[] {
            new DryBuoyancyVesselState(),
            new DryBuoyancyVesselState { flightReady = true, active = true, loaded = true, orbiting = true, bodyPresent = true, bodyHasOcean = true,
                altitudeMeters = 1000, vesselBoundMeters = 100, fixedDeltaSeconds = .02 },
            new DryBuoyancyVesselState { flightReady = true, active = true, loaded = true, orbiting = true, bodyPresent = true, bodyHasOcean = true,
                altitudeMeters = 100000, vesselBoundMeters = double.NaN, fixedDeltaSeconds = .02 }
        };
        foreach (var unsafeVessel in unsafeVessels) { if (DryBuoyancyAdmission.Decide(unsafeVessel, dryPart) != DryBuoyancyDisposition.RunStock) throw new Exception("Unsafe vessel admitted"); count++; }
        foreach (var unsafePart in new[] {
            new DryBuoyancyPartState(),
            new DryBuoyancyPartState { bodyInitialized = true, bodyMatchesVessel = true, settledDry = true, splashed = true },
            new DryBuoyancyPartState { bodyInitialized = true, bodyMatchesVessel = true, settledDry = true, depthMeters = .01 }
        }) { if (DryBuoyancyAdmission.Decide(dryVessel, unsafePart) != DryBuoyancyDisposition.RunStock) throw new Exception("Unsafe part admitted"); count++; }
        var swept = dryVessel; swept.altitudeMeters = 10001; swept.radialSpeedMetersPerSecond = -300000;
        if (DryBuoyancyAdmission.Decide(swept, dryPart) != DryBuoyancyDisposition.RunStock) throw new Exception("Swept approach admitted");
        count++;
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
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var report = new BenchReport { samples = new[] { new Sample { boxes = 128, millisecondsPerStep = 0.125 } },
                queuedForceIsolation = new[] { new QueuedForceIsolationSample { strategy = "kinematic-toggle",
                    expectedDeltaVelocity = .1f, observedDeltaVelocity = 0, queuedForceStatus = "cleared" } } };
            using (var json = JsonDocument.Parse(ReportJson.Encode(report)))
            {
                Near(1, json.RootElement.GetProperty("samples").GetArrayLength());
                Near(128, json.RootElement.GetProperty("samples")[0].GetProperty("boxes").GetInt32());
                Near(0.125, json.RootElement.GetProperty("samples")[0].GetProperty("millisecondsPerStep").GetDouble());
                if (json.RootElement.GetProperty("queuedForceIsolation")[0].GetProperty("queuedForceStatus").GetString() != "cleared")
                    throw new Exception("Queued-force result changed");
            }
            var forceRows = new[] {
                new QueuedForceIsolationSample { strategy = "baseline", expectedDeltaVelocity = .1f, observedDeltaVelocity = .1f, queuedForceStatus = "retained" },
                new QueuedForceIsolationSample { strategy = "kinematic-toggle", expectedDeltaVelocity = .1f, observedDeltaVelocity = 0, queuedForceStatus = "cleared" },
                new QueuedForceIsolationSample { strategy = "sleep-wake", expectedDeltaVelocity = .1f, observedDeltaVelocity = .1f, queuedForceStatus = "retained" },
                new QueuedForceIsolationSample { strategy = "velocity-rewrite", expectedDeltaVelocity = .1f, observedDeltaVelocity = .1f, queuedForceStatus = "retained" } };
            if (!new BenchReport { queuedForceIsolation = forceRows }.QueuedForceIsolationPassed())
                throw new Exception("Complete queued-force probe rejected");
            forceRows[3].strategy = "baseline";
            if (new BenchReport { queuedForceIsolation = forceRows }.QueuedForceIsolationPassed())
                throw new Exception("Duplicate queued-force strategy accepted");
            forceRows[3].strategy = "velocity-rewrite";
            forceRows[3].observedDeltaVelocity = float.NaN;
            if (new BenchReport { queuedForceIsolation = forceRows }.QueuedForceIsolationPassed())
                throw new Exception("Nonfinite queued-force result accepted");
            count += 3;
            var text = "quote\" slash\\ newline\n control\u0001 rocket🚀";
            using (var json = JsonDocument.Parse(ReportJson.Encode(new VesselReport {
                inventory = new[] { new PartReport { partType = text, parentIndex = -1 } } })))
            {
                if (json.RootElement.GetProperty("inventory")[0].GetProperty("partType").GetString() != text)
                    throw new Exception("Report string changed");
                count++;
            }
            using (var json = JsonDocument.Parse(ReportJson.Encode(new ProbeReport { markers = new[] {
                new MarkerReport { nanoseconds = new[] { long.MaxValue }, blocks = new int[0] } } })))
            {
                if (json.RootElement.GetProperty("markers")[0].GetProperty("nanoseconds")[0].GetInt64() != long.MaxValue)
                    throw new Exception("Report integer lost precision");
                count++;
                Near(0, json.RootElement.GetProperty("markers")[0].GetProperty("blocks").GetArrayLength());
            }
            using (var json = JsonDocument.Parse(ReportJson.Encode(new ProbeReport { dryBuoyancy = new DryBuoyancyReport {
                status = "complete", calls = 110, bypassed = 109, fallbacks = 1 } })))
            {
                if (json.RootElement.GetProperty("dryBuoyancy").GetProperty("bypassed").GetInt64() != 109)
                    throw new Exception("Dry buoyancy report changed");
                count++;
            }
            using (var json = JsonDocument.Parse(ReportJson.Encode(new NativeBoundaryMonoReport {
                schema = "continuum-native-boundary-mono/v1", samplesPerCase = 101, warmupsPerCase = 12,
                stepSeconds = 1.0 / 60.0, noop = new NativeBoundaryMonoTiming { medianNanoseconds = 42 },
                rows = new[] { new NativeBoundaryMonoRow { bodies = 256, steps = 4,
                    managed = new NativeBoundaryMonoTiming { medianNanoseconds = 900 },
                    native = new NativeBoundaryMonoTiming { medianNanoseconds = 450 }, managedToNativeRatio = 2 } } })))
            {
                if (json.RootElement.GetProperty("schema").GetString() != "continuum-native-boundary-mono/v1" ||
                    json.RootElement.GetProperty("rows")[0].GetProperty("steps").GetInt32() != 4 ||
                    json.RootElement.GetProperty("rows")[0].GetProperty("native").GetProperty("medianNanoseconds").GetDouble() != 450)
                    throw new Exception("Native-boundary Mono receipt changed");
                count++;
            }
            Reject(() => ReportJson.Encode(new Sample { millisecondsPerStep = double.NaN }));
            Reject(() => ReportJson.Encode(new object()));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        count += TimelineTests.Run();
        Console.WriteLine("PASS: " + count + " analytic/report assertions (no Unity or KSP runtime exercised).");
    }
}
