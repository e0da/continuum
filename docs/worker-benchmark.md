# Compare compute strategies

Run the portable worker experiment without KSP:

```sh
dotnet run --project tools/KspContinuum.WorkerBench -c Release -- --bodies 1024 --samples 100
```

It emits a JSON report for inline serial, background serial, and background parallel execution of the same independent constant-force point masses. Each strategy uses ten warmup batches and then the requested number of measured batches. Every result is checked against an analytic fixture. Body counts are bounded to 1–4096 and sample counts to 1–10000.

Raw milliseconds span constructing immutable inputs, handoff where applicable, computation, and result consumption. Validation and report serialization are outside the interval. Median uses the lower middle observation and p95 uses nearest rank. The background consumer yields while awaiting completion; the polling cost is part of this particular consumer's result. The worker's idle behavior is tested separately.

The workload has no rotational dynamics, contacts, joints, serialization, native boundary, Unity publication, or stock force callbacks. Strategy order is fixed; these exploratory observations are sensitive to JIT, allocation, scheduling, thermal state, and other running applications. A fast or slow result cannot select a game physics engine or establish stock-vessel speedup. In particular, the work per body is intentionally small enough that scheduling overhead can exceed the arithmetic cost.

## Initial local observation

One ARM64 .NET run on a host reporting 16 logical processors produced these medians and p95 values (milliseconds, 100 measured batches per strategy):

| Bodies | Inline median / p95 | Serial worker median / p95 | Parallel worker median / p95 |
| --- | --- | --- | --- |
| 32 | 0.0072 / 0.0088 | 0.0202 / 0.0301 | 0.0384 / 0.0675 |
| 1024 | 0.1738 / 0.2218 | 0.2247 / 0.3220 | 0.1632 / 0.2571 |
| 4096 | 0.2775 / 0.5490 | 0.3405 / 0.7410 | 0.3268 / 0.9007 |

All fixture position and velocity errors were zero in these runs. The rows are separate process launches, with fixed strategy order within each launch; they are not a controlled scaling curve. This .NET ARM64 process is also different from the installed Intel Unity/Mono game runtime. No ranking or target budget is established by this observation.

The [worker API](simulation-worker.md) owns bounded work and result identity. The [profiling capture](profiling.md) supplies a separate way to measure real flight workloads before choosing what to delegate.
