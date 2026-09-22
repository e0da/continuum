# Rust execution-view benchmark

This benchmark calibrates scalar CPU, Rayon CPU-parallel, and Metal GPU implementations of the same dense state-integration kernel. It also measures packing canonical body records into the execution view. Measurements use logarithmic batch sizes from 16 through 1,048,576 entities and report median and p95 wall time.

Run it through the repository task runner with a new output path:

```console
cargo run --manifest-path tools/xtask/Cargo.toml -- execution-bench artifacts/execution-view-NEW.json
```

The JSON includes deterministic scalar and parallel checksums, GPU error against scalar Rust, stable observed crossovers, and simple fixed-plus-per-entity least-squares estimates. Those estimates are calibration hints for `ExecutionWorkload`; their `rSquared` values expose poor linear fits rather than hiding cache and scheduling effects.

The scalar loop is written to be friendly to compiler optimization, but this experiment does not inspect generated code and does not claim explicit SIMD. The parallel lane uses Rayon's process-global CPU pool. The GPU lane uses safe Rust `wgpu` targeting Metal and a WGSL compute shader over three `vec4` SoA columns. Metal's portable shader model does not provide `f64`, so the kernel retains `f64` position anchors on the host and integrates local position offsets, velocity, force, and inverse mass in `f32`. GPU results must meet a mixed absolute-or-relative tolerance against the scalar `f64` result; the report records maximum error and does not claim bitwise GPU determinism.

`gpuColdEndToEnd` times conversion to GPU columns, upload, one dispatch, synchronization, readback, and reconstruction. `gpuResidentAmortizedPerStep` uploads once, retains state on device, issues 64 integration dispatches per submission, synchronizes and reads back once, then divides the batch duration by 64. It still includes amortized readback and host reconstruction. Neither lane includes adapter/device/pipeline creation.

## M4 Max observation

One run on an Apple M4 Max 40-core GPU, macOS 26.5.2, Rust 1.98.1, `wgpu` 26.0.1, and a 16-thread Rayon pool produced these median times. Times are microseconds per integration step.

| Entities | Scalar CPU | Rayon CPU | GPU cold | GPU resident |
|---:|---:|---:|---:|---:|
| 16 | 0.083 | 24.959 | 1,629.000 | 24.267 |
| 1,024 | 3.250 | 78.750 | 1,982.833 | 25.876 |
| 4,096 | 21.917 | 135.042 | 2,058.916 | 27.321 |
| 16,384 | 46.583 | 264.084 | 1,980.792 | 26.264 |
| 65,536 | 310.250 | 319.666 | 2,362.875 | 28.230 |
| 262,144 | 1,818.708 | 447.583 | 4,694.583 | 62.101 |
| 1,048,576 | 1,494.375 | 625.875 | 15,759.375 | 196.193 |

Cold GPU execution never beat scalar CPU in the measured range. The resident GPU lane became and remained faster than scalar at 16,384 entities and beat the measured Rayon lane at every size, although scalar remained decisively better for small batches. At 65,536 entities, resident GPU was 10.99 times faster than scalar and 11.32 times faster than Rayon. At 1,048,576 entities, it was 7.62 and 3.19 times faster respectively. The non-monotonic CPU measurements reinforce that these are routing observations for this machine and run, not universal constants.

The maximum one-step GPU error was `9.53e-7`. After 1,152 resident steps it was `5.71e-3`, within the declared `0.05` absolute or `1e-5` relative tolerance. An earlier absolute-`f32` position implementation accumulated `11.27 m` of error at the largest size; the anchor-plus-local-offset representation removed that scale-dependent loss and is part of the measured implementation.

The benchmark is synthetic. Use its crossover only for the measured machine, build, workload shape, and otherwise-idle process. Runtime routing should combine calibrated cost with current queue pressure, residency, precision and determinism requirements, latency budget, dependency conflicts, and the number of useful kernels before readback. It does not include a persistent Continuum scheduler, KSP/Unity handoff, native ABI cost, or contention with rendering and game work.
