# Choosing coordinates and curve representations

Changing representation can make motion cheaper, but it must preserve the physical question: can these enclosing shapes interact at the same physical time? The useful goal for Continuum is cheap propagation plus reliable local encounter tests. A straight coordinate trace alone does not establish either.

## ASC model

| Object or relation | State or operation | Required invariant |
| --- | --- | --- |
| Trajectory segment | Identity, revision, epoch, validity, coefficients, error bounds | Bounds enclose every permitted trajectory throughout validity |
| Frame | Origin, axes, physical time mapping | Compared objects use a common physical time and distance metric |
| Pair | Subtract trajectories before enclosing their difference | Shared terms cancel; uncertainty remains enclosed |
| Time window | Recenter, subdivide, exclude or retain | Exclusion must imply no possible contact anywhere in the window |
| Work budget | Charge each screening operation | Exhaustion means unknown, never clear |
| Handoff | Capture, solve provisionally, validate, publish | Changed authority invalidates the whole result; no partial publication |

The first five relations are supported in part by the current [encounter planner](encounter-scheduler.md). General frame conversion and atomic local-solver publication are not implemented.

If positions have a shared moving origin, `xᵢ(t)=c(t)+rᵢ(t)`, then
`xᵢ(t)−xⱼ(t)=rᵢ(t)−rⱼ(t)`. An arbitrary common translation cancels exactly in the mathematics. The current pair test already cancels equal nominal accelerations before applying interval bounds. This can leave straight relative motion between two objects whose individual paths curve.

Separate object-dependent transformations do not provide that cancellation: their maps back to the common frame must participate in the distance test. A nonlinear coordinate map generally changes the distance calculation. If we actually integrate dynamics in an accelerating or rotating frame, the equations also need the corresponding frame terms. Screening prescribed positions and solving dynamics are different responsibilities.

## A representation experiment

For `y(t)=100−20t+t²` over `t∈[0,20]`, direct interval evaluation gives `[-300,500]`. It treats repeated appearances of time independently. The actual range is `[0,100]`.

Set `u=t−10`. The identical curve becomes `y=u²`, with `u∈[-10,10]`, immediately giving `[0,100]`. No trajectory approximation or new physical model is involved.

| Window | Direct interval | Midpoint-centered interval | Quadratic Bernstein hull | Exact range |
| --- | --- | --- | --- | --- |
| 0–20 s | −300…500 | 0…100 | −100…100 | 0…100 |
| 0–8 s | −60…164 | −12…100 | 4…100 | 4…100 |
| 9–11 s | −39…41 | 0…1 | −1…1 | 0…1 |
| 12–20 s | −156…260 | −12…100 | 4…100 | 4…100 |

Neither representation is always tightest. For a quadratic over `[l,h]`, the Bernstein control values are `q(l)`, `q(l)+q′(l)(h−l)/2`, and `q(h)`; their minimum and maximum enclose the curve because the Bernstein basis weights are nonnegative and sum to one. Intersecting independently valid enclosures remains valid.

The exact-rational [toy experiment](../tools/numerics/src/bin/curve_bounds.rs) checks 6,210 coefficient/window combinations against endpoint and stationary-point extrema:

```sh
cargo run --manifest-path tools/numerics/Cargo.toml --bin curve-bounds -- --output artifacts/curve-bounds-NEW.json
```

The output path must be new. These checks establish the tested rational enclosures, not floating-point correctness or a performance gain. A production version must carry outward rounding through coefficient conversion, remain safe under cancellation/overflow and justify its extra work using actual screening counts and elapsed times. The current planner deliberately remains the baseline until that comparison passes.

## Techniques to qualify next

1. **Centered and Bernstein enclosures.** Compare bounds on the same crossing, tangent, near-miss and sparse scenes. Require conservative results under numerical adversaries; measure false positives, early stops, interval counts and total cost. The [exact-rational event-certificate toy](../tools/numerics/src/event_certificate.rs) now compares coordinate-box bounds with Bernstein controls of the scalar polynomial `g(t)=|Δp+Δvt+½Δat²|²−R²`. For constant radius this has degree at most four. Strictly positive controls certify separation throughout a window, preserving the fact that coordinates refer to the same time. The toy also uses exact closest approach for zero-acceleration relative motion. Its 741 fixed cases gave the coordinate and distance methods the same 634 clear outcomes at subdivision depth six; distance used 3,497 nodes versus 3,585, while coordinate bounds won more single-window cases (504 versus 449). The analytic linear method handled 93 cases and cleared 74 without subdivision. The test found no sampled false clears. This is a mixed result, not evidence that the quartic form is faster. Production needs outward-rounded floating-point bounds, explicit uncertainty radii and a timed comparison against the actual planner on representative encounter scenes.
2. **Prescribed orbital segments.** Use an orbital propagator or precomputed ephemeris appropriate to the model, with validity and error bounds. NASA SPICE supports both two-body propagation and polynomial ephemeris representations, including Chebyshev segments; that is useful precedent for separating a trajectory provider from its consumer, not proof that our own approximation is bounded. [NAIF SPK documentation](https://naif.jpl.nasa.gov/pub/naif/toolkit_docs/C/req/spk.html).
3. **Conservative continuous collision detection.** Reuse established methods and adversarial workloads when moving from enclosing spheres to actual shapes. Tight Inclusion offers an inclusion-based CCD implementation and benchmark; its guarantees cannot be transferred to our curved sphere model merely by citing it. [Authors' project](https://continuous-collision-detection.github.io/tight_inclusion/).

Open questions are the trajectory adapter's error contract, how much tighter bounds reduce total work, and which component owns atomic publication into KSP. No change of coordinates removes those obligations. N-body simulation is not required for this path.
