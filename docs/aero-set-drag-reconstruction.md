# Stock body-drag reconstruction experiment

## Question and frozen hypotheses

Can Continuum reproduce stock `DragCubeList.SetDrag` area drag from capture-v3 inputs without reading stock's computed `AreaDrag` label?

- H1: the six post-occlusion areas, six weighted drag coefficients, surface curves, Cd curves, Mach, attitude, and relative airflow are sufficient for ordinary unweighted curves. It is ruled out if an independently held-out capture-v3 trajectory exceeds the frozen error gate below.
- H2: the earlier cube projection is sufficient. It is already ruled out by the capture-v2 powered-descent receipt: near-identical cube geometry and flow produced materially different stock `AreaDrag` values because attachment occlusion and runtime curves were absent.
- H3: captured stock magnitude scalars constitute an independent body-drag replacement. It is ruled out by construction: multiplying stock `AreaDrag`, pseudo-Reynolds, and cached multipliers only reassembles stock outputs.

The installed stock assembly's IL fixes the candidate formula. For each of six faces it evaluates a Mach-dependent surface curve at `(dot + 1) / 2`, multiplies that by post-occlusion area, transforms weighted drag below one through `pow(DragCurveCd(drag), DragCurveCdPower(mach))`, and sums area times transformed drag. Lift calculations in the same stock method are outside this experiment.

## Acceptance gates

Portable fixtures must be deterministic and match hand-computed results within `1e-12` square metres. Metamorphic checks must cover scale, opposing faces, zero area, direction normalization, and captured `ClampForever` curve behavior. Unsupported weighted animation-curve keys or non-clamping evaluation outside the captured key range must abstain explicitly.

Live qualification is split by complete capture receipt, never adjacent samples. With at least two valid capture-v3 receipts, freeze one complete receipt as development data and keep another complete receipt held out. The held-out stock `AreaDrag` comparison gate is p99 relative error at most `1e-5` and maximum relative error at most `1e-4`, with absolute error at most `1e-5 m^2` when the stock label is near zero. A passing area-drag result does not qualify total force magnitude, trajectories, lift, submerged samples, provider compatibility, or active authority.

Here, complete means the qualification capture reached its declared 64-sample bound. A partial receipt can still produce diagnostic error distributions, but it cannot set `qualifiedHeldOutGate`; the comparison report exposes completeness separately for the development and held-out sides.

## Current evidence boundary

Two independent capture-v3 runs from the immutable `scenarios/Powered Landing` checkpoint each completed 64 samples and 2,560 body-drag labels. The development run exposed two adapter defects: captured stock curves use `ClampForever` outside their key range, and `SetDrag` consumes airflow in the same direction as the captured `Part.dragVector`. With both corrected, development and held-out comparisons contain 2,560 finite rows each and no abstentions. Development p99/max relative error is `6.0804e-6`/`8.8021e-6`; held-out p99/max is `5.9247e-6`/`8.2614e-6`. Both pass the frozen numeric gate and set `qualifiedHeldOutGate=true`.

Both receipts start from the same checkpoint, so this qualifies the independent-run gate for that powered-descent workload, not craft-family or regime generalization. The result supports advancing the area computation into a bounded shadow or substitution experiment. It does not establish a performance improvement: the scalar candidate evaluates six faces and several curves per part, and end-to-end capture, packing, validation, and publication cost remains unmeasured.
