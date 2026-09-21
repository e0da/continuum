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
