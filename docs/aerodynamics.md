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
`continuum-drag-cube-projection/v2`. It projects relative airflow onto the six
captured drag-cube faces, blends each cube's weight, area and drag coefficient,
applies dynamic pressure, and maps the weighted cube center back into
the world frame. Scalar and bounded parallel entry points run the identical
per-part calculation; the parallel path preserves input order and performs no
shared reduction. KSP's runtime `DragCubeList` weights the cube `Drag` arrays;
the separately captured `DragModifiers` values describe generation inputs and
are retained as provenance rather than multiplied into the runtime coefficient.

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

Capture schema v2 adds one fixed-size stock scalar label to every body-drag publication: runtime drag-cube `AreaDrag`,
part dynamic pressure, the flight integrator's pseudo-Reynolds, cached drag-cube and cached global drag multipliers, and
the final part `dragScalar`. Mach remains in the shared part context. These read-only values separate projected-area
error from stock magnitude multipliers without retaining KSP objects or expanding the sample and part bounds. The
ocean scalar remains unobserved, so submerged samples cannot close the full stock magnitude product.

Capture schema v3 adds the bounded inputs required to reproduce stock `DragCubeList.SetDrag` area drag: six
post-attachment-occlusion face areas in square meters, six post-occlusion weighted drag coefficients, the four surface
curves (tail, surface, multiplier, and tip), and `DragCurveCd` plus `DragCurveCdPower`. Each curve contains at most 64
ordered keys with time, value, in/out tangents, in/out weights, weighted mode, and pre/post wrap modes. Its SHA-256 is
derived from those exact parameters and is checked again while parsing. The adapter copies these values during the
existing read-only callback and retains no Unity or KSP object references.

`AeroSetDragReconstruction` implements the installed stock `SetDrag` area reduction without reading stock's computed
`AreaDrag` label. It supports ordinary unweighted curve keys plus captured `ClampForever` pre/post-wrap behavior and
abstains on weighted keys or unsupported extrapolation. The comparison report records area-drag errors separately from the older stock-scalar product,
which remains diagnostic because it consumes stock-computed magnitude terms. See
[`aero-set-drag-reconstruction.md`](aero-set-drag-reconstruction.md) for the frozen hypotheses, tolerances, and the
passing development/held-out capture-v3 qualification gate. Body lift remains excluded.

`AeroCompleteSetDrag` extends that bounded reduction to every `DragCubeList.CubeData` output: drag vector, lift force,
area, area drag, depth, cross-sectional area, exposed area, drag coefficient, and taper. Unity remains responsible for
curve evaluation; the portable kernel receives the resulting samples and performs the allocation-free six-face
reduction. The opt-in `--continuum-live-setdrag-provider` addon warms 32 complete candidate/stock comparisons,
then compares and times 256 inputs before suppressing and replacing exactly 256 original `SetDrag` calls. Schema v2
records `Stopwatch.Frequency` plus
patch-graph inspection count. Its `maximumRelativeError` field is the maximum absolute error normalized by
`max(1, abs(stock))`, so values below one use an absolute-error scale. Any mismatch, nonfinite result, unsupported KSP version, or
competing patch on `SetDrag` stops substitution and leaves subsequent calls to stock. `UpdateAerodynamics`, body-drag
and body-lift application, ocean handling, and Unity integration remain stock-owned. A mod such as KSP Community Fixes
that inlines this reduction in a broader `UpdateAerodynamics` replacement can bypass the `SetDrag` seam entirely; the
bounded provider makes no compatibility claim for that configuration.

The separate opt-in `--continuum-setdrag-stress-candidate` mode extends the verified provider across one complete
4x atmospheric profiling window. Its whole-game measurements and ownership-audit limitation are in
[profiling](profiling.md#atmospheric-4x-setdrag-experiment).
Add `--continuum-setdrag-quit-after-qualification` for an automated run that exits with code 0 only after a complete
receipt and verified patch removal; abstention or cleanup failure exits with code 2.
When combined with the existing scale-checkpoint loader flags, this provider-owned termination mode leaves the orbital
scale profiler inactive so it cannot overwrite the powered-descent qualification's exit status during teardown.

Two fresh headless runs loaded the immutable `scenarios/Powered Landing` checkpoint and exited 0 after verified cleanup.
The final run used source `11aaea33584a5311db2c740af2a589e52a84bd68`, package SHA-256
`397a7e6ffe1c0ad27c683009a47ce500c3ddf0b8e0e3fbae2e02274b6dde9f86`, and installed plugin SHA-256
`7ba49d9afa36f6a643606c75556d047150028596b652366e642430c2470a8eaf`. It matched all 128 complete stock outputs,
suppressed 256 original calls, recorded zero fallbacks, removed its owned patches, and observed maximum relative error
`2.1706087635072911e-6`. The checkpoint remained byte-identical at SHA-256
`f9cafd86957b99b9fe58eac61963678ca068cee2de04aeaaba8c31e3a1b6d2e6`.

The precursor run from source `702991fe0582d309fa86062c21a01d06b57fd99f` independently passed the same
128-match/256-suppression/zero-fallback gates with maximum relative error `2.5471993994525494e-6`, verified cleanup,
exit 0, and the same unchanged checkpoint. Its package SHA-256 was
`c3178570a688210bb0ab0a014f517a3553277ac77affcfe36d001e2421281ea7`; its installed plugin SHA-256 was
`d9bb3e67ff125716b9fef853a57b5179b45dbda9d432ba97292cf3125cab65d6`.

The first replacement was slower in its measured section. The final run recorded 63,631 candidate ticks over 384
calls and 15,790 stock ticks over 128 shadow calls: 165.71 versus 123.36 ticks per call, or about 1.34 times stock.
The precursor run observed 166.73 versus 123.61 ticks per call, or about 1.35 times stock. Candidate and stock samples
come from different phases and counts, include first-use and JIT effects, and exclude surrounding publication and
patch-graph validation. They are diagnostic method-section averages, not a controlled end-to-end ratio, frame-rate
result, or full-provider measurement. The useful result is real suppression with complete-output parity and an exact
optimization baseline; no speedup is claimed.

The strategy qualification separately executes admission-boundary and guarded direct reductions for each of 256 warmed
inputs, alternating their order, then executes stock last so stock remains authoritative during replay. Each direct
window includes curve capture, six-face reduction, fail-closed validation, and `CubeData` publication. Guarded time adds
the aligned patch-graph readback actually performed for that input; it excludes small wrapper, stopwatch, and accounting
costs. Harmony dispatch, postfix comparison, receipt work, one-time authority admission/exit checks, the rest of
`FlightIntegrator`, and rendering remain excluded. This is a bounded method comparison, not an FPS or complete
physics-tick measurement. The later 256-call authority window intentionally freezes the provider topology after an
admission check and checks it again at exit; it cannot detect a patch that appears and disappears inside that window.

The installed run used source `31c91896caaa6f61080679b1d9bf68fee5bf5fa0`, package SHA-256
`d74931464fe8752a36269003b5416baf5261b24b7220ab3198df0c21294157c7`, and plugin SHA-256
`aa70ef06576ce28f15daa5fdddf94de88d6ce295ae15e0d42abcbc38cc98c693`. It exited 0 after 288
complete-output matches, 256 suppressed originals, zero fallbacks, successful admission and exit attestations, and
verified cleanup. Maximum normalized absolute error was `4.14396e-6`. At a 10 MHz stopwatch frequency, 256 separately
executed admission-boundary calls used 2,646 ticks (1.0336 microseconds each), stock used 4,812 ticks (1.8797
microseconds each), and guarded calls used 17,355 ticks (6.7793 microseconds each), including 15,217 aligned graph
ticks. The admission-boundary calculation and publication was about 45.0% faster than stock at this seam. This does not
establish a fixed-tick or frame-rate gain; the next useful boundary is a coarse Continuum-owned batch rather than more
per-part callback tuning.

The installed Harmony 2.2.1 implementation of
[`GetPatchInfo`](https://github.com/pardeike/Harmony/blob/v2.2.1.0/Harmony/Public/PatchProcessor.cs#L202-L208)
locks shared state, whose
[`GetPatchInfo`](https://github.com/pardeike/Harmony/blob/v2.2.1.0/Harmony/Internal/HarmonySharedState.cs#L114-L121)
deserializes the stored patch bytes before the public API constructs the returned patch arrays. The measured 15,217
graph ticks explain most of the guarded strategy's cost without justifying private Harmony-state coupling.

This code compiles against Lib.Harmony but does not package `0Harmony.dll`. The package command emits local CKAN
metadata that declares the shared `Harmony2` dependency from HarmonyKSP and binds the exact archive by size and hashes.
The default archive and metadata filenames include the archive's full SHA-256, and the metadata points to that immutable
local filename. `--download-url` accepts an absolute `file`, `http`, or `https` URI when the
CKAN runtime cannot resolve the host path, while the recorded hashes continue to bind the locally built archive. The
repository does not yet publish release or NetKAN metadata. Source compilation and portable lifecycle tests do not
establish patch coexistence, callback ordering, field units, or receipt validity in KSP.

The automated entrypoint is `--continuum-aero-capture-001 --continuum-aero-save scenarios
--continuum-aero-checkpoint "Jool Aerobrake"`. Launch an owned qualification instance from its native main menu with
`-batchmode -nographics`. On the frame after the menu's GUI-ready event, the persistent addon loads the named existing
checkpoint through `GamePersistence`, verifies the loaded game and active-vessel identities, and starts flight. It waits
for one active, unpacked vessel at ordinary time scale, observes a two-second stable eligibility window, captures the
declared 64-sample bound, writes `aero-capture.json`, `status.txt`, and `shutdown.txt` beneath a uniquely named
`GameData/KspContinuum/PluginData/aero-capture-001-*` directory, then exits. It exits with code 0 only when the capture
receipt is valid, owns all 64 samples, and confirms removal of its Harmony hooks. An ineligible start or incomplete run
times out after 120 wall-clock seconds and exits with code 2. The selected save is an external immutable input to the
experiment; the addon hashes it before and after the run and does not create, overwrite, or persist game state.

1. Pin the stock assembly hash/MVID and exact callback dataflow.
2. Capture at most 64 samples and 128 parts per sample immediately after stock per-part aero calculation.
3. Record immutable atmosphere, flow, geometry, drag-cube, exposure, force, application-point and torque data with exact frames and units.
4. Validate force/torque aggregation, frame round trips, torque origin shifts and receipt hashes in a portable fixture.
5. Collect one-part and small rigid craft across altitude/density, speed/Mach, angle of attack and orientation, followed by a held-out craft.
6. Compare a stock-style analytic baseline and ridge regression offline. Stop before adding a larger model unless label completeness and residual structure justify it.

Current force receipts are not training data for stock aero. They omit stock aerodynamic rigidbody writes. Post-step velocity deltas also mix gravity, frame changes, contacts, constraints and other direct writes.

`continuum-tools aero-compare RECEIPT [RECEIPT ...] --output REPORT` evaluates the deterministic drag-cube baseline against the captured body-drag publications. The report separates finite comparisons from abstentions, summarizes force magnitude, force vector, direction and torque errors, and buckets results by dynamic pressure and Mach regime. It rejects malformed capture contracts and refuses to combine different receipt-wide provider fingerprints. The capture format has only receipt-wide provider provenance, so the comparison cannot independently prove per-publication provider homogeneity; that limitation is recorded in every output report.

For the first SetDrag qualification, collect two independent v3 receipts from separate stock runs. Use the first while developing the reconstruction, keep the second untouched, then run `continuum-tools aero-compare development.json --held-out held-out.json --output set-drag-qualification.json`.

The tool rejects the same path, identical bytes, or the same capture session in both roles, requires an identical stock provider fingerprint, and reports development and held-out metrics separately. Both splits must pass the numeric gate before `qualifiedHeldOutGate` becomes true. That gate applies only to the bounded SetDrag area reconstruction; it does not authorize force publication or claim parity for lift, ocean drag, trajectories, or the complete stock aerodynamic provider. The capture remains observational and does not mutate KSP physics.

## Evaluation

Split by complete attempt, trajectory, craft design and craft family. Adjacent physics steps are near duplicates and may not cross train/test boundaries. Keep provider and version splits exact. Required adversaries include zero density/speed, transonic flight, high angle of attack, spin, staging, shielding changes, deployed surfaces and reentry extremes.

Measure per-part and aggregate force/torque error, coefficient error, direction error, one-step linear/angular impulse, p95/p99/p99.9 and worst case by regime. Then run open-loop trajectory comparisons for velocity, attitude, orbit, heating and event timing where applicable. A low average one-step error does not qualify a rollout.

Metamorphic checks cover translation invariance, rotation and permutation equivariance, torque origin-shift law, zero-flow response, density/dynamic-pressure scaling, symmetric craft and reflection. For passive fixed geometry, aerodynamic power should not create energy beyond a declared tolerance; moving control surfaces and provider-specific behavior require separate treatment.

Performance measurements include oracle time, feature capture, geometry rebuild, packing, queueing, inference, validation and publication. Active-vessel latency and fleet throughput are separate objectives. Compare scalar and SIMD/parallel CPU paths before GPU or Core ML; small batches may cost less than accelerator dispatch.

A learned candidate stays shadow-only unless it improves the analytic baseline on held-out missions without worsening tail behavior, emits finite deterministic results, recognizes unsupported inputs and is faster end to end. Unknown providers, contacts, water, topology transitions and unsupported regimes produce an explicit abstention and exact fallback.
