# Worldline tube experiment

`WORLDLINE-TUBE-001` asks whether a bounded path envelope can cheaply reject independent objects while retaining every possible interaction for refinement. It is a portable toy built over the existing encounter planner. It does not control KSP time, propagate orbital trajectories, resolve contacts, or establish production collision safety.

## Contract

A tube contains a nominal constant-acceleration curve and five parts of its enclosure:

- a physical support with half-extents and nominal quaternion orientation;
- isotropic position and velocity uncertainty;
- a residual acceleration bound;
- a common frame and physical epoch;
- validity predicates and an explicit expiry.

The current screening shape is the support's circumscribed sphere. This safely makes orientation irrelevant to the first broad phase while preserving orientation and its uncertainty in the data contract for a later, tighter oriented bound. The approximation radius grows as `positionError + velocityError*t + 1/2 accelerationError*t^2`. Missing bounds, failed predicates, mismatched frames or epochs, exhausted budgets, and numerical uncertainty cannot produce `clear`. A request beyond either expiry produces `expired` rather than silently shortening the requested claim.

Keep the representation ladder lightweight. Start with point plus radius, then a center/radius polynomial curve. Consider capsules, ellipsoids, support functions, or several curve envelopes only when measured false positives or refinement work justify them. The data contract may retain orientation before the broad-phase shape uses it; that avoids making a tighter and more fragile shape the entry cost for every object.

The result is one of `clear`, `candidate`, `unknown`, or `expired`. A candidate is only a possible overlap interval. A local solver must refine it before any collision response. Revision and validity must be checked again before a result can affect authoritative state; the transactional publication seam remains separate.

## Toy comparison

Run the deterministic receipt into a new path:

```sh
dotnet run --project tools/KspContinuum.WorldlineTubeToy -c Release -- \
  --output artifacts/worldline-tube-NEW.json
```

The cases cover straight separated paths, accelerated crossing, linear crossing, exact tangency, near miss, an uncertain burn, an intentional conservative false positive, expiry, a missing acceleration bound, and a failed validity predicate. The oracle samples 200,001 points from each prescribed actual curve, with a small contact tolerance; these polynomial fixtures also have analytically inspectable contact geometry.

Qualification requires zero false negatives among oracle-positive fixtures. The receipt reports false positives and interval work rather than hiding them. `qualified` means only that this fixed toy set met that rule. Dense sampling is not a continuous-collision proof, the existing interval arithmetic is not a certified package, and the fixture does not cover general curved ephemerides, rotating supports, discontinuities, frame transforms, or floating-point range adversaries.

A deterministic outer tube and a Monte Carlo path population have different jobs. The outer tube may certify exclusion when its bounds are valid. Sampled interior paths can prioritize candidates, choose refinement effort, and estimate conditional risk, but cannot certify absence. Importance sampling over path space—and rendering or path-integral algorithms that inspire it—is only a computational analogy here; the samples do not carry physical probability amplitudes.

## What the result can decide

If the toy passes, the representation is suitable for further comparison as a conservative candidate generator. It does not yet justify replacing the encounter planner or integrating with KSP. The next useful challenger is a tighter oriented or relative-curve enclosure run against the same fixtures and receipts. It should win only if it preserves zero observed false negatives while reducing false positives or total refinement work enough to pay for its additional arithmetic.

Open questions are how trajectory providers certify approximation error, how burns split or invalidate tubes, how orientation bounds should tighten support without unsafe assumptions, and which floating-point method can support a production safety claim.
