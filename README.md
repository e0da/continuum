# KSP Continuum

An experimental KSP 1 toolkit for measuring simulation cost, recording flight inputs, and testing alternatives. The goal is faster, believable structural physics, including flex and breakage. Rigid grouping is one experiment, not a commitment to making every ship rigid.

**Status: experimental measurement, input, and worker infrastructure.** The isolated physics benchmark has passed in headless KSP 1.12.5, and an installed orbital coast has exercised the live background worker plus next-step stock comparison. This is a research harness, not a gameplay fix or a replacement physics engine. Stock-vessel speedup and mod compatibility remain unverified.

## What exists

- A main-menu benchmark compares 8, 32, and 128 aligned boxes as a chain of Unity fixed joints and as one compound rigid body. Both retain the same collider geometry. It uses separate physics scenes and does not change global simulation settings.
- The benchmark checks collider ray hits, spacing error, a compound body's contact with a floor, and momentum at separation. It reports raw repeated timings. These are synthetic bodies, not stock parts or KSP joints.
- A flight panel inventories the active vessel without modifying it. A small structural allowlist identifies inspection candidates; it does not approve them for merging.
- An optional 300-frame timing capture records available Unity markers, frame context, and timing distributions. Unsupported markers are reported as unavailable, never as evidence of zero cost. An opt-in coast/powered/contact run exports a [qualification report](docs/qualification-report.md). With `--continuum-playerloop`, captures also bracket unchanged native physics and behaviour FixedUpdate subtrees. This timing candidate is source/build verified; installed dispatch and perturbation remain unqualified. See [profiling](docs/profiling.md).
- A flight input recorder exports step-keyed recordings plus event/trajectory observations; the format supports manual linear and cubic-Bezier edits. An opt-in analog player enforces a conservative control-ownership boundary; see [input timeline](docs/input-timeline.md).
- An optional MechJeb mission addon creates a separate sandbox and attempts a recorded stock Kerbal X flight to Minmus. See [mission operation and qualification](docs/minmus-mission.md). Checkpoint trials now provide repeatable setup and preserved evidence; they do not establish deterministic replay.
- An opt-in survey mode targets a sampled flat Minmus site in daylight. Two checkpoint trials landed upright with all 17 parts, approximately 120 m from the target, missing the unchanged 100 m criterion. This is a systems test fixture; see the [flight evidence](docs/validation.md).
- A pure C# model verifies aligned-box aggregation and impulse-free separation without proprietary assemblies.
- A [bounded background worker](docs/simulation-worker.md) accepts immutable batches with tick, topology, and frame identities. A reference constant-force backend and runnable serial/parallel comparison exercise the boundary without controlling KSP bodies.
- An opt-in [flight shadow probe](docs/shadow-worker.md) captures real rigid-body arrays into the worker and rejects stale results. Its first accepted batch also retains pose, angular velocity, centers of mass, principal inertia and frame context for offline inspection. Accepted zero-force predictions can now be compared with the next matching stock observation; `scripts/shadow_report.py` summarizes those model discrepancies. Aggregate native forces and a validated stock-trajectory model remain unavailable.
- An opt-in [lifecycle trace](docs/lifecycle-trace.md) records named callback observations, body/force samples and context changes under one bounded session sequence. Its portable tests and native build qualify the source path; installed ordering and the flight-panel controls remain unverified.
- The opt-in qualification harness requests mission cancellation before process exit, preserving an `interrupted` outcome and separate cleanup receipts. Portable tests and native compilation cover this path; installed shutdown behavior still needs qualification.
- An [input comparison tool](docs/input-comparison.md) measures sampled differences between recorded control tracks without claiming equivalent physics outcomes.

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

## Run the experiments

The experiments are independent of the running game:

| Tool | What it measures |
| --- | --- |
| [Native structural fixture](docs/structural-benchmark.md) | Pinned Jolt spring motion against an analytic reference, substep cost/error, and a floor-contact check |
| [Contact checkpoints](docs/contact-checkpoints.md) | Uninterrupted contact/joint simulation versus full state restore and cold body reconstruction |
| [Orbital/event fixture](docs/orbital-fixture.md) | An external local Gimbal coast implementation against independent orbital states, plus bounded encounter-event adversaries |
| [Data-layout comparison](docs/layout-benchmark.md) | Object, SoA, and AoSoA captures under the same free-body and neighbor-reading kernels |
| [Gravitational trajectories](docs/field-trajectory.md) | Analytic and step-refined Plummer trajectories, conservation, reversibility and a potential-consistent split toy |
| [3D field gravity](docs/field-gravity.md) | Isolated softened forces via padded FFT/CIC against direct forces, with retained accuracy failures |
| [Telemetry playback](docs/telemetry-playback.md) | Scrubbing and phase navigation over recorded mission observations, without rerunning KSP |

Native structural dependencies are fetched by pinned digest into ignored build artifacts. The orbital donor is supplied explicitly from a separate checkout; its implementation is not distributed here. Neither tool installs a backend into KSP.

## Research direction

The longer-term experiment is a shadow solver: capture a vessel's inputs, run a separate solver without controlling the vessel, and compare its outputs with stock. First measure whether rigid-body solving, KSP's force calculations, part callbacks, or another subsystem dominates. A native CPU solver, an external process, and GPU compute remain candidates rather than assumed winners.

Development advances along independent worker/backend, profiling, replay/input, and space-program/chronicle tracks. Missions provide workloads and acceptance evidence; landing precision does not gate unrelated simulation infrastructure. The current free-body worker proves a compute boundary, not complete force capture or a shadow vessel solver.

The optional `--continuum-part-forces` probe captures a bounded read-only Part census before integration, with provider-local epoch and explicit missing force channels. It is a component-observation candidate; installed ordering and mod compatibility remain unqualified.

The standalone [encounter planner](docs/encounter-scheduler.md) tests conservative stopping boundaries and separate horizons for quiet objects. It does not yet own game time or resolve collisions.

See [interaction regimes and scheduling](docs/interaction-regimes.md), [integration map](docs/integration-map.md), [force-observation design](docs/force-observation.md), [experiment protocol](docs/experiment.md), [replacement architecture](docs/replacement.md), and [validation receipt](docs/validation.md), and [community integration boundaries](docs/compatibility.md). Work uses Git and GitHub on reviewable branches.

## Continuum Space Program

The [space program](docs/space-program.md) develops the toolkit through actual missions and preserves the results as a local multimedia chronicle. Its [naming conventions](docs/naming.md) distinguish missions, attempts, vehicle revisions and survey sites. Maintained [wiki templates](docs/wiki-templates.md) keep vehicle, site and experiment records consistent without rewriting old evidence.

Generate a [mission chronicle and connected website](docs/chronicle.md) from attempt telemetry, confirmed screenshots and a maintained program catalog. The site connects missions, attempts, vehicles, landing sites and experiments while preserving the original reports. Reports and game saves remain local; this public repository contains their tooling and templates.

The [local handoff fixture](docs/local-handoff.md) connects encounter planning, a local sphere solver, and atomic in-memory publication. It remains separate from live KSP ownership.

The [state-ownership contract](docs/ksp-state-ownership.md) maps integration responsibilities, proposes a read-only callback trace, and identifies recovery requirements before a live driver can publish simulation results.
