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

The shadow section reports submitted, accepted, stale and abandoned samples; capture, submit, collect, audit and handoff timing distributions; and the body count in the first accepted batch. These timings overlap in their documented native scopes and are not summed. Rigidbody mass remains in the native recorded unit without a kilogram label.

The shadow worker is a read-only zero-force transport probe. Its exact analytic result checks transport and worker plumbing. It is not a stock trajectory comparison, a gravity or contact model, deterministic replay evidence, or a gameplay speedup measurement. Stock KSP remains authoritative.

## Catalog integration

Keep the raw report directory in ignored local artifacts. After review, a maintained experiment entry can link a derived chart or this local report and record its SHA-256, capture mode, package version, checkpoint digest, marker availability, window statuses and explicit observational boundary. Link only mission attempts that actually produced the receipts. A designed profiling or shadow experiment stays `designed` until a terminal native receipt exists; a completed transport report alone does not qualify replacement physics.
