# Mission chronicles

The chronicle generator turns one explicit mission artifact directory and an optional input-recording directory into a self-contained local flight report. It copies confirmed screenshots, summarizes telemetry and input evidence, and records hashes without copying saves, raw timelines, or private source paths. Generated reports belong under ignored `artifacts/`; the public repository contains the generator, template, tests, and this contract.

Mission and attempt identities follow the [naming conventions](naming.md). The chronicle is the generated mission-report member of the maintained [wiki template set](wiki-templates.md) and contributes a report to the local [Continuum Space Program](space-program.md) archive.

## Metadata

Write a small JSON sidecar for the editorial fields. The full attempt ID must begin with its mission ID. `site_id` and `parent_checkpoint` are optional; all other fields shown here are required.

```json
{
  "schema": "ksp-continuum-chronicle-metadata/v1",
  "mission_id": "CSP-0001",
  "name": "Minmus Pathfinder",
  "attempt_id": "CSP-0001-A003",
  "vehicle_design_id": "CV-0001-R01",
  "objective": "Attempt an intact Minmus landing while preserving flight evidence.",
  "configuration": "Stock Kerbal X under MechJeb control; rendered at 1280 x 720.",
  "anomalies": [
    "The landed vessel was visually observed resting sideways."
  ],
  "next_experiment": "Measure landing attitude, target distance, lighting, and warp behavior."
}
```

The generator displays these fields as editorial context. When `mission.txt` contains native `missionId`, `attemptId`, `vehicleDesignId`, or `siteId` fields, each must match the corresponding metadata field. Legacy receipts without those fields remain supported. The generator never invents anomaly claims from telemetry. Measurements, receipt status, and media confirmation appear separately.

## Generate a report

Create the archive parent first, then choose a new attempt directory. The destination must not exist.

```sh
mkdir -p artifacts/space-program
python3 scripts/chronicle.py \
  --mission /path/to/mission-session \
  --inputs /path/to/input-session \
  --metadata /path/to/CSP-0001-A003.json \
  --output artifacts/space-program/CSP-0001-A003
```

When `--inputs` is supplied, its directory basename must match the mission receipt's `inputDirectory` basename. This preserves the native association while allowing a session directory to be relocated. Omit `--inputs` when input evidence is unavailable; the page and manifest explicitly record that omission instead of displaying zeroes as a complete recording. The output contains:

- `index.html`, a relocatable report with objective, configuration, timeline, media, measurements, outcome, anomalies, and next experiment;
- `manifest.json`, schema `ksp-continuum-chronicle-manifest/v1`, with stable IDs, title, outcome, report entrypoint, template and generator hashes, input-evidence status, source-session basenames, logical source paths, byte sizes, SHA-256 hashes, and media status;
- `media/`, containing source PNGs that have a `png-written` or `png-below-required-resolution` receipt, pass bounded PNG checks, and remain inside the mission directory. Below-resolution captures remain viewable but are marked as failing the 1920 × 1080 survey-evidence requirement.

The `mission-v1` HTML template lives at `templates/chronicle/mission-v1.html`. Its placeholder set is a strict generator contract. Improve or add a versioned template in Git, then generate a new report directory so an earlier report remains immutable.

## Browse the archive

Build a new local archive index after one or more reports exist:

```sh
python3 scripts/chronicle_index.py \
  --archive artifacts/space-program \
  --output index.html
```

The index shows the most recent rendering of each attempt and keeps links to earlier renderings. It refuses to overwrite by default. Add `--refresh` to atomically update the derived index without changing any report directory, or choose a new index filename when preserving an index snapshot. Flight evidence and report outputs remain immutable.

## Evidence and limits

The generator accepts UTF-8 source text up to 16 MiB per file, metadata up to 32 KiB, 100,000 telemetry rows, 256 mission events, 512 input files totaling 256 MiB, 2,000 samples per input segment, and 64 screenshots up to 16 MiB each. It rejects nonfinite or backward telemetry time, malformed schemas, unsafe screenshot names, symbolic-link sources, output inside a source directory, and an existing destination.

Phase spans use left-sample attribution: each telemetry interval belongs to the phase on its first row. The final phase has no displayed span unless a later row closes it. This makes boundaries approximate at the telemetry cadence. Screenshot dimensions come from each confirmed PNG header; the historical Pathfinder captures are 1280 × 720 observations, not a promise for later missions.

Legacy three-column screenshot receipts remain supported. New five-column completion rows include width and height; nonzero reported dimensions must match the copied PNG header. If present, `survey.csv` and `terrain.csv` are included in the source hash manifest so a later site report can cite their exact bytes. The mission chronicle does not interpret those files into a site qualification by itself.

The report is a human-readable record of observed telemetry and receipts. It does not render video, restore a save, prove deterministic world replay, certify a landing site, or infer that a screenshot request succeeded. Source-session directory basenames are retained for provenance, while private absolute paths and save contents are excluded from generated pages and manifests. A receipt reason containing a Unix, Windows-drive, or UNC absolute path is replaced with a safe pointer to the hashed local receipt.
