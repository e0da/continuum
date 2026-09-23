# Observational flight profiling

The flight panel's existing **Capture 300 frames of available timing markers** button now writes `ksp-continuum-markers/v2` JSON in the local `GameData/KspContinuum/PluginData` directory. Capture remains opt-in. It changes recorder enable flags temporarily; it does not change the vessel, time warp, global profiler enable state, or graphics settings.

The report contains the raw observations and their summaries:

- `frames` records coroutine boundary intervals measured with `Stopwatch`, native scene/UT, active-vessel ID, body/situation, part count, loaded/packed state, warp rate, pause state, fixed timestep, time scale and screen dimensions. Managed heap bytes and generation collection counts are observations, not allocation-rate measurements. A missing vessel has explicit status, a null identity and part count `-1`; unavailable optional context is null.
- `markers` retains elapsed nanoseconds, block counts and an `available` mask for each observation of Physics.Simulate, Physics.Processing, BehaviourFixedUpdate, BehaviourUpdate and GC.Collect. A false availability entry must never be interpreted as zero cost. Native invalid counters remain raw but are excluded from summaries. `availabilityDetail` describes setup failure or the latest read failure.
- Each marker summary distinguishes available, unavailable, positive-block and zero-block frames. Duration distributions include only available positive-block frames. No observations produce a null distribution, including a valid recorder that never emits a block. Marker times can overlap and must not be added to infer total CPU time or a physics percentage.
- New captures also census `rigidbodies`, `joints`, `colliders`, and `loadedVessels` for each profiler frame. The four fields are one complete observation: each value is a nonnegative count or `-1` when unavailable. The report summarizes the observed minimum and maximum plus unavailable-frame counts. Legacy frames may omit all four fields. Structural counts are the deduplicated union of every KSP-owned `Part` subtree, each explicit `Part.rb`, and the vessel-root subtree for auxiliary components. A `Vessel` component's Unity transform subtree alone is not the KSP part ownership graph. These counts do not distinguish enabled from disabled components or attribute time to any object class.
- `wallIntervals` contains count, minimum, maximum, mean, p50, p95 and p99 of the callback intervals. Percentiles use linear interpolation at `(count - 1) * quantile`. These intervals include scheduling, waiting, rendering cadence and probe overhead. They are not CPU execution durations or a performance improvement measurement.

Unity 2019.4's [Recorder](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Profiling.Recorder.html) reports the preceding frame's accumulated marker time and [block count](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Profiling.Recorder-sampleBlockCount.html). The probe skips its partially enabled first frame. `markerFrame` identifies the previous native frame, `observedFrame` identifies the read, and `contextFrame` identifies the earlier context boundary. `contextAligned` and the report's misaligned-frame count expose gaps; even aligned context is one boundary observation, not proof that the vessel or warp state stayed constant throughout the frame.

The report records KSP/Unity/plugin versions, platform, processor and graphics labels, target frame rate and v-sync setting. These describe this capture's environment; they do not control other processes or establish comparable workloads.

## Completion and ownership

Normal completion exports 300 observations with status `complete`. Disposing a running probe exports only its completed prefix with status `interrupted`; unobserved array tails are removed. The report callback runs at most once. Recorder enables acquired by the probe are released before export, and a failed partial-report write is logged without preventing teardown. Cleanup failures are explicit in the report. A destroyed process or failed filesystem write can still prevent a durable receipt.

Only one Continuum probe can run at a time. Unity recorders are shared engine objects, so concurrent external changes to their enable flags are not an independently owned lease; avoid changing those flags during a capture. Previously enabled recorders are left enabled by this probe, and only its own initial enables are undone.

## Qualification boundary

Portable tests cover raw-marker availability, no-block observations, distribution arithmetic, invalid readings, partial-prefix trimming and nested JSON export. Native compilation checks the installed KSP 1.12.5 / Unity 2019.4 API surface. Neither establishes marker availability or capture behavior in a release player.

A007 completed three normal 300-frame captures with no physics/update marker samples, as recorded below. Installed qualification remains open for packed/warp transitions and interruption through a scene transition, including partial export and recorder cleanup. Missing samples identify an instrumentation gap; they do not establish zero physics cost or PhysX dominance.

## Controlled qualification workload

The opt-in `--continuum-qualify` flight addon observes an externally launched
flight. It starts a 300-frame marker capture and a separate shadow-worker capture
at each of three sequential conditions: airborne with throttle command below
0.01, airborne with throttle command above 0.05, and landed or splashed. The
flight mission or player supplies those states; this addon does not steer the
vessel. Each frame now includes nullable `throttleCommand` so the report can
show whether the window remained in its starting context. A throttle command
is not proof of thrust, and a landed situation is not collision-cost attribution.

Receipts are written beneath a new `PluginData/qualification-*` directory. Each
window has a start-context text receipt, marker JSON and the completed shadow
JSON. The addon exits the process after all windows or a 600-second flight
limit. Use it only in a disposable test launch: it is intentionally an automatic
exit harness. `complete` means the three receipt windows completed, not that
all shadow samples were accepted, markers were available, or physics matched.
Marker timing includes the running shadow observer and other addons; it is not
an instrumentation-free baseline. Inspect source frames and shadow acceptance
before drawing conclusions.

## Native qualification observation: CSP-0002-A007

An isolated KSP 1.12.5 instance ran the installed qualification package from the preserved Minmus-orbit checkpoint at 1920 × 1080. The harness completed all three windows, exited with code 0, and the closed-instance receipts were preserved. This is a collection result, not a replacement-physics qualification.

| Start-classified window | Profiler frames | Accepted / submitted shadow batches | First accepted dynamic bodies | Frame-interval median / p95 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Coast | 300 | 120 / 120 | 10 | 4.500 / 8.396 |
| Powered | 300 | 120 / 120 | 10 | 4.347 / 10.332 |
| Contact | 300 | 120 / 120 | 10 | 7.894 / 13.279 |

The four physics/update markers were `available-no-samples` in all three windows: 300 available frames and zero observed blocks per marker per window. `GC.Collect` was observed in two powered frames (three blocks) and two contact frames (two blocks), with median observed durations 2.654 and 2.475 ms; coast had no GC samples. These observations did not measure physics cost. The callback intervals include the instrumented workload and are not an uninstrumented FPS benchmark or a causal attribution. The powered window contained 143 frames with positive throttle command and 157 near-zero frames; its start label does not imply continuous thrust.

The worker reported no stale results in these windows. It used zero force and a constant-velocity oracle, proving live capture/transport and freshness checks under this workload, not captured stock forces or trajectory agreement. Stale-result rejection has separate synthetic epoch-transition tests. Median capture cost was 0.102, 0.090 and 0.111 ms; median handoff latency was 0.621, 0.652 and 0.939 ms. Those scopes overlap and must not be added as a total simulation cost.

Qualification quit after the landed capture, before the mission's settling acceptance completed. The preserved mission receipt still says `running`; it is an interrupted mission attempt with no terminal pass/fail verdict. The independent qualification receipt says `complete`. Neither should overwrite the other. On application teardown, the pre-existing mission cleanup logged two `FlightGlobals.ActiveVessel` null-reference failures after the flight singleton was destroyed. Capture receipts were already complete, but this run does not prove graceful mission cleanup or complete settings restoration.

Package SHA-256: `bd2680ab2e17fdc1c6a9f7c905658cfa316bf1c7ddc8055fb5837b5ae295b64c`. Parent checkpoint SHA-256: `6993575ee97eb6f9529d6f91187e63909dc2aad424e477905a4a86a063445240`, verified unchanged after exit. Native receipts do not themselves bind the package or attempt; a separate local capture/install record preserves that association. The [qualification report tool](qualification-report.md) renders the raw window receipts without promoting collection completion into a physics claim.

Next: qualify a supported timing source for actual force callbacks and native solving before selecting a replacement bottleneck. More landing precision tuning is not a prerequisite.

## Graceful qualification shutdown

The follow-on shutdown path requests cancellation before `Application.Quit`, while flight objects still exist. An optional callback registry in the shared plugin assembly avoids a dependency on the mission addon. Only an opted-in mission registers. The harness first stops its capture owners, then dispatches each registered cancellation handler once, writes `shutdown.txt`, and exits. A handler failure does not prevent the other handlers from running. Dispatch has no wait loop or retry; it does not provide an operating-system timeout for synchronous file I/O.

An active mission cancels its progression, releases its acquired controllers and settings, closes its recording and telemetry, and appends `status=interrupted`, `reason`, `shutdownContext`, and `cleanupStatus` to its existing mission receipt. It does not fabricate a landed save, screenshot, pass, or failed-flight verdict. A prior passed/failed terminal outcome is not rewritten. Repeated cancellation and later destruction do not repeat released actions.

The qualification's `status.txt` remains the capture outcome. Its separate `shutdown.txt` uses `ksp-continuum-shutdown/v1`, records `status=complete|error`, the original `captureStatus`, registered handler results, and matching handler/error counts. The `mission` handler returns `interrupted`, `already-terminal`, `inactive`, or `error`; `error` includes cleanup failure, an unavailable flight owner, or an interrupted-receipt export failure. The `qualification-capture` handler reports `inactive` after capture disposal, or `error` if disposal throws. No mission handler means no mission-cleanup claim. A shutdown failure changes the requested process exit code to 2 without rewriting the capture outcome.

If mission destruction occurs without this handshake, late cleanup checks flight-owner availability before invoking flight-dependent actions. Skipped restoration is recorded as `cleanupStatus=flight-unavailable` with the skipped owner names. Other owned cleanup and file closing still run; the receipt does not call the skipped work successful. This guards the vanished `FlightGlobals` path observed in A007, whose historical receipt remains unchanged.

Portable tests cover handler isolation, exactly-once dispatch, unregistration, interrupted outcome retention, and late flight-owner loss. Source/native-build checks do not establish graceful installed shutdown. A new closed-instance qualification must verify the interrupted mission receipt, clean callback/resource release, separate shutdown outcome, and teardown logs before that claim is made.

### Opt-in PlayerLoop boundaries

Add `--continuum-playerloop` to an explicitly started timing/qualification run to enable the experimental boundary sampler during each existing Probe capture. Without that flag, `playerLoop` is null and no loop changes are requested. Existing two-scope receipts retain schema `ksp-continuum-playerloop/v1`. New five-scope receipts use `ksp-continuum-playerloop/v2`; existing Recorder arrays retain their previous-frame meaning.

Unity 2019.4 supports reading the [current PlayerLoop](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/LowLevel.PlayerLoop.GetCurrentPlayerLoop.html) and [inserting script callbacks](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/LowLevel.PlayerLoop.SetPlayerLoop.html). The installed managed API exposes the measured targets. Version 2 records exactly five named scopes:

- `UnityEngine.PlayerLoop.FixedUpdate`, with `timeDomain=fixed` and `overlap=contains-fixed-children`;
- `UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate` and `UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate`, each with `timeDomain=fixed` and `overlap=contained-by-fixed`;
- `UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate` and `UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate`, each with `timeDomain=frame` and `overlap=separate-frame-phase`.

Continuum brackets each unchanged target with timestamp callbacks. It preserves existing native pointers, delegates, subtrees and ordering; it does not call physics itself, change the simulation timestep, or replace native work. The FixedUpdate parent contains the two measured fixed children, so those three durations overlap and must not be added. Update and LateUpdate are separate rendered-frame phases; their frame-domain samples are not fixed-step observations. The portable report validates these names and relationships and displays an overlap warning.

Each raw sample contains `frame`, `fixedTimeSeconds`, `fixedDeltaSeconds` and `elapsedTicks`. The legacy field names carry `Time.fixedTime`/`fixedDeltaTime` for fixed-domain scopes and `Time.time`/`deltaTime` for frame-domain scopes; `timeDomain` disambiguates them. Convert elapsed ticks using the report's `clockFrequency`. A rendered frame can have zero, one or several fixed-step samples. Unity's float clocks are not KSP universal time.

The measurement is elapsed wall time around each selected subtree, including blocking and callback overhead. Work outside a subtree and asynchronous work continuing after it are outside that interval. The scopes do not provide complete attribution of a frame and do not identify any particular mod. `timerReadFloorTicks` is the minimum of 128 immediate timestamp-read pairs before installation. It is only a timer-read floor, not a measurement of full instrumentation overhead, and is never subtracted. No physics-dominance or speedup claim follows from these samples.

### Stock/candidate A/B

`continuum-tools profile-compare --stock STOCK.json --candidate CANDIDATE.json` compares two completed `markers/v2` captures. It fails closed unless both PlayerLoop captures have verified integrity and cleanup, contain all three fixed-step scopes without dropped samples, and match frame-by-frame on vessel identity, part/collider counts, loaded-vessel count, scene, body, situation, screen size, timestep, time scale, throttle, warp, load/pack and pause state. Every comparison requires a `--substitution-id`, `--substitution-status verified`, and explicit `--stock-rigidbodies`, `--stock-joints`, `--candidate-rigidbodies`, and `--candidate-joints`; each frame must match its declared side. This admits an intended 128-body/127-joint to one-body/zero-joint transition while rejecting undeclared or mismatched changes and retaining stable collider, part and loaded-vessel counts. It reports signed mean and p95 deltas plus speedup percentages for callback cadence, the complete FixedUpdate subtree, native `PhysicsFixedUpdate`, and script `FixedUpdate` callbacks. The FixedUpdate parent contains the two child scopes; never add them.

For the first live substitution experiment, clone one settled vacuum-orbit save containing a high-part-count jointed vessel. Run one 300-frame stock capture and one 300-frame candidate capture from separate fresh process starts with the same save, camera, normal-rate timestep, disabled engines/SAS/RCS, and `--continuum-playerloop`. The candidate changes only structural representation: replace an eligible rigid cluster's internal rigidbodies and joints with one compound body while preserving its colliders and external connection map. Alternate stock/candidate run order across at least three pairs. Retain each raw receipt and compare each pair; a lower whole FixedUpdate duration with a lower `PhysicsFixedUpdate` duration and stable script duration attributes the gain to the native structural solve. A lower callback interval alone is insufficient, and a changed census is expected only where the candidate receipt explicitly binds the cluster substitution.

This is the nearest performance-oriented takeover seam because the existing isolated benchmark already holds collider geometry constant and shows a measurable gap between joint chains and compound bodies. The 128-collider fixture averaged about 0.259 ms per step as 128 bodies with 127 joints versus about 0.164 ms as one compound body. That synthetic result selects the experiment; it does not predict a live-vessel speedup. Aerodynamic final-force replacement remains valuable for compatibility and fidelity, but the current evidence does not show aero calculation dominating frame time.

Each scope retains at most 4,096 samples in a preallocated buffer. `droppedSamples` counts further complete pairs; retained-prefix distributions must be described as truncated when this is nonzero. Empty scopes are `no-samples`, with null distributions. Missing after-callbacks, crossed-frame pairs and callback failures invalidate the measurement. Summaries are suppressed for the entire capture if any scope has a sequence error or any loop audit fails; raw samples remain diagnostic evidence.

Audits at coroutine boundaries and before removal verify the selected subtrees, adjacent hooks, original fixed-step sibling order, and the ancestor path and ordering. Foreign additions outside the brackets can remain. Audits cannot detect a foreign edit that appears and disappears between observations. They also allocate/traverse the loop outside the measured brackets, so they can affect overall frame cadence. On completion or interruption, callbacks are disabled and only this capture's delegates are removed from the latest loop. A whole saved loop is never restored over foreign changes. If another owner attached work to a hook node, that foreign work remains; copied owned delegates are removed as well. Cleanup failure has its own status and cannot qualify a clean capture.

The managed-contract tests link the actual adapter against a small Unity API test double and test mutation, partial-set failure, incomplete pairs and idempotent removal. Native assembly compilation checks API compatibility. Neither proves native dispatch or safe scene-transition behavior. Actual KSP qualification still needs observed callbacks, complete and interrupted removal receipts, and a comparison with instrumentation disabled to assess perturbation. The earlier A007 marker evidence remains unchanged.

### Scalable orbital workload

`--continuum-scale-profile --continuum-playerloop --continuum-scale-parts=N` enables `LIVE-STRUCT-SCALE-001`. In a disposable qualification instance it waits for an unpacked, unpaused active vessel at normal time, in orbit, below 0.01 commanded throttle for ten seconds. It captures one 300-frame window and exits. The first and last frame census the complete KSP-owned part graph plus vessel-root auxiliaries; intermediate frames reuse that census to avoid a scale-dependent observer loop. A changed boundary census, vessel identity, part count, loaded-vessel count, or flight context invalidates the receipt.

Add `--continuum-scale-save SAVE --continuum-scale-checkpoint CHECKPOINT` to load one named checkpoint automatically. The persistent main-menu loader accepts safe leaf names only, hashes the source before loading, verifies the selected game and active vessel after Flight becomes ready, and requires the source hash to remain unchanged when the profiler completes. These flags do not enable the survey mission or aerodynamic capture.

The harness writes `markers.json` and a fail-closed `status.txt` under `PluginData/scale-profile-*`. Without the named-checkpoint flags it does not load a save. It does not construct a craft, control the vessel, replace physics, or establish a speedup. Separate controlled saves and repeated runs own the part-count series and instrumentation-off perturbation measurement.

#### Initial station observation

Two headless KSP 1.12.5 runs loaded the preserved station checkpoint with package SHA-256 `d2d437abdcb7a688afc687eb0b39be99f5fad9aea94d576d443830e04dd98191` from source commit `54bf455`. The source checkpoint hash was verified unchanged after both exits. Each observed graph contained 196 logical parts, 110 rigidbodies, 144 joints, 328 colliders and one loaded vessel. Both harnesses completed 300 rendered frames, exited with code 0, and reported intact PlayerLoop installation and cleanup with no dropped samples or sequence errors.

| Run | Fixed-step samples | Physics p50 / p95 (ms) | Script FixedUpdate p50 / p95 (ms) |
| --- | ---: | ---: | ---: |
| 1 | 184 | 1.1809 / 1.44207 | 3.0351 / 3.748355 |
| 2 | 207 | 1.1855 / 1.38052 | 3.0276 / 3.6922 |

These are child scopes within fixed-step work. A rendered frame can contain zero or multiple fixed steps, so the `FixedUpdate` parent distribution is not directly comparable to either child distribution, and percentile values must not be added. The observations are headless, include the installed mod and instrumentation workload, and do not attribute script time to aerodynamics or any individual system. The whole-game logs were not clean: they retained the pre-existing headless `MessageSystemAppFrame.Reposition` startup null-reference error seen in earlier captures. The completed loop receipts do not qualify unrelated startup behavior.

For scale only, the separate [native CPU layout benchmark](native-cpu-throughput.md) executes millions of independent synthetic body updates per second on native arm64. It excludes Unity, KSP callbacks, joints, contacts, capture and publication. Its compute throughput therefore identifies available off-game capacity, not removable time in this station trace. Repeated station runs, an instrumentation-off perturbation measurement and a controlled stock/candidate substitution remain required before claiming a live speedup.

#### Managed fixed-callback attribution

Add `--continuum-callback-attribution` to an otherwise unchanged scale-profile run to attribute the native `ScriptRunBehaviourFixedUpdate` scope to managed providers. The opt-in profiler discovers and Harmony-patches loaded, patchable, zero-argument `MonoBehaviour.FixedUpdate` methods. Its timer is active only inside the existing PlayerLoop bracket, so startup, `Update`, rendering and callbacks outside that subtree are excluded. Rows identify the declaring assembly, version, MVID, type and metadata token and report call count plus inclusive total, mean and maximum time.

The same run separately times the pinned stock `FlightIntegrator` seams for integration, aerodynamics, thermodynamics and solar/body/convection occlusion, plus `VesselPrecalculate.CalculatePhysicsStats`. These nested rows distinguish an expensive component callback from the stock routine it invokes. Raw inclusive counts and time retain every invocation. Separate outermost-per-method counts and time remove recursive self-double-counting and own the ranking order. Rows can still overlap other methods and their parent, so totals and percentiles must not be added. Harmony prefixes, other patches and callees can be inside a target's interval, so each row describes the installed provider chain at that method seam rather than isolated original IL.

The receipt fails qualification on discovery, callback, patch-cleanup or coverage errors. It records a clock-read floor, but that is not the complete Harmony perturbation. Quantify overhead with serial runs of the same immutable checkpoint and package: at least two ordinary `--continuum-playerloop` runs followed by at least two runs adding callback attribution. Compare the enclosing script scope across runs before interpreting callback totals. This is attribution evidence only; it does not authorize replacement or establish a speedup.

One installed paired observation used source `da653dc950447d3f99aeb6dd3d186806e0434af0`, package SHA-256 `1693bd3028fa361b70cd5523f3e22cbef0f625877a83030837934cf47143777d`, and plugin SHA-256 `438b6648cf526313b18858d8571d1744dbd2bd0ad5365c69822f0352c33bb5c6`. Both runs completed 300 rendered frames against the same 196-part station checkpoint, exited 0 and left its source SHA-256 `c5903b84cb1b041a02373b003f85ed1bd74a484997b8e1743f3e774f6f55407e` unchanged. The attribution run patched and removed all 136 discovered methods with zero scan or callback errors. Its enclosing script scope was 3.4206/4.05417 ms p50/p95 over 218 fixed steps; the immediately following attribution-off run was 2.9978/3.93128 ms over 157 steps. The 0.4228 ms p50 increase is about 14.1 percent in this pair and establishes material perturbation, not a universal correction factor.

Outermost-per-method means in the attributed run were 0.70957 ms for `FlightIntegrator.FixedUpdate`, 0.67048 ms for `FlightInputHandler.FixedUpdate`, 0.26942 ms for `FlightIntegrator.UpdateThermodynamics`, and 0.22355 ms for the recursive `FlightIntegrator.Integrate` entry. `Integrate` made 42,728 total calls but only 218 outermost calls; its raw 626.19 ms inclusive total demonstrates why recursive totals cannot rank providers. Six `ModuleDeployableSolarPanel.FixedUpdate` calls per step averaged 0.05971 ms each, but its base `ModuleDeployablePart.FixedUpdate` row is nested and must not be added. These rows identify concrete optimization candidates while retaining overlap and instrumentation limits.

No solar, body or convection occlusion seam ran inside this fixed-update window. Stock `FlightIntegrator.Update`, outside the measured subtree, owns steady `UpdateOcclusion(false)` work; absence here does not establish that thermal occlusion was idle or cheap. The separate script-update scope was about 1.21 ms p50 in the preceding observation, but this experiment does not attribute it. Update-scope attribution is a later experiment, not part of this fixed-callback result.
## Experimental dry-orbit buoyancy admission

The 196-part station attribution observed 23,980 stock buoyancy calls across 218 fixed steps (110 per step) and 51.8151
ms of instrumented inclusive time, or 0.23768 ms per step. That was only an upper bound because callback timing overhead
is material at this cadence.

The first `--continuum-dry-buoyancy` candidate used one Harmony prefix per part callback. It ran only for initialized,
settled dry parts of the active loaded vessel in a high `ORBITING` regime around an ocean body. The paired installed
experiment used source `810e40a`, package SHA-256
`4b9e47a4ffdc6e66666ac6b1f20ba4fc8f9bf80df131059bc72706dc2e9ccd9e`, plugin SHA-256
`12fb9edbbd120c4afaecbcee7f6205edc32a938546ffc58d125c14c578dd1a75`, and the unchanged station checkpoint. All four
runs completed and exited 0. Both candidate runs verified every dry publication, recorded zero fallback or callback
errors, and removed their owned prefix.

| Order | Prefix | Component bypasses | Script p50 / p95 (ms) | Physics p50 / p95 (ms) | Active fixed parent mean (ms) |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Off | 0 | 2.9456 / 3.62945 | 1.1558 / 1.45595 | 4.20405 |
| 2 | On | 22,550 | 2.9063 / 3.6597 | 1.2139 / 1.52186 | 4.23730 |
| 3 | On | 20,790 | 2.9040 / 3.87878 | 1.2059 / 1.5440 | 4.28663 |
| 4 | Off | 0 | 2.9460 / 3.5857 | 1.1408 / 1.4203 | 4.18168 |

The prefix reduced script p50 by about 0.04 ms, but the enclosing active fixed-step parent mean was about 0.8% and 2.5%
slower than its neighboring baselines. This experiment demonstrates no end-to-end gain. The component callbacks and
per-part prefix dispatch remained in the hot path.

The current strategy, identified as `playerloop-batch-disable` in new receipts, moves admission to the existing
PlayerLoop boundary. Before each `ScriptRunBehaviourFixedUpdate` traversal it revalidates the vessel and every owned
component, republishes and verifies dry integration state as one batch, and disables only the initially enabled
`PartBuoyancy` behaviours that Continuum owns. [Unity does not update disabled behaviours](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Behaviour-enabled.html). Any ineligible state, loop
fault, or teardown restores and reads back every owned enable independently before stock script traversal continues.
The batch report distinguishes fixed steps, owned components, component-step bypasses, publications, fallbacks, errors,
and cleanup. Its work runs before the script child timer begins, so only the enclosing active fixed-step parent can
establish a net gain.

Both strategies use a 10 km minimum clearance plus a two-tick swept descent bound and fall back for ambiguous, wet,
near-surface, packed, inactive, delayed-call, prior-wet, or nonfinite state. Diagnostic geometry and depth fields retain
their last dry values, so this remains an opt-in KSP 1.12.5 physical-equivalence experiment rather than a complete
stock-output substitute. Existing Harmony ownership is checked at installation; a foreign patch added later is not
detected. A verified publication establishes internal consistency after Continuum writes it, not equality with an
independently executed stock trajectory.

This boundary probe is not Continuum's permanent object-callback architecture. The follow-up is vessel- or island-level
domain selection over Continuum-owned state, with KSP reconciliation at explicit boundaries.
