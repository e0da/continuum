# KSP Continuum

An experimental KSP 1 toolkit for measuring simulation cost, recording flight inputs, and testing alternatives. The goal is faster, believable structural physics, including flex and breakage. Rigid grouping is one experiment, not a commitment to making every ship rigid.

**Status: the isolated benchmark passes in headless KSP 1.12.5; 123 analytic/report/timeline assertions pass.** This is a research harness, not a gameplay fix or a replacement physics engine. Synthetic timings are recorded in the validation receipt; stock-vessel speedup and mod compatibility remain unverified.

## What exists

- A main-menu benchmark compares 8, 32, and 128 aligned boxes as a chain of Unity fixed joints and as one compound rigid body. Both retain the same collider geometry. It uses separate physics scenes and does not change global simulation settings.
- The benchmark checks collider ray hits, spacing error, a compound body's contact with a floor, and momentum at separation. It reports raw repeated timings. These are synthetic bodies, not stock parts or KSP joints.
- A flight panel inventories the active vessel without modifying it. A small structural allowlist identifies inspection candidates; it does not approve them for merging.
- An optional 300-frame timing capture records available Unity markers. Unsupported markers are reported as unavailable, never as evidence of zero cost.
- A flight input recorder exports step-keyed recordings plus event/trajectory observations; the format supports manual linear and cubic-Bezier edits. An opt-in analog player enforces a conservative control-ownership boundary; see [input timeline](docs/input-timeline.md).
- An optional MechJeb mission addon creates a separate sandbox and attempts a recorded stock Kerbal X flight to Minmus. See [mission operation and qualification](docs/minmus-mission.md). One rendered attempt achieved an intact, settled Minmus touchdown; it finished approximately sideways, and repeated rotation during descent remains unexplained.
- A pure C# model verifies aligned-box aggregation and impulse-free separation without proprietary assemblies.

Experiments run only through explicit panel actions or command-line flags. Analog replay changes flight controls. The optional mission runner creates its own save and uses MechJeb on its mission vessel; normal launches do not start a mission. Local reports/recordings go to `GameData/KspContinuum/PluginData/` and can include vessel identifiers, mod identities and mission save paths. They are runtime artifacts and are not committed to this public repo.

## Build

Use a .NET 8 or newer SDK. Run the analytic tests without installing KSP:

```sh
dotnet run --project tests/KspContinuum.Tests -c Release
```

To compile the addon, point `KSP_MANAGED` at the `Managed` directory in your own KSP 1.12.5 installation. On macOS this is inside `KSP.app/Contents/Resources/Data`; on Windows/Linux it is typically under `KSP_x64_Data`.

```sh
export KSP_MANAGED="/path/to/your/KSP/Managed"
dotnet build src/KspContinuum.Plugin -c Release
python3 scripts/package.py
```

The build reads game references but never copies them or installs anything. The local archive in `artifacts/` contains this project's plugin, documentation and an example input track; mission packages add the optional project-owned mission DLL. There is no published CKAN release yet. The local experiment was installed through CKAN in an independent test copy; public release metadata and gameplay qualification remain incomplete. Do not deploy into an instance another process or agent is using.

For automated engine qualification in an owned test copy, launch its KSP executable with `-batchmode -nographics --continuum-bench`. At the main menu, the addon runs the isolated benchmark, writes its report, and exits with code 0 when all report checks pass or 1 on failure. An unsupported KSP version exits with code 2. Normal launches do not auto-run or exit. Headless execution does not verify the visible panels or live-vessel inspector.

## Research direction

The next consequential experiment is a shadow solver: capture a vessel's inputs, run a separate solver without controlling the vessel, and compare its outputs with stock. First measure whether rigid-body solving, KSP's force calculations, part callbacks, or another subsystem dominates. A native CPU solver, an external process, and GPU compute remain candidates rather than assumed winners.

See [integration map](docs/integration-map.md), [experiment protocol](docs/experiment.md), [replacement architecture](docs/replacement.md), and [validation receipt](docs/validation.md), and [community integration boundaries](docs/compatibility.md). Work uses Git and GitHub on reviewable branches.
