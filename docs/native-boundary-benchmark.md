# KSP-to-Rust native boundary benchmark

This experiment measures the smallest architecture-compatible native route available without running KSP. The installed macOS KSP 1.12.5 executable, `UnityPlayer.dylib`, and `libmonobdwgc-2.0.dylib` are all x86_64 Mach-O binaries. Continuum's existing Metal benchmark runs as arm64. An arm64 library therefore cannot be loaded into the KSP process.

The selected route is an x86_64 C# host loading an x86_64 Rust `cdylib` under Rosetta. The host pins blittable arrays and makes synchronous P/Invoke calls. Rust mutates the arrays in place; return from the call is the synchronization boundary. No serialization, IPC, allocation, or GPU transfer occurs inside the native call. The harness separately measures copying canonical f64 state into the execution buffer, an f64-to-f32 conversion variant, and publication back to canonical state.

This is the closest standalone test of the ABI and process architecture. It is not a Unity Mono measurement: the host uses self-contained x86_64 .NET 8 because the installed standalone Mono and .NET tools are arm64, while Unity's x86_64 Mono runtime is embedded in KSP. KSP was not launched for this experiment.

## Falsifiable question

Can a synchronous, in-process Rust call be cheap enough for one or a few physics steps at craft and background-fleet batch sizes?

- If the empty call or pinned-array call dominates small batches, native dispatch is unsuitable at that granularity.
- If an in-place native kernel wins but pack and publication erase the win, the boundary is useful only with persistent execution views or sufficiently many resident steps.
- If f32 conversion materially improves the complete path while staying within its declared error budget, conversion can be considered. Otherwise, preserve f64 at the CPU boundary.

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

One run on an M4 Max used macOS 26.5.2, .NET 8.0.29 x86_64 under Rosetta, Rust 1.98.1, 101 samples per case, 12 warmups, and a 1/60 second step. Median times are nanoseconds. `native` includes P/Invoke, the synchronous Rust kernel, and return. `complete` adds full pack/conversion and full publication. The empty native call median was 41 ns (42 ns p95).

| bodies | steps | managed f64 | native f64 | complete f64 | managed/native | managed/complete | native f32 | complete f32 | f32 max position error (m) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | 1 | 42 | 42 | 126 | 1.00x | 0.33x | 42 | 126 | 0.00000042 |
| 64 | 1 | 125 | 125 | 293 | 1.00x | 0.43x | 83 | 333 | 0.00000094 |
| 256 | 1 | 459 | 250 | 834 | 1.84x | 0.55x | 250 | 1,084 | 0.00000190 |
| 1,024 | 1 | 1,625 | 875 | 4,792 | 1.86x | 0.34x | 875 | 4,333 | 0.00000760 |
| 4,096 | 1 | 8,125 | 5,417 | 21,250 | 1.50x | 0.38x | 4,208 | 19,916 | 0.00003051 |
| 16,384 | 1 | 32,458 | 21,292 | 83,667 | 1.52x | 0.39x | 16,625 | 78,959 | 0.00012206 |
| 16 | 4 | 125 | 83 | 167 | 1.51x | 0.75x | 83 | 167 | 0.00000119 |
| 64 | 4 | 459 | 292 | 458 | 1.57x | 1.00x | 250 | 500 | 0.00000330 |
| 256 | 4 | 1,833 | 875 | 1,500 | 2.09x | 1.22x | 917 | 1,751 | 0.00000725 |
| 1,024 | 4 | 7,042 | 3,708 | 7,542 | 1.90x | 0.93x | 3,833 | 7,417 | 0.00002726 |
| 4,096 | 4 | 28,333 | 21,041 | 36,666 | 1.35x | 0.77x | 16,292 | 31,792 | 0.00012172 |
| 16,384 | 4 | 127,709 | 86,542 | 149,167 | 1.48x | 0.86x | 66,250 | 129,833 | 0.00048808 |

The direct native f64 call matched the managed f64 result exactly and was 1.35-2.09x faster in the cases where timer resolution did not hide the difference. A complete one-step full-copy path was 1.8-3.0x slower than the managed kernel, so rebuilding and publishing every row for every ordinary tick is ruled out for this small kernel. Four steps amortized enough work to reach parity at 64 bodies and a 1.22x win at 256 bodies in this run, although the larger batches remained slower end to end. The preferred design is still a persistent, pinned execution view with dirty-row refresh and selective publication; batching multiple steps is a second valid amortization mechanism to qualify for warp and background simulation.

Two additional fresh process runs reproduced the central result. At 256 bodies the direct native f64 speedup was 1.83x in all three one-step runs and 1.87-2.09x in the four-step runs. The complete four-step path remained faster at 256 bodies in all three runs (1.07-1.22x), while the complete 1,024-body four-step path remained slightly slower (0.93-0.94x) and the complete 16,384-body four-step path remained slower (0.86x). The isolated 64-body timings are near timer resolution and varied more.

The f32 route does not rescue full-copy execution. It reduces buffer size and can reduce native kernel time, but conversion remains dominant and introduces measurable error. It should be selected only for explicitly local coordinates and a qualified error budget, not as a general boundary optimization.

## Route decision

Use the in-process x86_64 Rust dylib as the next CPU integration route. Measure the same exports from Unity Mono before claiming KSP latency or frame improvement. Bind it to the existing persistent execution-view and stale-publication contracts rather than rebuilding all rows per tick.

An arm64 sidecar could use native Apple Silicon and the existing Metal backend, but adds IPC, scheduling, and synchronization. It becomes a useful experiment only when a resident workload is large enough to amortize those costs. An in-process x86_64 Metal backend avoids IPC but still needs x86_64 GPU qualification and the existing f32/local-offset accuracy contract. Neither GPU route is supported by this CPU ABI result.

This benchmark does not measure Unity Mono, KSP's actual extraction and publication code, contention with rendering, GPU transfer, or live frame rate. It establishes that the architecture-compatible synchronous ABI is cheap and that full per-tick repacking is not.
