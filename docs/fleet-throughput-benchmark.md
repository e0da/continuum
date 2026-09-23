# Multi-core fleet stepping benchmark

`KspContinuum.FleetBench` measures independent Continuum-owned vessel work across one through sixteen workers. Each vessel owns a persistent `CoastingEngine`. Workloads sample one, eight, or thirty-two future states per vessel and apply the real two-object `EncounterPlanner` to zero, one quarter, or all vessels. Fleet sizes range from 16 to 4,096.

Every worker count writes results into stable input-index slots and must reproduce the serial SHA-256 output hash. Each row performs three warmups, calibrates repeated passes to at least 20 ms, then reports median and p95 wall time, throughput, and process-wide allocated bytes. Allocation includes worker-thread activity and is not retained memory. `--quick --samples 3` runs a reduced CI matrix; the full seven-sample run is:

```sh
dotnet run --project tools/KspContinuum.FleetBench -c Release -- --samples 7
```

## First ARM64 observation

The held-out full run covered 225 rows on a .NET 8 process reporting 16 logical processors. All 45 workload configurations produced identical ordered hashes at every worker count. Parallel execution beat the serial median in every configuration. The minimum observed best-worker speedup was 1.46x; the maximum was 10.16x.

| Vessels | Median best-worker speedup across nine workloads | Range | Most common best worker count |
| ---: | ---: | ---: | ---: |
| 16 | 4.85x | 1.46x–10.16x | 16 |
| 64 | 7.11x | 3.62x–8.35x | 16 |
| 256 | 7.44x | 5.80x–8.77x | 16 |
| 1,024 | 5.38x | 4.87x–9.24x | 16 |
| 4,096 | 6.36x | 5.01x–9.88x | 16 |

At 4,096 vessels and one trajectory sample with no event prediction, the serial median was 6.25 ms and the 16-worker median was 1.56 ms. With event prediction on every vessel they were 31.17 ms and 3.61 ms. At 32 samples per vessel with events everywhere they were 236.57 ms and 33.95 ms.

The result supports parallel execution of independent vessel batches and preserving deterministic publication order through indexed output. It does not yet select a production scheduler. `Parallel.For` supplied the worker pool for this experiment; cancellation, failure isolation, persistent partitioning, nested parallelism and live KSP/Mono costs remain unqualified. Allocation is the immediate measured concern: the 4,096-vessel, 32-sample rows allocated roughly 262–279 MB per fleet pass at every worker count, mostly from repeated immutable trajectory results. Reducing that cost is a better next production experiment than wrapping this benchmark in a general scheduler.

Raw held-out receipt: `b175375f5d70470a1f9e13b4b337766a5dbe2910f9d92fa627da38c0e8c56194` (ignored local artifact). Results are host- and runtime-specific and are not a KSP frame-rate claim.
