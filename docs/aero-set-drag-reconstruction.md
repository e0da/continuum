# Stock body-drag reconstruction experiment

## Question and frozen hypotheses

Can Continuum reproduce stock `DragCubeList.SetDrag` area drag from capture-v3 inputs without reading stock's computed `AreaDrag` label?

- H1: the six post-occlusion areas, six weighted drag coefficients, surface curves, Cd curves, Mach, attitude, and relative airflow are sufficient for ordinary unweighted curves. It is ruled out if an independently held-out capture-v3 trajectory exceeds the frozen error gate below.
- H2: the earlier cube projection is sufficient. It is already ruled out by the capture-v2 powered-descent receipt: near-identical cube geometry and flow produced materially different stock `AreaDrag` values because attachment occlusion and runtime curves were absent.
- H3: captured stock magnitude scalars constitute an independent body-drag replacement. It is ruled out by construction: multiplying stock `AreaDrag`, pseudo-Reynolds, and cached multipliers only reassembles stock outputs.

The installed stock assembly's IL fixes the candidate formula. For each of six faces it evaluates a Mach-dependent surface curve at `(dot + 1) / 2`, multiplies that by post-occlusion area, transforms weighted drag below one through `pow(DragCurveCd(drag), DragCurveCdPower(mach))`, and sums area times transformed drag. Lift calculations in the same stock method are outside this experiment.

## Acceptance gates

Portable fixtures must be deterministic and match hand-computed results within `1e-12` square metres. Metamorphic checks must cover scale, opposing faces, zero area, and direction normalization. Unsupported weighted animation-curve keys or evaluation outside the captured key range must abstain explicitly.

Live qualification is split by complete capture receipt, never adjacent samples. With at least two valid capture-v3 receipts, freeze one complete receipt as development data and keep another complete receipt held out. The held-out stock `AreaDrag` comparison gate is p99 relative error at most `1e-5` and maximum relative error at most `1e-4`, with absolute error at most `1e-5 m^2` when the stock label is near zero. A passing area-drag result does not qualify total force magnitude, trajectories, lift, submerged samples, provider compatibility, or active authority.

## Current evidence boundary

The local qualified powered-descent receipt is capture schema v2. It has 64 samples and 2,560 body-drag labels, but it predates `setDragInputs`; therefore it cannot evaluate H1. Capture-v3 source and parsing contracts exist, but no completed capture-v3 receipt is currently available. Portable tests can validate the extracted formula and abstention boundary; the held-out live gate remains pending a future read-only capture run.
