# Native CPU layout and throughput benchmark

This experiment compares four safe Rust `f64` implementations of the same independent-body integration step on the native host CPU: a canonical array of structs (AoS), sealed structure of arrays (SoA), eight-lane blocked SoA (AoSoA), and the same blocked layout over Rayon's persistent process-global thread pool. It tests whether layout or multicore execution changes the bottleneck at 16,384 through 4,194,304 bodies.

Run it through the repository task runner with a new output path:

```console
cargo run --manifest-path tools/xtask/Cargo.toml -- native-cpu-throughput-bench artifacts/native-cpu-throughput-NEW.json
```

The task runner compiles the benchmark with `-C target-cpu=native`; the receipt records the resulting process architecture.

Each strategy retains its state between steps. Calibration increases the number of steps per timed window until every strategy takes at least 10 ms, providing margin above the 5 ms acceptance floor. The reported median and p95 divide complete window time by its step count. Parallel windows include Rayon dispatch and synchronization. Fifteen measured windows are interleaved in rotating strategy order after three warmup windows, reducing fixed-order thermal bias. The report refuses to write if any observed window is shorter than 5 ms or any implementation differs bit for bit from the AoS baseline.

The blocked layout exposes fixed-width contiguous field lanes to the optimizer, but the experiment does not inspect generated assembly and makes no explicit SIMD claim. It measures execution on already resident data. It excludes layout construction, KSP and Unity integration, the x86_64 plugin ABI, capture and publication, constraints, contacts, rendering, and game-frame contention.

## Observation

One otherwise-idle M4 Max run used native `aarch64` code, 16 Rayon threads, 15 samples per case, and rotating strategy order. Every implementation produced bitwise-identical output. The shortest accepted timed window was 11.05 ms.

| Bodies | AoS scalar | SoA scalar | Blocked scalar | Blocked Rayon | Blocked / AoS | Rayon / AoS |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16,384 | 0.0185 ms | 0.0233 ms | 0.0143 ms | 0.1047 ms | 1.29x | 0.18x |
| 65,536 | 0.0741 ms | 0.5562 ms | 0.0575 ms | 0.1418 ms | 1.29x | 0.52x |
| 262,144 | 0.3002 ms | 2.3187 ms | 0.2337 ms | 0.2055 ms | 1.28x | 1.46x |
| 1,048,576 | 1.2438 ms | 1.6207 ms | 1.0513 ms | 0.5123 ms | 1.18x | 2.43x |
| 4,194,304 | 5.0618 ms | 6.4973 ms | 4.4703 ms | 1.7269 ms | 1.13x | 2.93x |

The eight-lane blocked representation beat the AoS baseline at every measured size, although its advantage narrowed from about 29% to 13% as the working set grew. Rayon's scheduling and synchronization cost overwhelmed useful work below 262,144 bodies. It first won at 262,144 and reached 2.93 times the AoS throughput at 4,194,304 bodies, about 2.43 billion synthetic entity updates per second.

An initial indexed SoA loop did not win and performed especially poorly at 65,536 and 262,144 bodies. Because ten independently indexed public vectors could preserve repeated bounds checks, that run was rejected and the maintained lane was changed to one equal-length zipped traversal. A fresh quiet run reproduced the loss: optimized SoA reached only 0.13 times AoS throughput at the two middle sizes and 0.77 to 0.79 times it elsewhere. The ten concurrent memory streams and generated code are plausible causes, but this experiment does not assign a cause without assembly and hardware-counter evidence. It establishes that this plain SoA loop is not the winning CPU representation for this kernel.

These results support a routing policy that keeps small batches serial and considers persistent multicore execution only for large independent batches. They do not establish the crossover for a constraint solver, contact graph, KSP capture/publication path, x86_64 Unity plugin, or contended game process. They are routing evidence for this machine and synthetic kernel, not a KSP frame-rate claim.
