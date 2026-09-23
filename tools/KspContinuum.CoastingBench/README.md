# Coasting engine benchmark

Run the portable benchmark from the repository root:

```text
dotnet run --project tools/KspContinuum.CoastingBench/KspContinuum.CoastingBench.csproj -c Release -- --samples 11 --output /tmp/coasting-bench.json
```

The matrix evaluates 1, 16, 256, and 4,096 independent trajectories with one-body work batches and batches of up to
64 bodies. Each strategy runs an epoch copy without propagation, one 600-second final sample, a sparse advance with one
intermediate publication, 60 samples whose earlier results are discarded, and the same 60 samples while retaining 59
intermediate publications. Inputs and engine construction are outside the timed region. The report records every
wall-time and allocation observation, medians, normalized costs, bitwise agreement between batch strategies, and a
compact cost map. Checksums cover the final state; they do not independently validate every intermediate sample.

One 11-sample ARM64 run observed the following medians for 4,096 bodies with 64-body work batches:

| Work | Elapsed | Process allocation |
| --- | ---: | ---: |
| Epoch copy and immutable snapshot | 0.033 ms | 0.35 MB |
| One 600-second final sample | 0.075 ms | 0.35 MB |
| One intermediate publication plus final | 0.143 ms | 0.71 MB |
| 60 evaluations, earlier snapshots discarded | 5.348 ms | 20.91 MB |
| 59 intermediate publications retained plus final | 10.887 ms | 20.92 MB |

Subtracting the epoch-copy median from the final-sample median estimates about 0.042 ms of propagation work for this
fixture. This is a diagnostic difference between two complete paths, not an isolated solver timer. The matched dense
strategies separate repeated evaluation and snapshot creation from retaining those snapshots: discarding earlier results
took 5.348 ms, while retention took 10.887 ms. Both allocated about 20.9 MB in total, as expected because allocation
counts objects whether or not they remain live; their 5.539 ms elapsed difference captures the complete API-path and
lifetime difference rather than a pure retention timer. Work-batch results were bitwise identical, but elapsed ratios
varied substantially between runs; this bench does not yet justify a fixed batch-size heuristic.

These numbers do not measure KSP frame time. The installed host profile in [profiling.md](../../docs/profiling.md)
places outermost `FlightIntegrator.FixedUpdate` around 0.710 ms and `FlightInputHandler.FixedUpdate` around 0.670 ms in
one instrumented workload, while attribution itself added about 0.423 ms to paired median frame time. The recursive
`FlightIntegrator.Integrate` total cannot rank providers. Together, the results make dense repeated evaluation and
snapshot retention engine costs to avoid and keep KSP integration, input, and synchronization as the next end-to-end
profiling targets rather than presenting portable propagation throughput as an FPS gain.
