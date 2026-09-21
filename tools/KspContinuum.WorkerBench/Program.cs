using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KspContinuum;

// This consumer measures reference arithmetic and worker handoff, not KSP physics.
sealed class ParallelBackend : ISimulationBackend
{
    public SimulationBatch Compute(SimulationBatch batch, CancellationToken cancellation)
    {
        var output = new SimulationBody[batch.Bodies.Count];
        Parallel.For(0, output.Length, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            var b = batch.Bodies[i];
            var a = b.Force * (1 / b.Mass);
            double dt = batch.StepSeconds;
            output[i] = new SimulationBody(b.Id, b.Mass,
                b.Position + b.Velocity * dt + a * (0.5 * dt * dt),
                b.Velocity + a * dt, b.Force);
        });
        return new SimulationBatch(batch.Stamp, batch.StepSeconds, output);
    }
}

static class Program
{
    const double Dt = 0.02;
    static int Main(string[] args)
    {
        try
        {
            int bodies = 1024, samples = 100;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !int.TryParse(args[i + 1], out int value))
                    throw new ArgumentException("Use --bodies 1..4096 and --samples 1..10000.");
                if (args[i] == "--bodies" && value >= 1 && value <= SimulationBatch.MaxBodies) bodies = value;
                else if (args[i] == "--samples" && value >= 1 && value <= 10000) samples = value;
                else throw new ArgumentException("Invalid bodies/samples option or bound.");
            }
            var results = new List<object>();
            var serial = new ConstantForceBackend();
            results.Add(Measure("serial-inline", bodies, samples, b => serial.Compute(b, CancellationToken.None)));
            using (var worker = new SimulationWorker(serial))
                results.Add(Measure("serial-worker", bodies, samples, b => Execute(worker, b)));
            using (var worker = new SimulationWorker(new ParallelBackend()))
                results.Add(Measure("parallel-worker", bodies, samples, b => Execute(worker, b)));
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema = "ksp-continuum-worker-bench/v1", bodies, samples,
                warmupSamples = 10, stepSeconds = Dt,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                measurement = "batch construction through result consumption; validation outside timing; fixed strategy order",
                workload = "independent constant-force point masses; no rotation, joints, contacts or game objects",
                stockPhysicsSpeedupMeasured = false, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    static SimulationBatch Execute(SimulationWorker worker, SimulationBatch batch)
    {
        if (worker.TrySubmit(batch) != SubmitStatus.Accepted)
            throw new InvalidOperationException("Worker rejected sequential benchmark submission.");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var status = worker.TryTake(batch.Stamp, out SimulationBatch result);
            if (status == ResultStatus.Ready) return result;
            if (status != ResultStatus.Pending)
                throw new InvalidOperationException("Worker result: " + status, worker.Fault);
            if (deadline.Elapsed.TotalSeconds > 10) throw new TimeoutException("Worker did not complete within 10 seconds.");
            Thread.Yield();
        }
    }

    static object Measure(string strategy, int count, int samples, Func<SimulationBatch, SimulationBatch> compute)
    {
        var milliseconds = new double[samples];
        double positionError = 0, velocityError = 0;
        for (int sample = -10; sample < samples; sample++)
        {
            var watch = Stopwatch.StartNew();
            var input = new SimulationBody[count];
            for (int i = 0; i < count; i++)
                input[i] = new SimulationBody(i, 2, new Vec(i, 0, 0), new Vec(1, 2, 3), new Vec(0, -4, 2));
            var batch = new SimulationBatch(new WorkStamp(sample + 10, 0, 0), Dt, input);
            var output = compute(batch);
            watch.Stop();
            if (sample >= 0) milliseconds[sample] = watch.Elapsed.TotalMilliseconds;
            if (output.Bodies.Count != count) throw new InvalidOperationException("Wrong output body count.");
            for (int i = 0; i < count; i++)
            {
                var b = output.Bodies[i];
                // Independent numeric oracle for this fixture, not a second call to the backend.
                positionError = Math.Max(positionError, MaxError(b.Position, new Vec(i + 0.02, 0.0396, 0.0602)));
                velocityError = Math.Max(velocityError, MaxError(b.Velocity, new Vec(1, 1.96, 3.02)));
                if (b.Id != i) throw new InvalidOperationException("Reordered body identity.");
            }
        }
        if (positionError > 1e-10 || velocityError > 1e-10)
            throw new InvalidOperationException("Reference motion disagrees with the analytic fixture.");
        var sorted = milliseconds.OrderBy(x => x).ToArray();
        return new { strategy, milliseconds, medianMilliseconds = sorted[(sorted.Length - 1) / 2],
            p95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
            maxPositionError = positionError, maxVelocityError = velocityError };
    }

    static double MaxError(Vec a, Vec b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
}
