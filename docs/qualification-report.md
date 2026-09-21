# Flight qualification report

`qualification_report.py` turns one terminal native qualification directory into a portable JSON summary and a standalone HTML report. It reads evidence only. It does not start KSP, control a vessel, modify the source receipts or add the report to the space-program catalog.

An optional `shutdown.txt` (`ksp-continuum-shutdown/v1`) records callback outcomes separately from collection status. The summary validates its capture status, unique bounded handler IDs and matching error counts, includes its source hash, and displays shutdown errors even when all three capture windows completed. Missing shutdown receipts remain explicitly unqualified, including historical A007 evidence. Callback completion does not independently prove restored native settings or game state.

Run it after the native qualification has written `status.txt`:

```sh
python3 scripts/qualification_report.py \
  /path/to/qualification-SESSION \
  --output artifacts/qualification-reports/SESSION
```

The destination must not exist. The command creates:

- `summary.json`, schema `ksp-continuum-qualification-summary/v1`;
- `index.html`, a self-contained readable rendering of the same bounded summary.

The summary identifies the source by its safe directory basename and hashes each fixed relative input file. It does not store the source directory, native error text, marker availability details, graphics labels or other source prose that could contain a private path. Raw native receipts remain the evidence owner.

## Required receipts

The source has `scope.txt`, `status.txt`, and a prefix of the three ordered windows `coast`, `powered`, and `contact`. A completed window has all three files:

```text
PHASE-start.txt
PHASE-markers.json
PHASE-shadow.json
```

`status.txt` records `complete`, `timeout`, `error`, or `interrupted`, followed by `completedWindows=N`. A `complete` result requires all three windows. Another terminal status may report a completed prefix and lists later phases as missing. A partial triplet, non-prefix phase, mismatched count, symbolic input, duplicate JSON field, unsupported schema, nonfinite value, or size-bound violation is rejected.

Each file is limited to 16 MiB and all read files together to 64 MiB. Profiler frames are limited to 600, shadow samples to 512, and the first accepted body batch to 512. The fixed native captures currently request 300 profiler frames and 120 shadow attempts; the larger parser limits preserve bounded compatibility with interrupted or revised captures.

## Reading the result

The phase name describes the vessel context at the instant that window began. It does not claim the same state persisted. For each phase, the report separately shows actual profiler-frame counts for throttle command, packed state, body and situation. A null command or packed state remains `unknown`.

Profiler callback intervals report the time between coroutine boundaries. Marker rows preserve `observed`, `available-no-samples`, or `unavailable`, along with available and unavailable frame counts. Observed marker duration distributions include only available frames with positive block counts. Marker scopes can overlap, so the report does not add them, turn them into a physics percentage, or treat an unavailable marker as zero cost.

An optional `playerLoop` member (`ksp-continuum-playerloop/v1`) carries opt-in fixed-step bracket samples separately from Recorder frames. Its two supported scopes are the native physics and script FixedUpdate subtrees. The parser bounds each scope to 4,096 samples, validates statuses, clock frequency, raw ticks and step context, and recomputes duration distributions from the raw ticks. A capture is usable only when its boundary-integrity and owned-hook cleanup receipts agree; invalid or unavailable captures retain sample counts but withhold duration summaries. Absent legacy fields remain “No player-loop timing recorded.”

Bracket durations are elapsed wall time, including waits and callback overhead, not exclusive CPU or a PhysX-only measurement. The window runs from hook installation through cleanup and includes initial warmup. Multiple fixed steps can belong to one rendered frame; these samples must not be joined by array index to the previous-frame Recorder observations. Dropped samples identify a retained prefix, not a representative sample of the unobserved tail. The raw timer-read floor is not subtracted, and durations are not added to markers or converted into a physics percentage. Native installation, execution, restoration and perturbation remain separate qualification gates.

The shadow section reports submitted, accepted, stale and abandoned samples; capture, submit, collect, audit and handoff timing distributions; and the body count in the first accepted batch. These timings overlap in their documented native scopes and are not summed. Rigidbody mass remains in the native recorded unit without a kilogram label.

Versioned physical inputs appear as a separate coverage section. Receipts with `ksp-continuum-rigidbody-input/v1` and `ksp-continuum-unity-frame-context/v1` must include finite pose/velocity/center/inertia vectors, unit quaternions, positive mass, nonnegative principal inertia, unique IDs, raw constraint and sleeping values, and synthetic-zero force provenance. The first snapshot must match the first accepted sample's tick and body count. Each sample must name the supported Unity frame and retain frame velocity and nonnegative epoch/origin counters. Missing or unsupported portions of an advertised contract are rejected.

The summary reports body/frame counts, sleeping and constrained counts, and bodies with a zero principal inertia component. Zero components are retained without interpreting them as invertible or infinite inertia. Legacy receipts show “No versioned physical input recorded”; a current receipt without an accepted batch remains unqualified for body coverage. Validation establishes receipt shape and linkage, not physical accuracy, native force availability, a complete inertial transform or replay completeness. Native force and torque remain unavailable. Both standalone and connected-site pages use this same renderer.

The shadow worker is a read-only zero-force transport probe. Its exact analytic result checks transport and worker plumbing. It is not a stock trajectory comparison, a gravity or contact model, deterministic replay evidence, or a gameplay speedup measurement. Stock KSP remains authoritative.

## Catalog integration

Keep the raw report directory in ignored local artifacts. After review, a maintained experiment entry can link a derived chart or this local report and record its SHA-256, capture mode, package version, checkpoint digest, marker availability, window statuses and explicit observational boundary. Link only mission attempts that actually produced the receipts. A designed profiling or shadow experiment stays `designed` until a terminal native receipt exists; a completed transport report alone does not qualify replacement physics.

## Part-force component coverage

The optional `partForces` receipt uses `ksp-continuum-part-force-observation/v1`. The consumer validates provider/stage, lifecycle, fixed bounds, batch counts, context identities, monotonic callback epochs, finite vectors and part references before summarizing retained batches and records. Missing legacy fields are explicitly absent. Raw diagnostic prose is not copied into the public summary.

Only positive-count captures with a valid terminal prefix and verified callback removal are labeled usable component observations. Cleanup errors must propagate to the outer profiler status. Destroyed owners, invalid captures and no samples remain unqualified. The UI never sums the census into total force, converts raw units to Newtons, infers omitted channels, or joins independent Shadow epochs.
