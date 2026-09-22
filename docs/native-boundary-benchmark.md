# KSP-to-Rust native boundary benchmark

This experiment measures the smallest architecture-compatible native route available without running KSP. The installed macOS KSP 1.12.5 executable, `UnityPlayer.dylib`, and `libmonobdwgc-2.0.dylib` are all x86_64 Mach-O binaries. Continuum's existing Metal benchmark runs as arm64. An arm64 library therefore cannot be loaded into the KSP process.

The selected route is an x86_64 C# host loading an x86_64 Rust `cdylib` under Rosetta. The host pins blittable arrays for synchronous P/Invoke calls. Rust mutates the arrays in place; return from the call is the synchronization boundary. No serialization, IPC, allocation, or GPU transfer occurs inside the native call. The harness separately measures copying canonical f64 state into the execution buffer, an f64-to-f32 conversion variant, and publication back to canonical state. It also measures a stable f64 execution view that retains motion and mass across ticks, refreshes only force inputs, and publishes only position and velocity.

This is the closest standalone test of the ABI and process architecture. It is not a Unity Mono measurement: the host uses self-contained x86_64 .NET 8 because the installed standalone Mono and .NET tools are arm64, while Unity's x86_64 Mono runtime is embedded in KSP. KSP was not launched for this experiment.

## Falsifiable question

Can a synchronous, in-process Rust call be cheap enough for one or a few physics steps at craft and background-fleet batch sizes?

- If the empty call or pinned-array call dominates small batches, native dispatch is unsuitable at that granularity.
- If an in-place native kernel wins but pack and publication erase the win, the boundary is useful only with persistent execution views or sufficiently many resident steps.
- If f32 conversion materially improves the complete path while staying within its declared error budget, conversion can be considered. Otherwise, preserve f64 at the CPU boundary.
- If retaining state while narrowing refresh and publication makes a one-step transaction faster than direct managed integration, use the native route for ordinary ticks. If it wins only after several resident steps, reserve it for workloads that can advance multiple steps without a host callback.

## Reproduction

Build and test the Rust library for the KSP-compatible architecture:

```console
RUSTC="$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin/rustc" "$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin/cargo" test --manifest-path tools/native-boundary/Cargo.toml --release --target x86_64-apple-darwin
RUSTC="$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin/rustc" "$HOME/.rustup/toolchains/stable-aarch64-apple-darwin/bin/cargo" build --manifest-path tools/native-boundary/Cargo.toml --release --target x86_64-apple-darwin --lib
```

Publish the managed host for x86_64, place `libcontinuum_native_boundary.dylib` beside it, and run with tiered compilation disabled so rows do not mix JIT tiers:

```console
dotnet publish tools/KspContinuum.NativeBoundaryBench/KspContinuum.NativeBoundaryBench.csproj -c Release -r osx-x64 --self-contained true -o BENCH_DIRECTORY
cp tools/native-boundary/target/x86_64-apple-darwin/release/libcontinuum_native_boundary.dylib BENCH_DIRECTORY/
DOTNET_TieredCompilation=0 BENCH_DIRECTORY/KspContinuum.NativeBoundaryBench --output NEW_REPORT.json
```

The output path must not already exist. Generated reports are local evidence and remain ignored by the repository.

## M4 Max observation

One run on an M4 Max used macOS 26.5.2, .NET 8.0.29 x86_64 under Rosetta, Rust 1.98.1, 101 samples per case, 12 warmups, and a 1/60 second step. Median times are nanoseconds. `native` includes P/Invoke, the synchronous Rust kernel, and return. `complete` is measured directly as one full pack/conversion, P/Invoke, and publication transaction over the same reusable buffers and evolving canonical state. It is not a sum of independently measured components. The empty native call median was 41 ns (42 ns p95).

| bodies | steps | managed f64 | native f64 | complete f64 | managed/native | managed/complete | native f32 | complete f32 | f32 max position error (m) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | 1 | 42 | 42 | 83 | 1.00x | 0.51x | 42 | 83 | 0.00000042 |
| 64 | 1 | 167 | 83 | 250 | 2.01x | 0.67x | 83 | 250 | 0.00000094 |
| 256 | 1 | 583 | 209 | 917 | 2.79x | 0.64x | 250 | 1,042 | 0.00000190 |
| 1,024 | 1 | 1,834 | 1,041 | 4,917 | 1.76x | 0.37x | 875 | 4,500 | 0.00000760 |
| 4,096 | 1 | 8,750 | 5,250 | 20,583 | 1.67x | 0.43x | 4,083 | 19,416 | 0.00003051 |
| 16,384 | 1 | 32,791 | 19,041 | 82,458 | 1.72x | 0.40x | 16,458 | 78,125 | 0.00012206 |
| 16 | 4 | 167 | 83 | 83 | 2.01x | 2.01x | 83 | 125 | 0.00000119 |
| 64 | 4 | 542 | 250 | 375 | 2.17x | 1.45x | 250 | 459 | 0.00000330 |
| 256 | 4 | 1,833 | 917 | 1,458 | 2.00x | 1.26x | 958 | 1,833 | 0.00000725 |
| 1,024 | 4 | 8,000 | 3,583 | 7,542 | 2.23x | 1.06x | 3,750 | 7,333 | 0.00002726 |
| 4,096 | 4 | 35,125 | 20,750 | 35,875 | 1.69x | 0.98x | 16,125 | 31,334 | 0.00012172 |
| 16,384 | 4 | 143,208 | 84,791 | 145,959 | 1.69x | 0.98x | 64,541 | 126,250 | 0.00048808 |

The direct native f64 call matched the managed f64 result exactly and was 1.67-2.79x faster in this run once the batch exceeded timer-scale cases. The directly measured complete one-step full-copy transaction remained slower at every size, so rebuilding and publishing every row for every ordinary tick is ruled out for this small kernel. Four steps amortized enough work to win at 64 and 256 bodies and reached approximate parity at larger sizes. The preferred design remains a persistent, pinned execution view with dirty-row refresh and selective publication; batching multiple steps is a second valid amortization mechanism to qualify for warp and background simulation.

Two additional fresh process runs reproduced the central result. The complete four-step f64 transaction was faster at 64 bodies in all three runs (1.45-1.78x) and at 256 bodies in all three runs (1.13-1.26x). The 1,024-body result straddled parity (0.95-1.06x), while 16,384 bodies remained slightly slower (0.97-0.98x). One-step full-copy execution remained slower throughout. The smallest timings are near timer resolution and should not drive routing policy.

### Persistent-view follow-up

The next run retained all ten f64 fields in the execution view between calls. Each transaction refreshed three force fields per body, called Rust synchronously, and published six motion fields per body. Stable inverse mass and prior motion were not repacked. The managed comparison integrated the same canonical body layout directly. The correctness check required the complete persistent transaction to match the managed state exactly.

One M4 Max run produced these median transaction times; two fresh-process repetitions checked the routing result. Times are nanoseconds.

| bodies | steps | managed canonical | native call | force refresh | motion publication | persistent complete | managed/complete |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 64 | 1 | 125 | 83 | 42 | 83 | 167 | 0.75x |
| 256 | 1 | 458 | 209 | 167 | 333 | 667 | 0.69x |
| 1,024 | 1 | 1,750 | 917 | 1,375 | 1,916 | 4,083 | 0.43x |
| 16,384 | 1 | 31,292 | 18,916 | 23,583 | 27,584 | 70,917 | 0.44x |
| 64 | 4 | 417 | 250 | 42 | 84 | 375 | 1.11x |
| 256 | 4 | 1,750 | 833 | 166 | 292 | 1,375 | 1.27x |
| 1,024 | 4 | 6,958 | 3,666 | 1,375 | 1,750 | 7,125 | 0.98x |
| 16,384 | 4 | 117,041 | 84,750 | 23,500 | 27,667 | 132,708 | 0.88x |

The two repeated runs kept the four-step speedup at 64 bodies between 1.10x and 1.11x and at 256 bodies between 1.12x and 1.27x. The 1,024-body case ranged from 0.92x to 1.00x and 16,384 bodies from 0.85x to 0.88x. One-step persistent execution lost at every meaningful size. This rules out routing this trivial kernel through Rust on every ordinary tick merely because its execution view is persistent. It supports Rust when the native workload does materially more computation per published state, or when warp and background simulation permit several resident steps.

The f32 route does not rescue full-copy execution. It reduces buffer size and can reduce native kernel time, but conversion remains dominant and introduces measurable error. It should be selected only for explicitly local coordinates and a qualified error budget, not as a general boundary optimization.

## Route decision

Use the in-process x86_64 Rust dylib as the next CPU integration route for compute-dense or multi-step work. Keep the smallest one-step live canary managed until a representative kernel demonstrates enough work to repay refresh and publication. Measure the same exports from Unity Mono before claiming KSP latency or frame improvement. Bind Rust to the existing persistent execution-view and stale-publication contracts rather than rebuilding all rows per tick.

An arm64 sidecar could use native Apple Silicon and the existing Metal backend, but adds IPC, scheduling, and synchronization. It becomes a useful experiment only when a resident workload is large enough to amortize those costs. An in-process x86_64 Metal backend avoids IPC but still needs x86_64 GPU qualification and the existing f32/local-offset accuracy contract. Neither GPU route is supported by this CPU ABI result.

This benchmark does not measure Unity Mono, KSP's actual extraction and publication code, dirty-set discovery, contention with rendering, GPU transfer, or live frame rate. The persistent-view transaction still scans every row for force refresh and motion publication. It establishes that the architecture-compatible synchronous ABI is cheap, while host traffic can dominate even without full-state repacking.

## Unity Mono qualification

The opt-in `--continuum-native-boundary-bench` main-menu qualification measures the same f64 managed and Rust kernels from KSP's embedded Mono host. Install the x86_64 `libcontinuum_native_boundary.dylib` beside `KspContinuum.dll` in `GameData/KspContinuum/Plugins`, then launch an owned KSP 1.12.5 qualification copy with the flag. The addon writes `native-boundary-mono-*.json` under `PluginData` and exits with code 0; ABI, layout, load, or exact-result failures exit with code 2.

The receipt reports 101-sample median and p95 latency for 64, 256, 1,024, and 4,096 bodies over one and four steps, plus a 1,001-sample empty-call baseline. This isolates Unity Mono synchronous P/Invoke plus the Rust kernel. It deliberately excludes vessel capture, execution-view refresh, transactional publication, Unity physics, rendering, and live contention, so it can qualify host-specific call cost but cannot establish a game-frame speedup.
