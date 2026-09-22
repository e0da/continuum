using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using KspContinuum;

static class Program
{
    sealed class Row
    {
        public int parts { get; set; }
        public int repetitions { get; set; }
        public double scalarMilliseconds { get; set; }
        public double parallelMilliseconds { get; set; }
        public double parallelToScalarRatio { get; set; }
        public bool bitwiseEquivalent { get; set; }
    }
    sealed class Report
    {
        public string schema { get; set; } = "ksp-continuum-aero-baseline-bench/v1";
        public string strategy { get; set; } = AeroDragCubeBaseline.Strategy;
        public int parallelism { get; set; }
        public string interpretation { get; set; } = "Process-local elapsed times include result allocation and scheduling; they are comparative observations, not a speedup qualification.";
        public Row[] workloads { get; set; } = Array.Empty<Row>();
    }

    static AeroCaptureContext Step(int ordinal) => new AeroCaptureContext("00000000-0000-0000-0000-000000000001",
        "00000000-0000-0000-0000-000000000002", "bench", 1, 1, 1, 1, 1, ordinal, 0, 0, .02);
    static AeroPartContext Part(int index)
    {
        double angle = index * .017;
        Rotation rotation = Rotation.AxisAngle(new Vec(1, 2, 3), angle);
        var cube = new AeroDragCubeState("bench", 1, new Vec(.1, -.2, .05), new Vec(1, 1, 1),
            new[] { 1d, 1.1, .8, .9, 1.2, .7 }, new[] { .3, .35, .25, .28, .4, .22 },
            new[] { 1d, 1, 1, 1, 1, 1 }, new[] { 1d, .95, .9, .85, .8, .75 });
        return new AeroPartContext(Step(index % 256), index + 1, index + 100, index + 200, 10, 1.2, 101325, 288,
            340, .7, 1, 1, false, new Vec(index, 0, 0), new Vec(100, 0, 0),
            rotation.Rotate(new Vec(-100, index % 9 - 4, index % 7 - 3)), new Vec(),
            new Vec(rotation.X, rotation.Y, rotation.Z), rotation.W, new[] { cube }, SetDragInputs());
    }
    static AeroSetDragInputs SetDragInputs()
    {
        var curve = new AeroFloatCurveDefinition(0, 0, new[] { new AeroCurveKey(0, 1, 0, 0, 0, 0, 0) });
        return new AeroSetDragInputs(new double[6], new double[6], new AeroSurfaceCurveDefinitions(curve, curve, curve, curve), curve, curve);
    }
    static bool Same(AeroBaselineResult[] left, AeroBaselineResult[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index].ForceNewtons.X != right[index].ForceNewtons.X || left[index].ForceNewtons.Y != right[index].ForceNewtons.Y ||
                left[index].ForceNewtons.Z != right[index].ForceNewtons.Z ||
                left[index].TorqueAboutPartCenterOfMassNewtonMeters.X != right[index].TorqueAboutPartCenterOfMassNewtonMeters.X ||
                left[index].TorqueAboutPartCenterOfMassNewtonMeters.Y != right[index].TorqueAboutPartCenterOfMassNewtonMeters.Y ||
                left[index].TorqueAboutPartCenterOfMassNewtonMeters.Z != right[index].TorqueAboutPartCenterOfMassNewtonMeters.Z) return false;
        return true;
    }
    static double Time(int repetitions, Func<AeroBaselineResult[]> operation, out AeroBaselineResult[] result)
    {
        operation();
        var stopwatch = Stopwatch.StartNew();
        result = Array.Empty<AeroBaselineResult>();
        for (int repetition = 0; repetition < repetitions; repetition++) result = operation();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
    static int Main(string[] arguments)
    {
        int parallelism = Environment.ProcessorCount, repetitions = 50;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == "--parallelism" && index + 1 < arguments.Length) parallelism = int.Parse(arguments[++index]);
            else if (arguments[index] == "--repetitions" && index + 1 < arguments.Length) repetitions = int.Parse(arguments[++index]);
            else throw new ArgumentException("usage: [--parallelism N] [--repetitions N]");
        }
        if (parallelism < 1 || repetitions < 1) throw new ArgumentOutOfRangeException();
        var rows = new List<Row>();
        foreach (int count in new[] { 1, 32, 4096 })
        {
            var inputs = new AeroPartContext[count];
            for (int index = 0; index < count; index++) inputs[index] = Part(index);
            AeroBaselineResult[] scalar, parallel;
            double scalarMs = Time(repetitions, () => AeroDragCubeBaseline.EvaluateScalar(inputs), out scalar);
            double parallelMs = Time(repetitions, () => AeroDragCubeBaseline.EvaluateParallel(inputs, parallelism), out parallel);
            rows.Add(new Row { parts = count, repetitions = repetitions, scalarMilliseconds = scalarMs,
                parallelMilliseconds = parallelMs, parallelToScalarRatio = parallelMs / scalarMs,
                bitwiseEquivalent = Same(scalar, parallel) });
        }
        Console.WriteLine(JsonSerializer.Serialize(new Report { parallelism = parallelism, workloads = rows.ToArray() },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
