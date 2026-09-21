using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KspContinuum;

static class Program
{
    const double Horizon = 20;
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
    static EncounterMotion Motion(int id, Vec position, Vec velocity, double radius = 1,
        double lookahead = Horizon, double? acceleration = 0, long generation = 1, string frame = "inertial-fixture")
    {
        return new EncounterMotion(id, generation, 0, frame, position, velocity, radius, Horizon, lookahead, 0, 0, acceleration);
    }
    static EncounterMotion[] Quiet(int count)
    {
        return Enumerable.Range(0, count).Select(i => Motion(i + 3, new Vec(1000000 + 100 * i, 1000, 0), new Vec())).ToArray();
    }
    static EncounterMotion[] Crossing()
    {
        return new[] { Motion(1, new Vec(-50000, 0, 0), new Vec(5000, 0, 0), 10),
                       Motion(2, new Vec(50000, 0, 0), new Vec(-5000, 0, 0), 10) };
    }
    static string Status(EncounterPlan plan)
    {
        switch (plan.Status)
        {
            case EncounterPlanStatus.Complete: return "complete";
            case EncounterPlanStatus.BudgetExhausted: return "budget-exhausted";
            case EncounterPlanStatus.UnknownBounds: return "unknown-bounds";
            default: return "numerical-uncertainty";
        }
    }
    static object Semantic(EncounterPlan plan)
    {
        return new {
            status = Status(plan), detail = plan.Detail, epoch = plan.Epoch, frame = plan.Frame, horizonSeconds = plan.HorizonSeconds,
            candidates = plan.Candidates.Select(c => new { firstId = c.FirstId, secondId = c.SecondId, firstGeneration = c.FirstGeneration, secondGeneration = c.SecondGeneration,
                lowerSeconds = c.LowerSeconds, upperSeconds = c.UpperSeconds }).ToArray(),
            advances = plan.Advances.Select(a => new { id = a.ObjectId, groupId = a.GroupId, seconds = a.SafeAdvanceSeconds }).ToArray(),
            groups = plan.Groups.Select(g => new { id = g.Id, ids = g.ObjectIds.ToArray(), seconds = g.SafeAdvanceSeconds }).ToArray(),
            work = new { sweepAxis = plan.WorkUsed.SweepAxis, pairTests = plan.WorkUsed.PairTests, candidates = plan.WorkUsed.BroadphaseCandidates,
                intervalTests = plan.WorkUsed.IntervalTests }
        };
    }
    static string Hash(EncounterPlan plan)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Semantic(plan))))).ToLowerInvariant();
    }
    static object Row(string name, EncounterPlan plan, EncounterMotion[] inputs)
    {
        return new { name, status = Status(plan), bodyCount = plan.Advances.Count,
            inputs = inputs.Select(m => new { id = m.Id, generation = m.Generation, epoch = m.Epoch, frame = m.Frame,
                position = new[] { m.Position.X, m.Position.Y, m.Position.Z }, velocity = new[] { m.Velocity.X, m.Velocity.Y, m.Velocity.Z },
                radius = m.Radius, validForSeconds = m.ValidForSeconds, lookaheadSeconds = m.LookaheadSeconds,
                positionError = m.PositionError, velocityError = m.VelocityError, accelerationBound = m.AccelerationBound }).ToArray(),
            fullHorizonBodies = plan.Advances.Count(a => a.SafeAdvanceSeconds == Horizon),
            refinedBodies = plan.Advances.Count(a => a.SafeAdvanceSeconds < Horizon),
            minimumAdvanceSeconds = plan.Advances.Min(a => a.SafeAdvanceSeconds),
            maximumAdvanceSeconds = plan.Advances.Max(a => a.SafeAdvanceSeconds),
            largestGroup = plan.Groups.Max(g => g.ObjectIds.Count),
            allPairs = (long)plan.Advances.Count * (plan.Advances.Count - 1) / 2,
            semanticSha256 = Hash(plan), plan = Semantic(plan) };
    }
    static EncounterPlan Plan(EncounterMotion[] motions, EncounterBudget budget = null)
    {
        return EncounterPlanner.Plan(motions, Horizon, budget ?? new EncounterBudget());
    }
    static object Measure(string name, EncounterMotion[] motions, int samples, EncounterBudget budget = null)
    {
        Plan(motions, budget);
        var milliseconds = new double[samples]; var allocations = new long[samples];
        string signature = null;
        for (int i = 0; i < samples; i++)
        {
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            EncounterPlan plan = Plan(motions, budget);
            clock.Stop();
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            milliseconds[i] = clock.Elapsed.TotalMilliseconds;
            string current = Hash(plan);
            if (signature != null && signature != current) throw new InvalidOperationException("Repeated plan changed semantic result.");
            signature = current;
        }
        return new { name, bodyCount = motions.Length, samples, milliseconds, callerAllocatedBytes = allocations,
            semanticSha256 = signature };
    }
    static int Main(string[] args)
    {
        try
        {
            int quiet = 1024, samples = 5; string output = null;
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("Options require values.");
                if (args[i] == "--output") { output = args[i + 1]; continue; }
                int value;
                if (!int.TryParse(args[i + 1], out value)) throw new ArgumentException("Workload options require integers.");
                if (args[i] == "--quiet" && value >= 1 && value <= 4094) quiet = value;
                else if (args[i] == "--samples" && value >= 1 && value <= 32) samples = value;
                else throw new ArgumentException("Use --quiet 1..4094, --samples 1..32, --output NEW_PATH.");
            }
            if (output == null || File.Exists(output) || Directory.Exists(output)) throw new ArgumentException("Output must be a new file path.");
            var sparse = Quiet(quiet);
            var mixed = Crossing().Concat(sparse).ToArray();
            var crossAxis = Enumerable.Range(0, quiet).Select(i => Motion(i + 3, new Vec(1000000, 1000 + 100 * i, 0), new Vec())).ToArray();
            var tangent = new[] { Motion(1, new Vec(-10, 2, 0), new Vec(1, 0, 0)), Motion(2, new Vec(), new Vec()) };
            var miss = new[] { Motion(1, new Vec(-10, 2.001, 0), new Vec(1, 0, 0)), Motion(2, new Vec(), new Vec()) };
            var accelerating = new[] { Motion(1, new Vec(), new Vec(), acceleration: 4), Motion(2, new Vec(10, 0, 0), new Vec()) };
            var blocked = new[] { Motion(1, new Vec(), new Vec(), lookahead: 0) };
            var unknown = new[] { Motion(1, new Vec(), new Vec(), acceleration: null), Motion(2, new Vec(1000, 0, 0), new Vec()) };
            var dense = Enumerable.Range(1, 64).Select(id => Motion(id, new Vec(), new Vec())).ToArray();
            var smallBudget = new EncounterBudget(maxPairTests: 128, maxCandidates: 64, maxIntervalTests: 2048);
            var pCrossAxis = Plan(crossAxis);
            var pSparse = Plan(sparse); var pMixed = Plan(mixed); var pTangent = Plan(tangent); var pMiss = Plan(miss);
            var pAcceleration = Plan(accelerating); var pBlocked = Plan(blocked); var pUnknown = Plan(unknown);
            var pDense = Plan(dense, smallBudget);
            bool permutationStable = Hash(pMixed) == Hash(Plan(mixed.Reverse().ToArray())) && Hash(pDense) == Hash(Plan(dense.Reverse().ToArray(), smallBudget));
            var scheduler = new EncounterScheduler(); scheduler.Replace(mixed);
            var before = scheduler.Plan(Horizon, new EncounterBudget());
            bool initiallyCurrent = scheduler.IsCurrent(before);
            scheduler.Replace(Quiet(quiet));
            bool stalePlanRejected = !scheduler.IsCurrent(before);
            scheduler.Replace(mixed);
            bool noResurrection = !scheduler.IsCurrent(before);
            var another = new EncounterScheduler(); another.Replace(mixed);
            bool foreignPlanRejected = !another.IsCurrent(before);
            var newPlan = scheduler.Plan(Horizon, new EncounterBudget());
            bool replacementAccepted = scheduler.IsCurrent(newPlan);
            var debrisScheduler = new EncounterScheduler(); debrisScheduler.Replace(new[] { Motion(1, new Vec(), new Vec()) });
            var beforeDebris = debrisScheduler.Plan(Horizon, new EncounterBudget());
            var debris = new[] { Motion(1, new Vec(), new Vec()), Motion(2, new Vec(10, 0, 0), new Vec(-1, 0, 0)) };
            debrisScheduler.Replace(debris);
            var afterDebris = debrisScheduler.Plan(Horizon, new EncounterBudget());
            bool debrisRescreened = !debrisScheduler.IsCurrent(beforeDebris) && afterDebris.Advances.All(a => a.SafeAdvanceSeconds <= 8);
            bool frameMismatchRejected = false;
            try { Plan(new[] { Motion(1, new Vec(), new Vec()), Motion(2, new Vec(), new Vec(), frame: "another-frame") }); }
            catch (ArgumentException) { frameMismatchRejected = true; }
            var checks = new SortedDictionary<string, bool> {
                ["sparseOrientationPruned"] = Status(pCrossAxis) == "complete" && pCrossAxis.Advances.All(a => a.SafeAdvanceSeconds == Horizon) && pCrossAxis.WorkUsed.PairTests < quiet * 4L,
                ["sparseIsClear"] = Status(pSparse) == "complete" && pSparse.Advances.All(a => a.SafeAdvanceSeconds == Horizon),
                ["crossingStopsBeforeAnalyticContact"] = Status(pMixed) == "complete" && pMixed.Advances.Where(a => a.ObjectId <= 2).All(a => a.SafeAdvanceSeconds <= 9.998 && a.SafeAdvanceSeconds >= 9.9979),
                ["quietBodiesKeepFullHorizon"] = pMixed.Advances.Where(a => a.ObjectId > 2).All(a => a.SafeAdvanceSeconds == Horizon),
                ["tangentIsNotMissed"] = pTangent.Candidates.Count == 1 && pTangent.Advances.All(a => a.SafeAdvanceSeconds <= 10 && a.SafeAdvanceSeconds > 9.99),
                ["nearMissIsClear"] = pMiss.Candidates.Count == 0 && pMiss.Advances.All(a => a.SafeAdvanceSeconds == Horizon),
                ["uncertainAccelerationStopsBeforePossibleContact"] = pAcceleration.Advances.All(a => a.SafeAdvanceSeconds <= 2),
                ["zeroLookaheadStops"] = pBlocked.Advances.All(a => a.SafeAdvanceSeconds == 0),
                ["unknownBoundsStopAll"] = Status(pUnknown) == "unknown-bounds" && pUnknown.Advances.All(a => a.SafeAdvanceSeconds == 0),
                ["denseExhaustionStopsAll"] = Status(pDense) == "budget-exhausted" && pDense.Advances.All(a => a.SafeAdvanceSeconds == 0),
                ["deterministicPermutation"] = permutationStable, ["staleRevisionsRejected"] = initiallyCurrent && stalePlanRejected && noResurrection && replacementAccepted,
                ["foreignPlansRejected"] = foreignPlanRejected, ["debrisRescreened"] = debrisRescreened, ["frameMismatchRejected"] = frameMismatchRejected
            };
            var report = new { schema = "ksp-continuum-encounter-bench/v1", qualified = checks.Values.All(x => x),
                createdUtc = DateTime.UtcNow.ToString("o"), quietBodies = quiet, horizonSeconds = Horizon,
                physicsIntegrated = false, parallelExecutionQualified = false, stockPhysicsSpeedupMeasured = false,
                permutationStable, stalePlanRejected, foreignPlanRejected, checks,
                model = "Prescribed linear centers, spherical bounds and declared residual acceleration; plans only, no integration or contact response.",
                measurement = "One warmup per scene; fixed scene order; full synchronous Plan call including validation and allocation; excludes input construction, hashing, serialization and startup. Single-process wall times, not a stock-game speedup.",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
                cases = new[] { Row("sparse", pSparse, sparse), Row("sparse-cross-axis", pCrossAxis, crossAxis), Row("mixed-fast-crossing", pMixed, mixed), Row("tangent", pTangent, tangent), Row("near-miss", pMiss, miss),
                    Row("acceleration-uncertainty", pAcceleration, accelerating), Row("zero-lookahead", pBlocked, blocked), Row("unknown-bounds", pUnknown, unknown), Row("dense-budget-exhaustion", pDense, dense), Row("inserted-debris", afterDebris, debris) },
                measurements = new[] { Measure("sparse", sparse, samples), Measure("sparse-cross-axis", crossAxis, samples), Measure("mixed-fast-crossing", mixed, samples), Measure("dense-budget-exhaustion", dense, samples, smallBudget) }
            };
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, report, Json);
            Console.WriteLine(JsonSerializer.Serialize(new { report.qualified, output }));
            return report.qualified ? 0 : 2;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
