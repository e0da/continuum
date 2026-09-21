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

## Compare handoff layouts

The original command and `ksp-continuum-worker-bench/v1` report remain unchanged. The opt-in mode below compares object capture against the core's sealed column capture through the real `SimulationWorker`:

```sh
dotnet run --project tools/KspContinuum.WorkerBench -c Release -- \
  --mode handoff-layout --bodies 1024 --samples 100
```

Schema `ksp-continuum-handoff-layout/v1` uses the same fixed free-body fixture: body `i` has mass 2, position `(i,0,0)`, velocity `(1,2,3)`, force `(0,-4,2)` and a 0.02-second step, in consistent SI units. Both layouts preserve the actual worker stamp and are checked against the analytic result. Body counts and sample bounds remain 1–4096 and 1–10000. Each layout has its own persistent worker and ten unreported warmup submissions. A fixed xorshift seed, 20260921, selects strategy order per pair; the report includes all measured orders.

Raw `endToEndMilliseconds` includes capture/validation, submission, worker execution and envelope checks, caller polling, and indexed result consumption with analytic/identity checks. Fixture preparation, output hashing and report generation are outside timing. SHA-256 covers each output body's ID, mass, position, velocity and force in order using little-endian integer/double encoding; the timestamp/stamp is deliberately excluded because it advances between samples. Every sample and both layouts must produce the same hash. Neither path accesses the lazy `Bodies` compatibility view in this mode.

`callerAllocatedBytes` measures the caller thread from capture through consumption. `backendAllocatedBytes` separately measures `ConstantForceBackend.Compute` on the worker thread. These are allocated bytes, not retained memory. They omit worker queue/envelope overhead outside `Compute`, cancellation dispatch and other process threads. Timing includes the whole handoff even though allocation counters cover only those declared regions. There is no buffer pooling or claim that the worker eliminates all allocation. Median and p95 use nearest rank.

### Initial handoff observation

Three separate ARM64 .NET 8.0.29 launches on September 21, 2026 measured 100 samples per layout. All analytic errors were zero and both layouts produced identical output hashes for each body count.

| Bodies | Layout | Median / p95 ms | Median caller bytes | Median backend bytes |
| ---: | --- | ---: | ---: | ---: |
| 32 | object | 0.017292 / 0.030875 | 5,680 | 5,640 |
| 32 | columns | 0.012292 / 0.029500 | 4,600 | 1,976 |
| 1,024 | object | 0.267667 / 0.297458 | 190,128 | 190,088 |
| 1,024 | columns | 0.118458 / 0.137000 | 145,280 | 49,592 |
| 4,096 | object | 0.506750 / 1.003250 | 782,992 | 782,952 |
| 4,096 | columns | 0.214500 / 0.666333 | 602,928 | 197,048 |

Ignored raw receipts:

- `artifacts/handoff-layout-32-20260921T022227Z.json`, SHA-256 `d1bf1e73af9f3b07be68b57b9f7a090e35b39b220dde282afd8f6e8939eedec4`.
- `artifacts/handoff-layout-1024-20260921T022227Z.json`, SHA-256 `22839a7b926690023fcfbe04736fa8349e3bad395f4328315ac256ab549382ca`.
- `artifacts/handoff-layout-4096-20260921T022227Z.json`, SHA-256 `5d127bf8ef43b33283ad76a81322f82c71bd072a9b253eb5e3e0e36b077a89a3`.

This bounded run supports exposing column storage as an option, while leaving the default unchanged. It does not show behavior with contact/joint solvers, real capture force streams, Unity/Mono, native transport, retained-memory pressure, or a consumer that materializes `Bodies`. System load, JIT, GC and polling influence the observations; one run does not establish a scaling law or stock-vessel speedup.
