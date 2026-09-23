# Coasting engine benchmark

Run the portable benchmark from the repository root:

```text
dotnet run --project tools/KspContinuum.CoastingBench/KspContinuum.CoastingBench.csproj -c Release -- --samples 11 --output /tmp/coasting-bench.json
```

The matrix evaluates 1, 16, 256, and 4,096 independent trajectories with one-body work batches and batches of up to
64 bodies. Each strategy runs an epoch copy without propagation, one 600-second final sample, a sparse advance with one
intermediate publication, and a dense advance retaining 59 intermediate publications. Inputs and engine construction
are outside the timed region. The report records every wall-time and allocation observation, medians, normalized costs,
bitwise agreement between batch strategies, and a compact cost map.

One 11-sample ARM64 run observed the following medians for 4,096 bodies with 64-body work batches:

| Work | Elapsed | Process allocation |
| --- | ---: | ---: |
| Epoch copy and immutable snapshot | 0.067 ms | 0.35 MB |
| One 600-second final sample | 0.173 ms | 0.36 MB |
| One intermediate publication plus final | 0.360 ms | 0.71 MB |
| 59 intermediate publications plus final | 16.556 ms | 20.92 MB |

Subtracting the epoch-copy median from the final-sample median estimates about 0.106 ms of propagation work for this
fixture. This is a diagnostic difference between two complete paths, not an isolated solver timer. Dense publication
was the dominant portable cost in this run: about 96 times the elapsed time of one final sample, with allocation scaling
approximately with retained body snapshots. Work-batch results were bitwise identical, but the elapsed ratios varied
near parity between runs; this bench does not yet justify a fixed batch-size heuristic.

These numbers do not measure KSP frame time. The installed host profile in [profiling.md](../../docs/profiling.md)
places outermost `FlightIntegrator.FixedUpdate` around 0.710 ms and `FlightInputHandler.FixedUpdate` around 0.670 ms in
one instrumented workload, while attribution itself added about 0.423 ms to paired median frame time. The recursive
`FlightIntegrator.Integrate` total cannot rank providers. Together, the results make dense snapshot retention an engine
cost to avoid and keep KSP integration, input, and synchronization as the next end-to-end profiling targets rather
than presenting portable propagation throughput as an FPS gain.
