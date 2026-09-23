# Multi-core fleet stepping benchmark

`KspContinuum.FleetBench` measures independent Continuum-owned vessel work across one through sixteen workers. Each vessel owns a persistent `CoastingEngine`. Workloads sample one, eight, or thirty-two future states per vessel and apply the real two-object `EncounterPlanner` to zero, one quarter, or all vessels. Fleet sizes range from 16 to 4,096.

Every worker count writes results into stable input-index slots and must reproduce the serial SHA-256 output hash. Each row performs three warmups, calibrates repeated passes to at least 20 ms, then reports median and p95 wall time, throughput, and process-wide allocated bytes. Allocation includes worker-thread activity and is not retained memory. `--quick --samples 3` runs a reduced CI matrix; the full seven-sample run is:

```sh
DOTNET_TieredCompilation=0 dotnet run --project tools/KspContinuum.FleetBench -c Release -- --samples 7
```

The process refuses to run without `DOTNET_TieredCompilation=0`. A fixed ascending strategy order in the first experiment let later worker counts inherit hotter tiered-JIT code; one 16-vessel row changed from an apparent 24.0x to 3.08x when tiering was disabled. The corrected protocol disables tier promotion, prewarms every worker count, calibrates all strategies before measurement, rotates their order between samples, and records a trailing serial sentinel.

## First ARM64 observation

The corrected held-out full run covered 225 rows on a .NET 8 process reporting 16 logical processors. All 45 workload configurations produced identical ordered hashes at every worker count. Parallel execution beat both the interleaved serial median and the trailing serial sentinel in every configuration. The table uses the smaller of those two serial measurements as its baseline. On that conservative basis, the minimum observed best-worker speedup was 2.37x and the maximum was 9.66x.

| Vessels | Median best-worker speedup across nine workloads | Range | Most common best worker count |
| ---: | ---: | ---: | ---: |
| 16 | 4.32x | 2.37x–6.61x | 16 |
| 64 | 7.63x | 3.27x–8.95x | 16 |
| 256 | 7.92x | 4.71x–9.66x | 16 |
| 1,024 | 6.69x | 4.73x–8.00x | 16 |
| 4,096 | 6.19x | 5.15x–8.34x | 16 |

At 4,096 vessels and one trajectory sample with no event prediction, the interleaved serial median was 5.88 ms, the trailing serial sentinel was 5.43 ms, and the 16-worker median was 1.05 ms. With event prediction on every vessel they were 32.68 ms, 32.34 ms, and 3.92 ms. At 32 samples per vessel with events everywhere they were 187.88 ms, 215.33 ms, and 28.14 ms.

The result supports parallel execution of independent vessel batches and preserving deterministic publication order through indexed output. It does not yet select a production scheduler. `Parallel.For` supplied the worker pool for this experiment; cancellation, failure isolation, persistent partitioning, nested parallelism and live KSP/Mono costs remain unqualified. The trailing sentinel ranged from 0.54x to 1.41x of the interleaved serial median, so host drift remains material even after removing tier-promotion bias; the conservative baseline limits, rather than eliminates, that uncertainty. Allocation is the immediate measured concern: the 4,096-vessel, 32-sample rows allocated roughly 274–278 MB per fleet pass at every worker count, mostly from repeated immutable trajectory results. Reducing that cost is a better next production experiment than wrapping this benchmark in a general scheduler.

Raw held-out receipt: `0c2ea9f1a634ce8ae7cfd68c89c00018603b5b3562253c82a92cb1a79a54b7bb` (ignored local artifact). Results are host- and runtime-specific and are not a KSP frame-rate claim. The earlier fixed-order receipt and its 1.46x–10.16x range are retracted because the protocol did not isolate tiered-JIT maturity from worker count.
