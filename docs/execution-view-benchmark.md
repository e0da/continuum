# Rust execution-view benchmark

This benchmark calibrates two CPU implementations of the same dense state-integration kernel: a scalar safe-Rust SoA loop and a Rayon CPU-parallel SoA loop. It also measures packing canonical body records into the execution view. All three measurements use logarithmic batch sizes from 16 through 1,048,576 entities and report median and p95 wall time.

Run it through the repository task runner with a new output path:

```console
cargo run --manifest-path tools/xtask/Cargo.toml -- execution-bench artifacts/execution-view-NEW.json
```

The JSON includes deterministic scalar and parallel checksums, the first observed batch size after which parallel execution wins at every larger measured size, and simple fixed-plus-per-entity least-squares estimates. Those estimates are calibration hints for `ExecutionWorkload`; their `rSquared` values expose poor linear fits rather than hiding cache and scheduling effects.

The scalar loop is written to be friendly to compiler optimization, but this experiment does not inspect generated code and does not claim explicit SIMD. The parallel lane uses Rayon's process-global CPU pool. It does not include a persistent Continuum scheduler, affinity control, KSP/Unity handoff, native ABI cost, contention with game work, GPU transfer, or device-resident kernels. Packing intentionally includes allocation and canonical-to-SoA copying; integration excludes cloning and checksum calculation.

The benchmark is synthetic. Use its crossover only for the measured machine, build, workload shape, and otherwise-idle process. Runtime routing should combine calibrated cost with current queue pressure, residency, latency budget, determinism requirements, and dependency conflicts. A GPU calibration must separately include upload, dispatch, synchronization, download, and the option to retain state on device across several kernels.
