# Compiled execution views

Continuum keeps canonical entity identity and ownership separate from kernel layout. `EntityKey(slot, generation)` remains stable while an entity is alive and prevents a recycled slot from accepting an old result. `ExecutionSnapshot` is an immutable canonical boundary with the same tick/topology/frame stamp used by the simulation worker plus an owner revision.

`ExecutionViewCompiler.CompileMotion` applies one system query and copies only matching motion data into dense structure-of-arrays storage. A query declares required and excluded component capabilities and a lane width of 1, 4, 8, or 16. The view retains stable keys and canonical source indices; dense lane number is never entity identity. Read-only indexed views expose columns to managed kernels without materializing entities and remain compatible with KSP's .NET 4.7.2 target. Padding is explicit and excluded from `Count`, so kernels must mask their final block; a SIMD or GPU backend can choose a width without changing canonical storage or semantic membership. Contact and structural flags demonstrate that separate systems can compile different views from the same world.

The first typed view contains position, velocity, force, and mass because those fields already have a measured workload and ownership contract. This is not a universal ECS, archetype store, scheduler, or mutable world. New systems should add typed views only when a real kernel proves its column set and ordering. The prior layout benchmark found sealed SoA best in most measured cases and did not justify making AoSoA the world representation.

`IMotionExecutionBackend` is the backend seam. A managed scalar or SIMD implementation can consume the view now. A Rust/native or GPU adapter can later lease or pin its private columns and pass a versioned descriptor across a narrow ABI; that buffer-lifetime and device-transfer contract is deliberately absent until measured at the actual boundary. Rust must not own canonical identity or publish directly into game state.

## Workload-driven routing

`ExecutionWorkload` describes the facts that can change a backend choice: dense count and lane width, regular versus branchy work, current host/device residency, independent versus conflicting dependencies, latency budget, and determinism requirement. `ExecutionBackendReport` declares a backend's supported shape and a calibrated fixed, per-entity, and transfer cost. These reports are inputs from benchmarks; the contract does not infer that a GPU is fast merely because one exists.

`ExecutionBackendPolicy.Select` first rejects backends that cannot meet correctness and shape constraints, then selects the lowest estimated cost. Equal estimates use ordinal backend ID, so report enumeration order cannot change the route. Exactly one eligible scalar reference must always be present. The route reports whether its estimate meets the latency budget; missing a performance target does not weaken determinism or silently choose an incompatible backend. The initial tests show host SIMD winning regular 1,024-entity work while device transfer and weaker determinism disqualify the nominally faster GPU report; irregular shared work remains on the scalar reference.

`ExecutionPublisher.Publish` is the only implemented result transition. It revalidates the complete stamp, owner revision, source index, slot, and generation before constructing a new snapshot. Any mismatch rejects the whole result and publishes nothing. Successful publication increments the revision and changes only motion fields selected by the view. This provides deterministic gather, backend compute, and transactional scatter without allowing a kernel to retain or mutate canonical world objects.

Run the portable contract tests with:

```text
dotnet run --project tests/KspContinuum.ExecutionViews.Tests -c Release
```

The tests distinguish the representations structurally: four canonical entities compile to three dense lanes padded to four, an excluded contact entity remains in canonical state, dense lanes map back to noncontiguous canonical indices, and a recycled entity generation rejects the complete result. They also verify immutable before-images and all-or-none stale publication. Existing timing evidence remains in `layout-benchmark.md`; this slice makes the winning separation usable but makes no new throughput claim.

The strongest alternative is an archetype ECS that makes dense component tables the canonical world. It could remove some gather work, but current evidence only establishes a benefit for sealed SoA at a bounded worker handoff. It does not establish topology-change cost, mod-facing identity behavior, or a common layout across flight, contact, resource, and orbital systems. Compiled views preserve those decisions until live workloads measure them.

## Open questions

- Whether view compilation should be incremental, cached by query and revision, or rebuilt each tick depends on measured topology and component churn.
- Native buffer leases, GPU transfer overlap, and device-resident multi-kernel pipelines need a benchmark at the actual boundary before an ABI is fixed.
- Parallel publication may require component-level write sets rather than the current whole-owner revision. The first live consumer should determine that granularity without weakening stale-result rejection.
- Query planning may eventually use archetype indexes. The current linear scan is intentionally the smallest correctness oracle and must be measured before replacement.
