# Space-program wiki templates

Template set version: **1**. Copy a template into a new record and replace the guidance with evidence or an explicit `unknown` / `not measured`. Do not leave instructions disguised as results. Keep the version on the record so later improvements can be applied deliberately.

The generated mission-report template is maintained separately with the [chronicle](chronicle.md). These companion templates cover the objects a flight report links to; they are record formats, not claims that a database or wiki editor already exists.

## Vehicle design record

```markdown
# <readable vehicle name> — <CV-NNNN-RNN>
Template: vehicle/1
Status: proposed | built | flown | retired

## Purpose and design
Mission role, crew policy, payload, control reference, staging and landing arrangement.

## Reproducible definition
Craft-file hash, origin, dependencies, environment, and changes from the previous revision.
List runtime modifications separately from the saved craft.

## Limits and qualification
Mass, propulsion, power and control assumptions; measured capabilities and unknowns.
Link the attempts that support each capability claim.

## Evidence and next revision
Images, flight outcomes, failure observations and the next testable change.
```

## Site record

```markdown
# <readable site name> — <SITE-BODY-NNN>
Template: site/1
Status: candidate | surveyed | qualified-for-stated-use

## Location and environment
Body, latitude/longitude convention, terrain/mod versions, footprint and landing tolerance.

## Measurements
Terrain samples and spacing; elevation variation and slope method; actual contact evidence.
Sun elevation and UT intervals; eclipse/terrain-shadow limitations; panel state and accepted power.

## Qualification
Explicit use case and thresholds, supporting attempts, uncertainty and counterexamples.
Distinguish a level craft from a level surface, and a full battery from absent sunlight.

## Media and next survey
Map or image links, successful and failed probes, missing coverage, next observation.
```

## Experiment record

```markdown
# <testable question> — <experiment identifier>
Template: experiment/1
Status: designed | running | concluded

## Hypothesis and alternatives
Predicted observable difference and credible competing explanations.

## Protocol
Mission/attempt and parent checkpoint; independent variable; fixed conditions;
measurements, tolerances, failure/stop conditions, comparison strategy.

## Results
Measured data and artifact hashes, including unsuccessful runs.
Label rendered/headless mode, resolution, simulation steps and timing boundaries.

## Interpretation
What the evidence supports, what it rules out, and what remains unknown.
Separate model agreement from independent physical validation.

## Next decision
Adopt, revise, reject or collect specific missing evidence; link the follow-up.
```
