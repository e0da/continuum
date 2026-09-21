# Continuum Space Program

The space program is a persistent world as well as a development workload. Keep its vehicles, saves, discoveries, photographs and failures alongside the software that makes the next mission possible.

## The working loop

Design a mission with explicit objectives and a vehicle suited to them. Freeze its configuration and acceptance criteria before flight. Fly visibly when practical, preserve each attempt, write its chronicle, and choose the next engineering change from the observations. Reuse a checkpoint only when its restoration contract is understood; record a parent link rather than pretending it is a fresh launch.

Develop the tools alongside those missions. A physics benchmark answers a narrow engineering question; an actual flight exposes control handoffs, time warp, contact, staging, recording and presentation together. Neither substitutes for the other.

## Current catalog

| Mission | Purpose | Evidence boundary |
| --- | --- | --- |
| CSP-0001 — Minmus Pathfinder | Establish a recorded launch-to-landing workload | Three attempts; A003 achieved intact, settled touchdown, approximately sideways. No upright-landing or deterministic replay claim. |
| CSP-0002 — Minmus Survey 1 | Target a sunny Greater Flats candidate and measure the landing path | A001 landed sideways; A002 landed upright with the throttle floor disabled, but missed the 100 m distance limit at 116 m. Qualification remains open. |

The current vehicle is stock Kerbal X, catalog design `CV-0001-R01`. The next craft-design outcome is an uncrewed scout and carrier: deliver several identical probes, deploy and follow them sequentially, and preserve useful measurements from both successful and failed landings. Concurrent autonomous descents require additional control ownership and simulation qualification.

## What the next flight should teach us

- Does targeted landing use the expected warp-aware coast and reduce waiting before braking?
- Is the selected footprint sufficiently flat, and is actual touchdown in daylight and within tolerance?
- Does the craft remain upright and settle rotationally as well as translationally?
- Can the report explain the result through synchronized controller, attitude, warp and media evidence?

Keep the same craft initially to reduce the number of changes. It is still a crewed test vehicle, not a stand-in claim that our probe carrier exists.

## Checkpoint experiment

The next comparison branches from A002's saved Minmus orbit, before landing control was acquired. Two trials keep the throttle floor disabled and use the same checkpoint and external MechJeb settings. This separates the landing workload from launch and transfer, while measuring how much native reconstruction and controller scheduling vary between runs.

Before each trial, verify the checkpoint digest and the reconstructed vessel's identity, parts, resources and orbit. Record the controller settings and acquisition time. A source save is an input to the experiment, not a complete snapshot of running physics or autopilot memory. The resulting attempts keep separate saves, receipts and chronicles, with links back to A002.

Judge the outcome against the existing 100 m, upright, daylight and settling requirements. Compare where target error develops during braking, horizontal correction and final descent before adjusting guidance. Similar outcomes would support repeatability for this workload; they would not prove exact deterministic replay.

## Simulation development

Use recorded mission states and inputs to test candidate models alongside stock behavior before giving them authority. Compare conservation, contact, trajectory error and measured computational cost at the actual integration boundary. Add fidelity where a stated experiment needs it; more computation or agreement with stock alone does not establish physical accuracy.

Pluggable compute workers, adaptive warp, deterministic rewind, multiplayer coordination and retrospective video rendering remain research goals. The current harness does not make arbitrary KSP systems deterministic or replace Unity physics. Profiling should determine which workload to move first.

## The keepsake

The [chronicle](chronicle.md) preserves actual mission images and measurements in a relocatable local report. [Naming conventions](naming.md) connect missions, attempts, vehicles and sites. [Wiki templates](wiki-templates.md) keep records consistent as the program grows. Public Git contains tooling and documentation; local artifacts hold game saves and per-run evidence.

Preserve the raw record when correcting an interpretation. An entertaining failure can be an important result and a good chapter.
