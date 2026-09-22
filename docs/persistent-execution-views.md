# Persistent execution views

Continuum can retain a compiled structure-of-arrays execution view across simulation steps. `EntityKey(slot, generation)` separates stable identity from dense row number, and a slot-indexed canonical world makes dirty lookup constant-time. A refresh with an unchanged topology revision copies only declared dirty rows. Adding, removing, or recycling an entity changes the topology revision and rebuilds the view in deterministic slot order. A recycled slot cannot accept a stale generation.

The experiment reuses the existing scalar and Rayon integration kernels. It does not add a new solver, ECS scheduler, KSP adapter, unsafe code, native ABI, or GPU path. Exact tests compare every SoA column before computation and after eight scalar steps; the same persistent input must also produce exactly equal scalar and CPU-parallel results.

Run the benchmark through the repository task runner, using a new output path:

```console
cargo run --manifest-path tools/xtask/Cargo.toml -- persistent-execution-bench artifacts/persistent-execution-view-NEW.json
```

The JSON reports median and p95 time for a complete dense repack and an incremental refresh at 0%, 0.1%, 1%, 10%, and 100% dirty over 1,024, 16,384, 262,144, and 1,048,576 entities. It also records exact-validation results and the largest measured dirty fraction where refresh won for each size.

## M4 Max result

On the project M4 Max with 16 Rayon workers, a one-million-entity full pack took 9.89-9.96 ms median across the five cases. Incremental refresh took 0.030 ms at 0.1% dirty, 0.125 ms at 1%, and 2.944 ms at 10%. Their measured speedups were 334x, 79x, and 3.38x. At 100% dirty, refresh took 11.511 ms and full pack took 9.918 ms, so repacking was 14% faster. The one-million-entity p95 values at 10% dirty were 3.228 ms for refresh and 10.216 ms for full pack.

The same pattern held at 16,384 and 262,144 entities: refresh won through 10% dirty and full pack won at 100%. At 1,024 entities refresh still won at 100%, but the absolute difference was only 0.007 ms and is not a useful global routing rule.

This establishes two safe endpoints for a workload policy on this machine: retain and refresh an unchanged view at or below 10% dirty, and repack a large view when every row is dirty. The crossover lies somewhere between those measured fractions and needs a finer calibration before automatic routing uses a threshold inside that interval. A topology change always rebuilds because row membership and generations are correctness constraints. Current queue pressure, cache residency, kernel count, and downstream CPU/GPU residency remain separate routing inputs.

The benchmark measures already-available dirty keys. It does not include the cost of detecting changes, publishing results, transferring buffers to a GPU, or synchronizing another process. Dirty tracking therefore belongs with the canonical component owner; rescanning the whole world to discover a small dirty set would erase much of the measured benefit.
