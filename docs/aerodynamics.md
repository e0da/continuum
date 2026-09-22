# Aerodynamic strategies

Continuum treats aerodynamics as a typed strategy graph. Atmosphere, geometry, exposure, coefficient estimation, force construction and integration remain separate contracts. A stage may use equations, lookup tables, voxel kernels or a learned model, but every boundary declares units, frame, provider, validity domain and provenance.

```text
body state ──> atmosphere ───────────────┐
                                         v
craft ──> geometry ──> exposure ──> coefficients ──> part wrenches ──> integrator
                          ^                    |
                          └── neighborhood ────┘
```

This is not an arbitrary chain of predictors. Deterministic transforms should own coordinate conversion, atmospheric scaling, force construction and torque bookkeeping. A learned strategy should initially predict nondimensional coefficient residuals inside an explicit domain and abstain elsewhere. Every candidate has an analytic fallback.

## Why voxel geometry belongs here

A sparse multiresolution occupancy or signed-distance representation is useful even if learned aerodynamics loses. It can provide exposure, projected area, shielding, neighborhood and section features for deterministic kernels. The same representation can later support collision broad phase, heating exposure, blast propagation and terrain interaction.

Static craft geometry should be captured and reduced once. Staging, damage, deployment and moving control surfaces invalidate only affected regions. Coarse levels screen distant or unimportant interactions; fine levels refine exposed boundaries and moving geometry. Resolution is part of the strategy and evidence record, not a global fidelity setting.

Voxel fields are also regular batched inputs for learned candidates. That convenience does not justify relearning known atmosphere equations or frame transforms.

## Equivalent aerodynamic structures

A craft's construction topology need not be its aerodynamic topology. Fifty attached parts that form one smooth wing can be reduced to a small set of spanwise panels. A built-up fuselage can become longitudinal cross-sections; a fairing or heat shield can become a fitted body or surface. The reducer may use connected voxel regions, principal axes, medial structure, cross-section sweeps and primitive fitting to infer:

- wing planform, chord, sweep, twist, camber and thickness;
- control-surface regions and hinge axes;
- fuselage and nacelle sections;
- exposed leading/trailing edges and junctions;
- attachment, mass, center-of-mass and inertia mappings back to source parts.

The compact representation owns aerodynamic evaluation while retaining a contribution map to the original parts. Resulting forces and torques can be distributed back by area, stiffness, attachment or another declared rule for heating, failure and visualization. When a craft stages, deforms or breaks, only affected regions are rebuilt. Contact or damage can temporarily expand a compact component back to finer structure.

This is model reduction, not necessarily machine learning. Deterministic segmentation and fitted lifting-line or panel-style solvers should be evaluated first. Learned classification may help propose primitives, and learned residuals may approximate interference, separation or stall after a trusted higher-fidelity oracle exists. A voxel fluid solver is a later refinement strategy for bounded regions, not the default hot loop.

Continuum should expose separate opt-in policies:

- **stock-compatible** predicts or reproduces stock behavior for existing craft and tests;
- **FAR-compatible** uses FAR as an oracle or selected provider when installed;
- **Continuum natural aero** favors shape-derived equivalent structures and intuitive physical response, accepting that craft tuned to stock mathematics may behave differently.

FAR may already be the best active-vessel answer for many players. Continuum must compare before rebuilding it. Continuum's distinct value may instead be deterministic batch evaluation, fleet-scale off-camera simulation, multiresolution refinement, replay, and one geometry representation shared across aero, heating, damage and collision systems.

## Stock KSP 1.12.5

The inspected `Assembly-CSharp.dll` has SHA-256 `8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27` and module MVID `10657063-2fc3-43a7-84fa-d39e75e877bf`.

`FlightIntegrator.FixedUpdate` derives vessel atmosphere and Mach state, updates drag-cube and part aerodynamic state, and recursively integrates parts. `Integrate` consumes and clears generic `Part.force`, `Part.torque` and positioned force holders before calling `UpdateAerodynamics(part)`. Stock aerodynamic body drag and body lift then call the rigidbody directly. They are therefore absent from Continuum's existing component-force census.

Stock wings use another channel. `ModuleLiftingSurface.FixedUpdate` computes its public lift and drag forces and deposits them through `Part.AddForceAtPosition`. The existing FashionablyLate force census can observe those deposits only after installed callback ordering is qualified. Body aero and lifting-surface aero remain distinct providers and must be joined deliberately; an `UpdateAerodynamics` observation alone is not total stock aero.

`UpdateAerodynamics`, RVA `0x23a9a0`, derives relative airflow from rigidbody velocity plus Krakensbane frame velocity, updates local and world drag directions, rejects shielded or unsupported environment states, evaluates `DragCubes`, and records aerodynamic area and dynamic pressure. Drag is applied through `ApplyAeroDrag`, RVA `0x23a830`; body lift through `ApplyAeroLift`, RVA `0x23a8bc`. Relevant public part state includes drag vectors and scalar, body-lift scalar/vector/application position, aerodynamic and exposed area, dynamic pressure, density, pressure, Mach, drag cubes, transforms and center-of-pressure/lift offsets.

The first stock oracle should observe the exact completed `UpdateAerodynamics(Part)` call and reconstruct the force, application point and torque from the fields it wrote. A version-pinned Harmony postfix is a candidate because it can observe without owning the calculation. It must first prove callback ordering, coexistence and clean removal. Continuum must not invoke the stock method, mutate its output, or occupy an integration override merely to observe it.

## FAR and shared integration

[FAR's current architecture](https://github.com/ferram4/Ferram-Aerospace-Research/blob/787a30bc9deab0bde87591f0cc973ec3b0dd2de9/FerramAerospaceResearch/FARAeroComponents/FARVesselAero.cs) computes atmosphere and flow state once per vessel, evaluates compact aerodynamic sections, accumulates per-part forces and then applies them. Geometry changes trigger [main-thread transform capture followed by queued voxel processing](https://github.com/ferram4/Ferram-Aerospace-Research/blob/787a30bc9deab0bde87591f0cc973ec3b0dd2de9/FerramAerospaceResearch/FARAeroComponents/VehicleAerodynamics.cs#L364-L445). This demonstrates the capture, worker and publication shape; it does not establish FAR's threading implementation as a performance ceiling.

FAR exposes an aggregate prediction seam through [`FARAPI.CalculateVesselAeroForces`](https://github.com/ferram4/Ferram-Aerospace-Research/blob/787a30bc9deab0bde87591f0cc973ec3b0dd2de9/FerramAerospaceResearch/FARAPI.cs#L316-L348). Treat it as a separate provider and dataset. Its thread safety is not established, so initial calls remain on the main thread. Aggregate output cannot silently substitute for stock per-part labels.

[ModularFlightIntegrator](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularFlightIntegrator.cs#L734-L772) exposes a single-owner aerodynamic override. FAR already owns that slot when installed. Continuum must not compete for it to observe or benchmark. A future active strategy would require an explicit owner configuration or a permanent dispatcher and likely a scene reload to change owners.

FAR source is GPLv3 and its assets have separate terms. Continuum should use public APIs, measured outputs and independently implemented algorithms rather than importing implementation code. MFI is MIT licensed.

## Portable contracts

`AeroGeometrySnapshot` contains immutable part identity and topology, transforms relative to vessel center of mass, application arms, drag-cube or provider-neutral area descriptors, deployment/control state and a versioned multiresolution geometry reference.

`AeroQuery` combines one geometry snapshot with atmosphere, relative flow, angular velocity, attitude, control state and timestep. Body identity remains provenance. Stock atmosphere density, pressure, temperature and speed of sound are cheap authoritative inputs; a model should not rediscover them from body and elevation. Probes remain useful for validating these curves and characterizing modded atmospheres, weather or unknown providers.

`AeroResult` returns per-part force, torque and application point when the provider supports them, plus independently recomputed vessel force and center-of-mass torque. It also carries provider/version, strategy/version, frames, validity domain, uncertainty or error bound, and either `valid` or `abstain(reason)`.

`IAeroOracle` supplies trusted labels from a pinned provider. `IAeroStrategy` evaluates immutable batches through analytic, table, voxel or learned implementations. Provider identity is part of the type contract: stock and FAR observations never mix implicitly.

## Representation and candidate order

Predict nondimensional coefficients in a wind-aligned part frame: drag, two transverse components and torque coefficients. Deterministic code applies dynamic pressure, reference measures and coordinate transforms. This encodes translation and rotation behavior and separates atmospheric scale from shape response.

Candidates compete in this order:

1. zero and mean diagnostics;
2. a hand-coded drag-cube interpolation or vectorized stock-formula baseline;
3. ridge or low-order polynomial coefficient regression;
4. small boosted trees;
5. a shared tiny per-part network;
6. a permutation-equivariant neighbor graph only if residual analysis proves that independent parts and exposure features miss important interactions.

The strongest outcome may be a faster deterministic stock-style kernel. Machine learning earns a place only when it improves held-out accuracy or throughput after capture, packing, dispatch, validation and fallback costs.

### First deterministic baseline

`AeroDragCubeBaseline` is the first portable candidate, identified as
`continuum-drag-cube-projection/v1`. It projects relative airflow onto the six
captured drag-cube faces, blends each cube's weight, area, drag coefficient and
modifier, applies dynamic pressure, and maps the weighted cube center back into
the world frame. Scalar and bounded parallel entry points run the identical
per-part calculation; the parallel path preserves input order and performs no
shared reduction.

This is a deliberately falsifiable baseline, not a reproduction of KSP's stock
algorithm. It does not yet model Mach curves, pseudo-Reynolds corrections, body
lift, drag-cube interpolation details, shielding transitions, lifting surfaces,
heating, or provider-specific clamps. Cube centers are provisionally treated as
part-local application offsets. Captured stock labels must determine whether
that interpretation and the face projection are useful. Until then the result
is suitable for metamorphic checks and throughput experiments only, and cannot
control a vessel.

## `AERO-CAPTURE-001`

The first experiment is a bounded read-only truth recorder, not a replacement solver.

The addon now contains the first live capture seam for the pinned stock assembly. It patches `UpdateAerodynamics`,
`ApplyAeroDrag`, and `ApplyAeroLift` under the unique owner `continuum.capture`, records the direct rigidbody force
applications without suppressing or changing the stock calls, attests the installed patch graph, and removes only its
owned patches before exporting. The flight panel starts and stops the recorder. A valid receipt requires the exact
assembly hash and MVID below, the expected owned hooks at inspection time, and confirmed cleanup; otherwise the run
publishes no partial samples.

This code compiles against Lib.Harmony but does not package `0Harmony.dll`. The package command emits local CKAN
metadata that declares the shared `Harmony2` dependency from HarmonyKSP and binds the exact archive by size and hashes.
The metadata defaults to the local archive. `--download-url` accepts an absolute `file`, `http`, or `https` URI when the
CKAN runtime cannot resolve the host path, while the recorded hashes continue to bind the locally built archive. The
repository does not yet publish release or NetKAN metadata. Source compilation and portable lifecycle tests do not
establish patch coexistence, callback ordering, field units, or receipt validity in KSP.

The automated entrypoint is `--continuum-aero-capture-001`. Launch an owned qualification instance directly into a
controlled stock atmospheric flight save with KSP's `-loadfile` argument plus `-batchmode -nographics`. The addon waits
for one active, unpacked vessel at ordinary time scale, observes a two-second stable eligibility window, captures the
declared 64-sample bound, writes `aero-capture.json`, `status.txt`, and `shutdown.txt` beneath a uniquely named
`GameData/KspContinuum/PluginData/aero-capture-001-*` directory, then exits. It exits with code 0 only when the capture
receipt is valid, owns all 64 samples, and confirms removal of its Harmony hooks. An ineligible start or incomplete run
times out after 120 wall-clock seconds and exits with code 2. The selected save is an external immutable input to the
experiment; the addon does not create, overwrite, or persist game state.

1. Pin the stock assembly hash/MVID and exact callback dataflow.
2. Capture at most 64 samples and 128 parts per sample immediately after stock per-part aero calculation.
3. Record immutable atmosphere, flow, geometry, drag-cube, exposure, force, application-point and torque data with exact frames and units.
4. Validate force/torque aggregation, frame round trips, torque origin shifts and receipt hashes in a portable fixture.
5. Collect one-part and small rigid craft across altitude/density, speed/Mach, angle of attack and orientation, followed by a held-out craft.
6. Compare a stock-style analytic baseline and ridge regression offline. Stop before adding a larger model unless label completeness and residual structure justify it.

Current force receipts are not training data for stock aero. They omit stock aerodynamic rigidbody writes. Post-step velocity deltas also mix gravity, frame changes, contacts, constraints and other direct writes.

`continuum-tools aero-compare RECEIPT [RECEIPT ...] --output REPORT` evaluates the deterministic drag-cube baseline against the captured body-drag publications. The report separates finite comparisons from abstentions, summarizes force magnitude, force vector, direction and torque errors, and buckets results by dynamic pressure and Mach regime. It rejects malformed capture contracts and refuses to combine different receipt-wide provider fingerprints. The capture format has only receipt-wide provider provenance, so the comparison cannot independently prove per-publication provider homogeneity; that limitation is recorded in every output report.

## Evaluation

Split by complete attempt, trajectory, craft design and craft family. Adjacent physics steps are near duplicates and may not cross train/test boundaries. Keep provider and version splits exact. Required adversaries include zero density/speed, transonic flight, high angle of attack, spin, staging, shielding changes, deployed surfaces and reentry extremes.

Measure per-part and aggregate force/torque error, coefficient error, direction error, one-step linear/angular impulse, p95/p99/p99.9 and worst case by regime. Then run open-loop trajectory comparisons for velocity, attitude, orbit, heating and event timing where applicable. A low average one-step error does not qualify a rollout.

Metamorphic checks cover translation invariance, rotation and permutation equivariance, torque origin-shift law, zero-flow response, density/dynamic-pressure scaling, symmetric craft and reflection. For passive fixed geometry, aerodynamic power should not create energy beyond a declared tolerance; moving control surfaces and provider-specific behavior require separate treatment.

Performance measurements include oracle time, feature capture, geometry rebuild, packing, queueing, inference, validation and publication. Active-vessel latency and fleet throughput are separate objectives. Compare scalar and SIMD/parallel CPU paths before GPU or Core ML; small batches may cost less than accelerator dispatch.

A learned candidate stays shadow-only unless it improves the analytic baseline on held-out missions without worsening tail behavior, emits finite deterministic results, recognizes unsupported inputs and is faster end to end. Unknown providers, contacts, water, topology transitions and unsupported regimes produce an explicit abstention and exact fallback.
