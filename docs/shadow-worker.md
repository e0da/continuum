# Flight shadow worker transport probe

The opt-in flight probe captures actual active-vessel rigidbody data on Unity's main thread, transfers owned columns through `SimulationWorker`, and writes a JSON receipt. Stock KSP remains authoritative. The worker receives no Unity objects and the probe never applies its output to the vessel.

This is a transport and lifecycle experiment. Its backend predicts `position + velocity * dt` with zero force and unchanged velocity. Its analytic residual checks that arithmetic against the captured input. Accepted predictions also wait for the next eligible, matching one-boundary stock observation. The probe reports position and velocity discrepancies against that observation. Stock includes gravity, thrust, contacts and constraints omitted by the baseline, so these discrepancies are not solver accuracy, a physical error budget, or performance evidence.

## Activation

In the flight panel choose **Start read-only worker shadow capture**, then optionally **Stop shadow capture**. `--continuum-shadow` arms one capture when each opt-in flight panel starts; it does not launch a vessel or quit the game. No capture runs in normal sessions without a request.

A controlled harness can call `FlightPanel.BeginShadowCapture()` and `StopShadowCapture()`, observe `ShadowRunning` and `ShadowStatus`, and read `ShadowReportPath` after completion. These methods must run on Unity's main thread. Repeated calls after completion create independent receipts. Starting while another capture is active is rejected. The separate `--continuum-qualify` harness owns its window selection and process exit; the shadow probe owns neither.

The initial eligible state is a loaded, unpacked active vessel with dynamic part bodies, normal warp and time scale, no pause and no `HoldPhysics`. An armed capture waits at most 120 wall seconds for this state. Once first eligible, it runs for at most 30 wall seconds or 120 completed worker attempts, whichever occurs first. Interruptions and timeouts produce partial receipts. `complete` means the attempt bound was reached; inspect `accepted`, `stale` and the first accepted batch before claiming a successful transport observation.

## Capture and invalidation

`Update` audits context and captures at most once per observed `FixedUpdate` boundary. `Update` and `LateUpdate` poll the worker without blocking. Each capture contains up to 512 distinct nonkinematic `Part.rb` bodies; shared bodies are deduplicated and absent or kinematic bodies are excluded. The scan covers the active vessel's part rigidbodies, not all scene colliders, debris, joints, or attached subobjects. More than 4096 parts or 512 sampled bodies fails explicitly rather than truncating the workload.

Positions are raw `Rigidbody.position` Unity world coordinates and velocities are raw `Rigidbody.velocity`. `Rigidbody.mass` is retained exactly as reported, without a conversion or a kilogram claim. Captured IDs are sequential batch IDs; the first-batch receipt also preserves signed Unity instance IDs. These native IDs are run-local, not persistent craft identities. Inputs use the current `Time.fixedDeltaTime` as prediction duration.

The candidate first accepted batch also reads each body's raw rotation quaternion (`[x,y,z,w]`), angular velocity, local and world centers of mass, principal inertia tensor and its rotation, native constraints bitmask, and `IsSleeping()` result on the main thread. All values for that candidate are copied before worker submission. A stale candidate is discarded with its physical snapshot, so the retained fields cannot be mixed with a later accepted request.

The main-thread epoch guard compares active vessel/native instance identity, body identity, ordered part IDs, parent IDs, assigned rigidbody IDs, kinematic state, scene, and eligibility. It also invalidates on every observed physics boundary, every subscribed `onFloatingOriginShift` event, and any observed change in the exact Krakensbane frame velocity. A transition back to an earlier observed state still advances the generation. This conservative physics-epoch guard is not a measured floating-origin generation or a complete rotating-frame model. Rapid changes that happen and revert between audits are not qualified; this is why results are never committed to stock state.

A result whose stamp no longer matches is discarded and recorded as `stale-discarded`. There is only one outstanding worker request. Short scheduling windows or a low rendering rate may yield many stale results or no accepted results; the implementation does not relax validity to improve those counts. Scene teardown removes the origin subscription, disposes the worker without blocking the main thread, and attempts to export an interrupted receipt. An export error is logged and does not prevent panel cleanup.

## Receipt

`GameData/KspContinuum/PluginData/shadow-<timestamp>-<unique-id>.json` uses `ksp-continuum-flight-shadow/v2`. Writes go through a unique temporary file and rename; final receipts are not overwritten. Export is bounded to 4 MiB. The report includes runtime versions, limits, terminal reason, observed origin/physics epoch counts, and at most 120 sample records, including any abandoned pending sample.

The outer schema is `ksp-continuum-flight-shadow/v2`. `physicalInputSchema=ksp-continuum-rigidbody-input/v1` and `referenceFrameSchema=ksp-continuum-unity-frame-context/v1` retain the snapshot contract. A reader must not interpret old v1 analytic arithmetic residuals as discrepancies against observed stock motion.

Each sample records capture UT, Unity frames, vessel/body/situation, part/body counts, packed and warp context, worker stamp, prediction duration and status. It also records `referenceFrame=unity-world-at-capture`, the raw Krakensbane frame-velocity vector, and the physics-epoch and floating-origin-event counters observed for that request. Those fields expose the epoch context; they do not define a complete transform to a KSP inertial frame. Timing fields are milliseconds:

- `captureMilliseconds`: context scan plus body reads and owned column construction. When a scan also services an earlier pending request, its audit cost appears in both attributions.
- `submitMilliseconds`: the queue submission call.
- `collectMilliseconds`: accumulated `TryTake` calls for that request.
- `collectAuditMilliseconds`: accumulated main-thread context audits while that request was pending.
- `handoffWallMilliseconds`: elapsed time from submission to observed collection or abandonment, including polling/scheduling delay. This is not backend compute time.

Oracle checking, first-batch receipt construction, final serialization and disk writes are not included in `collectMilliseconds`. The fields are not an additive decomposition of frame cost. There is no timing attribution to stock physics.

`analyticAvailable` is true only for accepted results. Its position and velocity residuals are unavailable for stale or abandoned samples even though their numeric fields default to zero. `firstAcceptedBatch` retains the measured Rigidbody fields listed above, synthetic zero forces, and predicted outputs from one accepted request; `firstAcceptedTick` links it to that sample's duration and stamp. `aggregateForceStatus=unavailable-not-captured` and each body's `forceSource=synthetic-zero-not-native-measurement` prevent the zero transport input from being interpreted as measured total force.

The physical arrays must be finite, quaternions must be unit length within the contract's float tolerance, principal inertia components must be nonnegative, and synthetic forces must be exactly zero. A zero principal-inertia component is preserved but must not be naively inverted; this receipt does not assign it a physical meaning. Constraints and sleeping state do not expose solver iterations, sleep thresholds, constraint impulses, joints, contacts, collider geometry, force providers, torque, or module internals. The snapshot supports inspection or reconstruction of instantaneous Rigidbody inputs inside its captured Unity frame. It does not predict stock motion, span a frame transition, or constitute a deterministic replay checkpoint.

## Comparison with the next stock observation

A separate comparison owner retains at most one accepted immutable prediction. It does not relax worker stamp invalidation. At the first ordinary `Update`/`LateUpdate` observation after exactly one further host `FixedUpdate` boundary, it requires the same ordered topology, scene, eligible state and fixed-step duration. Multiple boundaries between observations are skipped rather than extrapolated. Routine floating-origin events and Krakensbane frame velocity changes are retained at both endpoints rather than treated as identity changes; raw-coordinate residuals therefore include KSP's frame adjustment as well as omitted forces.

The observed `Time.fixedTime` difference must match the predicted duration within `max(1e-6 s, 2 * float epsilon * max(abs(captureTime), abs(observationTime)))`. When that tolerance reaches one quarter of the prediction duration, the comparison is skipped because the float clock cannot resolve the interval sufficiently. This is an observational guard, not a claim about all installed callback/solver ordering.

`observedComparisonAvailable` gates all residual fields. `observedPositionMaxMeters` and `observedPositionRmsMeters` summarize per-body Euclidean position discrepancies; `observedVelocityMaxMetersPerSecond` and `observedVelocityRmsMetersPerSecond` summarize velocity discrepancies. RMS is over bodies, not individual coordinate components. The receipt includes compared body count, observation frame/boundary, capture and comparison fixed times, and observed duration. `comparisonStatus` explicitly records compared, pending, missed-boundary, context/time/precision rejection or teardown. Unavailable zero-valued metrics must never be interpreted as agreement. The report separately counts `compared` and `comparisonSkipped` accepted predictions.

The final accepted prediction gets a comparison attempt before sample-bound completion. Wall timeout or teardown can still leave it unavailable, with an explicit skipped status. Comparison reads and aggregation add observer cost that existing transport timing fields do not separately attribute. A portable-helper fixture proves comparison arithmetic and refusal behavior; live KSP residuals require separate installed qualification.

## Verification boundary

Portable shadow tests drive the real worker across blocked topology, frame, eligibility and return-to-prior-state transitions, verify acceptance without a transition, validate physical vectors, quaternions, inertias, force provenance and accepted-sample linkage, and parse nested receipt arrays. The native plugin compiles against owned KSP 1.12.5 assemblies. Installed observations of actual Rigidbody values, staging, origin changes, packing, scene transitions, interruption/export and actual threaded execution require separate game qualification; a source build and synthetic tests do not establish them.

### Installed orbital observation

An installed KSP 1.12.5 qualification run used package `0.1.3-shadow.9EB8D17FD681` (archive SHA-256 `9eb8d17fd681bcce836aed24bf727bce7ed549ab81eeea7b8e1bbf9c2357b2a0`) on the preserved Minmus-orbit workload. The capture completed 120 accepted worker requests with no stale or abandoned work in 4.922 wall seconds. It produced 119 next-boundary comparisons across 10 rigidbodies per comparison; one request explicitly skipped a missed boundary.

Across 1,190 body comparisons, the largest raw-coordinate position discrepancy was `0.000117479 m` and the body-weighted RMS was `0.0000790755 m`. The largest velocity discrepancy was `0.000240641 m/s` and the body-weighted RMS was `0.0000829615 m/s`. Median capture time was `0.08535 ms`, median submission was `0.00820 ms`, and median observed handoff was `0.89175 ms`. These measurements qualify the installed capture, worker, next-observation comparison and export path for this coast workload. They do not isolate gravity, stock force integration, constraints or Krakensbane adjustment, and they do not qualify active publication or a replacement solver.

Two preceding installed attempts retained zero comparisons because routine Krakensbane velocity changes and floating-origin events were initially treated as discontinuities. In ordinary orbit both vary continuously. The final contract keeps those signals at both endpoints and includes their effect in raw-coordinate discrepancy, while topology, scene, eligibility, step duration and exactly-one-boundary requirements remain gates.


## Paired central-field counterfactual

The zero-force worker and its receipt fields remain unchanged. A second, synchronous main-thread calculation captures `FlightGlobals.getGeeForceAtPosition(position, mainBody)` independently for every sampled body, then freezes that acceleration for one step: `v1 = v0 + a0 dt`, `x1 = x0 + v0 dt + a0 dt² / 2`. This is a bounded counterfactual to choose the next model; it is not a new worker backend or a performance improvement. Capture/prediction and comparison overhead have separate gravity timing fields.

The native central-field routine uses `mainBody.gMagnitudeAtCenter`; the receipt calls that coefficient `gravityMu` and records the separate orbital `gravParameter` as `gravityOrbitalMu`. The captured center, mean acceleration, model/source identity and prediction duration accompany the aggregates. Prediction uses each body's acceleration, not the mean. Invalid coefficients, singular positions or nonfinite acceleration make this strategy unavailable while preserving the zero baseline.

This does **not** reproduce stock integration. The stock integrator applies a shared vessel `precalc.integrationAccel`, which can include global/vessel gravity multipliers, rotating-frame terms and orbit-drift corrections. Thrust, aerodynamic and contact/constraint effects are also omitted. The fixture's closed-form position update may differ from stock integration convention, so velocity is the primary paired diagnostic and raw position is secondary.

Both predictions use the same observed arrays and the same comparison eligibility. `gravityComparisonAvailable` gates the central-field max/RMS metrics. `gravityVelocityRmsDeltaFromZero` is central minus zero RMS: positive means the central counterfactual fits worse. `gravityVelocityRmsRatioToZero` is null when the zero denominator is zero or the ratio is unrepresentable. A worse fit is retained as evidence, not treated as a failed capture.

### Observed-frame-adjusted velocity diagnostic

There is a second matched comparison in which **both** zero and central-field predicted velocities subtract the measured end-minus-start Krakensbane frame velocity. Native `Krakensbane.AddExcess` increments its frame velocity and passes the opposite offset into `Vessel.ChangeWorldVelocity`, which adds that offset to part rigidbody velocities. This establishes the translational sign; it does not establish a complete inertial or rotating-frame transform.

The adjustment uses future observed frame state and is explicitly a post-observation diagnostic, not a forecast. The receipt retains the endpoint delta, adjusted zero and adjusted central max/RMS velocity discrepancies, and central-minus-zero adjusted RMS. Both availability flags must be true for the adjusted pair. No corresponding origin-shift position correction is attempted, and adjusted central results must not be compared against the unadjusted zero baseline as an improvement claim.

A synthetic accelerating-frame control proves the distinction: with `a = -1 m/s²`, `dt = 0.2 s`, observed raw velocity unchanged and frame delta `-0.2 m/s`, raw zero has RMS 0 and raw central has RMS 0.2. After the identical frame correction, zero has RMS 0.2 and central has RMS 0. The exported fixture is labeled `portable-helper-fixture`; it proves arithmetic and matched comparison behavior, not observed KSP physics.

### Installed central-field observation

An installed KSP 1.12.5 run used CKAN package `0.1.4-gravity.CA501F739415` from source commit `878ecc38d20e6694a58ae9f03a02e877e5045cd8`. The bounded Minmus-orbit coast completed 120 submissions in 5.028 seconds. It produced 118 matched comparisons over 1,180 body comparisons; two samples explicitly skipped a missed observation boundary.

In raw Unity coordinates, central-field velocity RMS was `0.00465526 m/s` versus `0.0000799288 m/s` for zero force, a ratio of `58.24`. After applying the same observed endpoint Krakensbane velocity delta to both predictions, central-field RMS was `0.000841748 m/s` versus `0.00381787 m/s` for zero force, a reduction of `0.00297613 m/s` or about 78%. Every matched sample favored central gravity in the adjusted pair. Median synchronous gravity prediction cost was `0.0192 ms`; median paired comparison cost was `0.0536 ms`.

## Independent frame forecast

The next candidate persists Krakensbane's most recent correction for one more physics boundary. Native KSP 1.12.5 inspection shows that `AddExcess(x)` records `lastCorrection = -x` while increasing frame velocity by `x`; `Zero()` records the old frame velocity as the correction while decreasing frame velocity by that amount. A quiet update clears the correction. The portable forecast is therefore:

```text
predicted next frame-velocity delta = -captured lastCorrection
predicted raw body velocity = physical-model velocity - predicted frame delta
```

The plugin captures `Krakensbane.GetLastCorrection()` beside the existing frame velocity snapshot and freezes the resulting prediction before the endpoint exists. The same prediction is applied to both the zero-force and central-gravity branches. Receipts retain the captured correction, forecast, observed forecast error, and paired velocity residuals. The existing retrospective adjustment remains an oracle diagnostic.

This is `krakensbane-last-correction-persistence/v1`, not an exact reproduction of the next native branch. Callback order, safety or velocity-threshold crossings, packed-state transitions, and other mods calling `AddFrameVelocity` may invalidate persistence. No forecast-specific endpoint filter hides a miss, but the existing eligibility guards still skip packed, paused, or otherwise unavailable observations. This candidate does not correct floating-origin position changes.

The opposing raw and adjusted results confirm that coordinate treatment dominates this one-step orbital workload. They support central gravity as a useful model. The retrospective result remains conditioned on the observed future frame delta; the persistence candidate is independently runnable but still awaits installed qualification. Neither establishes stock-integrator equivalence or active state publication.
