# Data layout benchmark

Each result now includes a `continuum-performance-observation/v1` record. It binds raw samples to the system, workload, fixture hash, item count, step count, and strategy. The fixed phases are `capture`, `pack`, `compute`, `synchronize`, `publish`, and directly measured `total`; every phase records elapsed milliseconds and current-thread allocated bytes for the same randomized samples. An in-process synchronization phase can be near the timer floor, but remains explicit so native, device, and sidecar strategies can report their real synchronization cost without changing the report shape.

`PerformanceObservations.Compare` compares total medians only when workload identities match and returns speedup, allocation ratio, and a caller-selected maximum-regression verdict. It does not add phase timings to manufacture a total, set a universal threshold, or claim stock KSP performance. Machine and runtime identity remains owned by the surrounding benchmark report.

`KspContinuum.LayoutBench` is a bounded synthetic experiment for deciding whether a different simulation capture layout deserves further testing. It compares the current immutable object representation with two private experimental representations:

- `object-aos` builds the existing `SimulationBody` and `SimulationBatch` objects for both input and output.
- `soa` stores each body field in a separate sealed array-backed capture.
- `aosoa-8` stores fields in fixed blocks of eight lanes, with a separate stable ID array.

Each representation owns a defensive copy of its logical input and allocates a distinct output. The experiment does not pool captures or expose a reusable worker API.

## Run it

Run the complete bounded matrix in Release mode:

```sh
dotnet run --project tools/KspContinuum.LayoutBench -c Release -- \
  --bodies 32,1024,4096 --samples 100 --seed 20260921 \
  > artifacts/layout-benchmark-20260921.json
```

`--bodies` must be a unique subset of `32,1024,4096`. `--samples` is limited to `1..1000`. `--seed` is a nonnegative 32-bit integer. The defaults are the values shown above. The runner performs ten unreported warm-up samples per workload, then uses the seed to shuffle the three strategies before every measured sample. The same seed reproduces the strategy orders and evidence hashes; elapsed time and allocation observations remain runtime measurements.

The JSON schema is `ksp-continuum-layout-bench/v1`. Each body-count case contains the fixture SHA-256, measured strategy order, and raw sample arrays for these phases:

- `packing`: copy and validate the shared fixture into the selected immutable representation.
- `kernel`: compute a new immutable logical output.
- `consume`: check identity and numeric agreement, then compute a canonical output SHA-256.
- `endToEnd`: capture-through-consume elapsed time and allocation.

Allocation is from `GC.GetAllocatedBytesForCurrentThread`; this is the measuring thread's managed allocation, not retained memory or whole-process allocation. End-to-end allocation includes measurement objects created between the start and final counter reads. Timings use `Stopwatch` and are reported in milliseconds. Configuration and environment records each have a canonical SHA-256 so results can be associated with their declared setup.

## Workloads and agreement

Both kernels operate on the same deterministic fixture and use a `0.02` second step:

- `free-body` integrates position and velocity from each body's constant force and mass.
- `neighbor-chain` adds a fixed coupling from the immediately adjacent positions on each axis before performing the same integration. An endpoint reads itself for its missing neighbor.

A shared scalar reference result checks every body ID and all output position and velocity components. It uses the same acceleration and integration functions as the three layouts, so it verifies representation agreement rather than serving as an independent numerical oracle. The canonical output hash serializes each ID followed by its six result doubles in body order. A run fails if a strategy changes identity or order, produces inconsistent sample hashes, or violates the capture input contract. The report also records maximum absolute position and velocity error, and matching hashes across strategies make bit-for-bit equality visible.

## Recorded experiment

The September 21, 2026 run used .NET 8.0.29 on ARM64 Darwin 25.5.0 with 16 logical processors. Its ignored raw report is `artifacts/layout-benchmark-20260921-v2.json`, SHA-256 `e2c6c9a021ea823aee32d019694ca6bc7caf4f860d8887682b4f6c1300961f14`. All strategies produced the same output hash for each workload and body count; maximum reported numeric error was zero.

The table summarizes 100 end-to-end samples. Median allocation is in bytes. P95 is nearest-rank sample 95 after sorting the 100 elapsed observations.

| Bodies | Workload | Layout | Median ms | P95 ms | Median allocated bytes |
| ---: | --- | --- | ---: | ---: | ---: |
| 32 | free-body | object-aos | 0.013521 | 0.017667 | 13,912 |
| 32 | free-body | soa | 0.007687 | 0.009958 | 8,848 |
| 32 | free-body | aosoa-8 | 0.010604 | 0.013208 | 8,408 |
| 32 | neighbor-chain | object-aos | 0.013958 | 0.017625 | 13,912 |
| 32 | neighbor-chain | soa | 0.007938 | 0.010000 | 8,848 |
| 32 | neighbor-chain | aosoa-8 | 0.010750 | 0.013291 | 8,408 |
| 1,024 | free-body | object-aos | 0.385479 | 0.475083 | 446,296 |
| 1,024 | free-body | soa | 0.209167 | 0.233292 | 264,600 |
| 1,024 | free-body | aosoa-8 | 0.295458 | 0.345750 | 264,160 |
| 1,024 | neighbor-chain | object-aos | 0.395083 | 0.504084 | 446,296 |
| 1,024 | neighbor-chain | soa | 0.205375 | 0.246208 | 264,600 |
| 1,024 | neighbor-chain | aosoa-8 | 0.299854 | 0.452750 | 264,160 |
| 4,096 | free-body | object-aos | 0.546417 | 1.261875 | 1,828,632 |
| 4,096 | free-body | soa | 0.373188 | 0.817250 | 1,078,600 |
| 4,096 | free-body | aosoa-8 | 0.379521 | 0.819917 | 1,078,160 |
| 4,096 | neighbor-chain | object-aos | 0.337458 | 0.735792 | 1,828,632 |
| 4,096 | neighbor-chain | soa | 0.287605 | 0.564750 | 1,078,600 |
| 4,096 | neighbor-chain | aosoa-8 | 0.270521 | 0.664750 | 1,078,160 |

On this run, SoA had the lowest median at 32 and 1,024 bodies. At 4,096 bodies, SoA and AoSoA were close and the winner depended on the workload. Both array layouts allocated less than the object representation. AoSoA did not show a consistent advantage over SoA, so these results do not justify adding production AoSoA machinery.

This is one single-process synthetic measurement, not a stock-physics or KSP gameplay benchmark. It does not exercise joints, contacts, topology changes, Unity/Mono, x86, a native sidecar, or worker handoff. Validation and result hashing are included by design, and the existing object constructors perform their current validation. JIT, GC, scheduling, and thermal effects remain; workload and body-count order are fixed even though strategy order is shuffled. The non-linear timings between body counts show why this run should be treated as a direction signal rather than a scaling model. A production layout decision requires a runtime-specific experiment at the actual handoff boundary. The current result supports measuring sealed SoA there first while retaining the object contract as the correctness oracle.

The subsequent [worker handoff comparison](worker-benchmark.md#compare-handoff-layouts) measures an opt-in sealed column capture through the actual managed worker. It retains the object API and default storage. That follow-up does not retroactively make this private-layout experiment a worker or game benchmark.
