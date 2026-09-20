# Continuum Space Program names

Readable names describe the story. Stable identifiers connect the evidence even when a title changes. Never infer physical state, success, or compatibility from a name.

| Record | Identifier | Initial catalog entry |
| --- | --- | --- |
| Mission | `CSP-0001` | Minmus Pathfinder |
| Mission | `CSP-0002` | Minmus Survey 1 |
| Attempt | `CSP-0001-A003` | Pathfinder's first completed touchdown |
| Vehicle design revision | `CV-0001-R01` | Kerbal X / stock |
| Site | `SITE-MIN-001` | Greater Flats Candidate 1 |

Mission numbers are program-wide, allocated in the catalog before a flight. Attempt numbers increase within a mission and include failed preflight attempts. Never reuse an allocated attempt because it failed. The original Pathfinder attempts are A001 (compatibility preflight failure), A002 (control-ownership preflight failure), and A003 (intact, settled, approximately sideways touchdown).

An attempt also retains its original runtime session identifier and the hashes of its evidence. These catalog aliases do not rename existing files or claim that historical runtime metadata contained the new names. A timestamp and random suffix distinguish physical output directories; they do not replace a mission or attempt identity.

## Vehicles, sites, and revisions

A design revision identifies the craft definition, not an individual flying vessel. Record the craft-file hash and installed environment for each attempt; the runtime vessel identifier identifies the flown instance. Any craft change creates a new revision, including staging, attachments, fuel load, or control-reference changes. Changing the autopilot configuration changes the experiment configuration, not the craft revision.

`CV-0001-R01` means the stock Kerbal X used by the current harness. Record runtime additions such as MechJeb separately. Do not label this vehicle a probe: it is crewed. A purpose-built uncrewed survey carrier and scout will receive their own design identifiers once defined and qualified.

`SITE-MIN-001` identifies a candidate near latitude -4.794139°, longitude -11.575088° on Minmus. Its record must include the body/terrain environment, coordinates, survey radius and measurements. The name does not certify flatness or daylight. A site qualification result belongs to an attempt and configuration.

## Saves, media, and history

New save folders should include the attempt ID plus a unique run suffix. Checkpoints use meaningful phase names and unique suffixes inside that folder, for example `minmus-orbit-<unique-id>.sfs`. An evidence manifest connects them to the attempt and simulation time; filenames are not a substitute for that manifest.

Media use milestone names and unique suffixes. Preserve source bytes and record hashes when importing them into a chronicle. A requested screenshot is not a confirmed screenshot. Record capture resolution rather than assuming it from launch settings.

A retry starts a new attempt. A resumed or forked attempt additionally records the parent attempt, exact checkpoint and hash, and changed inputs/configuration. A save file alone does not establish deterministic controller restoration. The current harness does not yet implement general mission forks or concurrent probe flights.

## Templates

Reports record their template version. Improve templates in Git and regenerate into a new output directory; preserve the original report and evidence. Corrections should identify what changed and why. Keep observations, interpretation, and planned work distinguishable.

Use the [mission chronicle](chronicle.md) for actual flight reports. Vehicle, site, and experiment records should use the maintained [wiki templates](wiki-templates.md).
