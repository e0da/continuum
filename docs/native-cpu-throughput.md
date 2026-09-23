# Native CPU layout and throughput benchmark

This experiment compares four safe Rust `f64` implementations of the same independent-body integration step on the native host CPU: a canonical array of structs (AoS), a single-allocation cache-skewed structure of arrays (SoA), eight-lane blocked SoA (AoSoA), and the same blocked layout over Rayon's persistent process-global thread pool. It tests whether layout or multicore execution changes the bottleneck at 16,384 through 4,194,304 bodies.

Run it through the repository task runner with a new output path:

```console
cargo run --manifest-path tools/xtask/Cargo.toml -- native-cpu-throughput-bench artifacts/native-cpu-throughput-NEW.json
```

The task runner compiles the benchmark with `-C target-cpu=native`; the receipt records the resulting process architecture.

Each strategy retains its state between steps. Calibration increases the number of steps per timed window until every strategy takes at least 10 ms, providing margin above the 5 ms acceptance floor. The reported median and p95 divide complete window time by its step count. Parallel windows include Rayon dispatch and synchronization. Fifteen measured windows are interleaved in rotating strategy order after three warmup windows, reducing fixed-order thermal bias. The report refuses to write if any observed window is shorter than 5 ms or any implementation differs bit for bit from the AoS baseline.

The blocked layout exposes fixed-width contiguous field lanes to the optimizer. Inspection of the native arm64 output confirmed that the SoA traversal was already compiled into a two-wide NEON loop, so bounds checks or complete failure to vectorize did not explain its earlier mid-size regression. This is code-generation evidence rather than a general SIMD guarantee. The benchmark measures execution on already resident data. It excludes layout construction, KSP and Unity integration, the x86_64 plugin ABI, capture and publication, constraints, contacts, rendering, and game-frame contention.

## Observation

One otherwise-idle M4 Max run used native `aarch64` code, 16 Rayon threads, 15 samples per case, and rotating strategy order. Every implementation produced bitwise-identical output. Calibration targeted at least 10 ms while the hard acceptance floor recorded as `minimumRequestedWindowNs` was 5 ms; the shortest accepted timed window was 10.13 ms.

| Bodies | AoS scalar | SoA scalar | Blocked scalar | Blocked Rayon | Blocked / AoS | Rayon / AoS |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16,384 | 0.0185 ms | 0.0245 ms | 0.0144 ms | 0.1004 ms | 1.28x | 0.18x |
| 65,536 | 0.0757 ms | 0.0939 ms | 0.0579 ms | 0.1421 ms | 1.31x | 0.53x |
| 262,144 | 0.3013 ms | 0.3996 ms | 0.2362 ms | 0.2053 ms | 1.28x | 1.47x |
| 1,048,576 | 1.2797 ms | 1.4636 ms | 1.0882 ms | 0.5214 ms | 1.18x | 2.45x |
| 4,194,304 | 5.0543 ms | 5.8380 ms | 4.4644 ms | 1.7238 ms | 1.13x | 2.93x |

The eight-lane blocked representation beat the AoS baseline at every measured size, although its advantage narrowed from about 31% to 13% as the working set grew. Rayon's scheduling and synchronization cost overwhelmed useful work below 262,144 bodies. It first won at 262,144 and reached 2.93 times the AoS throughput at 4,194,304 bodies, about 2.43 billion synthetic entity updates per second.

The earlier equal-length zipped SoA traversal performed especially poorly at the power-of-two sizes 65,536 and 262,144. An interleaved same-session diagnostic compared ten independent allocations, one contiguous allocation with no inter-column skew, and the same contiguous allocation with a 17-`f64` (136-byte) skew. At 65,536 bodies their medians were 678, 667, and 89.3 microseconds; at 262,144 they were 1,851, 1,857, and 390 microseconds. A single allocation alone did not help, while address skew improved the two cases by 7.47 and 4.76 times relative to contiguous storage without skew. Together with the generated-code inspection, this supports address or cache-set aliasing as the cause of the cliff, although no hardware counters were collected to identify the exact cache mechanism.

The maintained SoA layout now gives each logical column a deterministic 17-element skew. The production benchmark removed the cliff and improved SoA by 5.92 times at 65,536 bodies and 5.80 times at 262,144 compared with the preceding quiet run. SoA still delivers 25% less throughput than AoS at worst, and blocked SoA remains the fastest serial representation. This is a reusable correction to the benchmark's native CPU layout, not evidence of an integrated Continuum engine or KSP speedup.

These results support a routing policy that keeps small batches serial and considers persistent multicore execution only for large independent batches. They do not establish the crossover for a constraint solver, contact graph, KSP capture/publication path, x86_64 Unity plugin, or contended game process. They are routing evidence for this machine and synthetic kernel, not a KSP frame-rate claim.
