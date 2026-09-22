# Read-only lifecycle trace

The opt-in lifecycle trace records callback order and the body/force state visible at each observation. It is an implementation candidate for the [state-ownership investigation](ksp-state-ownership.md), not an exclusive-writer protocol or a live physics replacement. Portable tests exercise the real capture class against managed API doubles; native compilation checks the KSP API surface. Installed callback order and the flight-panel experience remain unqualified.

## Start and stop

In a supported KSP 1.12.5 flight, the panel exposes **Start read-only lifecycle trace** and **Stop lifecycle trace**. A future qualification launch can request the same operation with `--continuum-lifecycle-trace`. Normal launches do not start it. This development slice does not install or launch the game.

The trace can arm while the vessel is not yet ready. It starts retaining events at the first eligible loaded, unpacked, normal-rate observation. Arming callbacks are counted separately and are not presented as captured physics evidence. The same 30-second deadline covers arming and capture. Missing or ambiguous native timing stages make registration unavailable immediately.

The host supplies ordinary `Update`, `FixedUpdate` and a coroutine continuation after `WaitForFixedUpdate`. The capture also registers its own delegates with `FashionablyLate`, `FlightIntegrator` and `BetterLateThanNever`. Registration is checked against the retained concrete native stage objects; cleanup removes only the capture's delegates. Stopping, disabling or destroying the host stops its owned trace coroutine and finalizes the receipt. A finished trace is exported before another one replaces it.

## Native physics-boundary qualification

`--continuum-qualify-physics-boundary` or **Qualify native physics boundary** runs a separate opt-in qualification. It does not infer solver order from KSP callback names. It installs owned callbacks immediately before and after the single `UnityEngine.PlayerLoop.FixedUpdate.PhysicsFixedUpdate` node, creates a hidden collider-free rigidbody with gravity and collision disabled, and performs three one-step axis trials. Each trial records the requested velocity and raw position before and after the native target. A receipt qualifies only when all three displacements equal one fixed step, both callbacks remain uniquely installed at every boundary, and cleanup removes the hooks and requests destruction of the probe object.

The probe does not touch the active vessel or save. Its content-derived qualification ID binds the target, callback identities, installed build versions, clocks and raw observations. `StructuralExperimentReport` embeds that complete receipt and rejects a free-form ID, mismatched callback pair or mismatched provenance. Portable fixture evidence can test the validator but cannot unlock a native experiment.

This qualifies the installed direct PlayerLoop bracket for that build. It does not qualify KSP's named `TimingManager` stages, structural response, deterministic replay, or a replacement solver. The installed run is still required; a native build and portable test only establish API and contract compatibility.

Each capture has one session ID and monotonically increasing event sequence. Stage names record where observations occurred. The host FixedUpdate counter is **not** a certified native physics-step identity, and the final group can be partial. Equal counters from separate providers are not joined.

## Observation boundary

Events retain clocks, main-thread identity, vessel/frame/topology context and raw part force deposits. Available Rigidbody samples include position, quaternion, linear and angular velocity. Missing bodies have explicitly unavailable pose data. Raw part force deposits remain component observations; gravity, stock aerodynamics, contacts, constraints and direct Rigidbody writes are not reconstructed as a total force.

The tracer copies on the main thread and rechecks its context. Observed topology, Rigidbody kinematic mode, origin/frame, eligibility or scene changes terminate the capture with an explicit outcome. A change followed by a return to the old state entirely between observations may go undetected; the subscribed floating-origin event counter does retain those shift notifications. This is conservative invalidation of this trace, not a universal mutation watcher or control over another plugin's jobs. The existing [shadow capture](shadow-worker.md) retains its own authority and invalidation checks.

The capture observes at most 120 host FixedUpdate callbacks, 512 parts per event and 8,192 events. It separately bounds retained part samples and force holders, and refuses an entire event when adding it would exceed a retention budget. Encoded event data has a 3 MiB budget; the final export has a 4 MiB bound. A monotonic 30-second timeout is checked by host Update even when physical stepping is paused; it is a cooperative deadline checked when the host executes, not a real-time scheduling guarantee. A disabled host disposes the trace.

The tracer never calls `Physics.Simulate`, changes automatic simulation or kinematic state, injects forces, writes poses, stages a craft, or commands warp. Its effects are registration/removal of its own observers, allocation/copying and report export. Those observers have overhead, so this is a diagnostic capture rather than a performance-neutral measurement.

## Receipt and offline analysis

Final receipts use `ksp-continuum-lifecycle-trace/v1` and are written to the plugin's local `PluginData` directory with unique names. They include status, cleanup outcome and explicit evidence/qualification fields. A failed export is surfaced in the panel/log rather than silently retried every frame. Local receipts may contain vessel and assembly identities; they are not committed to the public repository.

The offline consumer summarizes observed stage order without asserting what the correct order should be:

```sh
dotnet run --project tools/KspContinuum.Tools -c Release -- lifecycle-report --input /path/to/lifecycle.json --output artifacts/lifecycle-summary.json
```

The output path must be new. The reader rejects malformed or oversized input and inconsistent sequence/context evidence. It preserves test-double provenance and terminal status. Grouping by the observed host counter is a viewing convenience, not proof that those records form a complete physics step.

## Qualification

```sh
dotnet run --project tests/KspContinuum.LifecycleTrace.Tests -c Release
dotnet run --project tests/KspContinuum.Tools.Tests -c Release
```

The portable capture test can export an explicitly labeled fixture with `--export-fixture NEW_PATH`. That fixture verifies serialization and the real offline consumer path. It is not a KSP recording. Native Plugin/Mission builds use the same locally owned game references as the existing [build instructions](../README.md#build); they do not install the result.

Before any live ownership experiment, collect actual receipts across coast, an externally initiated topology change, origin shift, pause/packing and scene exit in an owned qualification instance. Cases not observed must remain unobserved. A trace can expose ordering and invalidation problems; it cannot prove exhaustive writer detection, complete force capture or safe multi-object publication.
