# Direct trajectories and a smooth potential split

This standalone experiment establishes a bounded direct Plummer trajectory reference and checks a smooth, potential-consistent pair decomposition. The original resolution schedule fails two noncircular gates; one explicit doubling passes the same gates. Neither result qualifies FFT trajectories or demonstrates a speedup.

Run with Python 3.11 or later; no scientific packages are required. Output paths must be new and their parent directories must exist.

```sh
python3 -B -m unittest tests.test_field_trajectory -v
python3 -B tools/field-gravity/trajectory.py --output artifacts/trajectory-baseline.json
python3 -B tools/field-gravity/trajectory.py --refinement-level 1 --output artifacts/trajectory-refined.json
```

A completed baseline report currently exits 2 because its gates fail. A completed qualified report exits 0. Invalid input or output errors exit 1; argparse syntax errors also use 2, so check that a report exists before treating an exit code as a scientific result. Existing output is never overwritten. JSON includes model, schedules, gates, every resolution's residuals, sampled positions, split adversaries, Python version and the script's SHA-256.

## Physical model and independent reference

Use normalized units, G = 1, softening ε = 0.6 and masses 2 and 5. Each isolated pair has potential energy

```text
Uij = −mi mj / sqrt(r² + ε²)
ai  = mj (xj − xi) / (r² + ε²)^(3/2).
```

The code accumulates each unordered pair symmetrically. There is no mesh, periodic image, external force or collision. Velocity Verlet advances positions and velocities at one fixed step. The velocity form of this integrator is also documented by [LAMMPS](https://docs.lammps.org/fix_nve.html).

For circular separation R = 2, radial force balance independently gives ω² = 7 / (R² + ε²)^(3/2). Sines and cosines then specify both bodies' positions and velocities in a plane inclined by 0.4 radians. Comparing the integrated endpoint after four periods to this analytic solution tests phase and state as well as invariants.

The noncircular fixture starts at separation 2.8 with 0.7 times the circular tangential speed. Its duration is two *nominal circular* periods at that starting separation; this softened orbit is not a Kepler ellipse. Its reference uses finer direct Verlet steps, so reference agreement is a convergence witness rather than an independent integration algorithm. The measured separation spans about 1.0984 to 2.8, crossing both split boundaries.

## Fixed protocol and gates

The initial schedule uses 128, 256, 512 and 1024 steps per nominal period, with noncircular references at 8192 and 16384. Level 1 doubles all those counts, including both references. Only levels 0 and 1 are supported. Model, duration and gates remain identical.

| Check | Gate |
| --- | ---: |
| Maximum relative energy excursion over every step | 10⁻⁴ |
| Maximum relative angular-momentum drift | 10⁻¹⁰ |
| Normalized center-of-mass and momentum drift | 10⁻¹⁰ |
| Forward then negative-step reversal residual | 10⁻⁸ |
| Endpoint position and velocity error | 10⁻³ |
| Final two adjacent endpoint-error improvement ratios | 3 through 5 |
| Noncircular reference-level agreement | 10⁻⁶ |
| Complete split versus direct endpoint difference | 10⁻¹⁰ |

Position errors are RMS vector errors per body divided by initial separation; velocity errors use the corresponding initial circular relative speed. COM drift uses the predicted inertial COM, divided by initial separation. Momentum drift divides by total mass times that velocity scale. Energy and angular-momentum changes divide by their nonzero initial magnitudes in these fixtures. Only the finest candidate must pass the invariant, endpoint and reversal gates; coarse failures stay visible. Reversibility is not an accuracy proof.

## Potential-consistent split

For unit mass product, write U(r) = −1 / sqrt(r² + ε²). Set the near weight w to 1 below radius 1.2 and 0 above 2.4. In between, with t = (r − 1.2) / 1.2, use

```text
w(t) = 1 − 10t³ + 15t⁴ − 6t⁵
Unear = w U                 Ufar = (1 − w) U
Unear′ = w U′ + w′ U        Ufar′ = (1 − w) U′ − w′ U.
```

Both derivatives sum to U′. Force on the first body points toward the second with magnitude mi mj U′. The weight and its first two derivatives join continuously at the boundaries. [GROMACS documents this quintic potential switch and its endpoint conditions](https://manual.gromacs.org/current/reference-manual/functions/nonbonded-interactions.html#modified-non-bonded-interactions). Here both complementary components remain present, retaining the full Plummer law.

The two components can be large and opposite in the transition region. Neither is claimed to be a separate attractive physical interaction or a validated multirate method. Both are evaluated analytically for every pair on every step.

The toy checks potential/force reconstruction to 10⁻¹², individual potential gradients by central differences to 10⁻⁶, and boundary derivative differences across a 2×10⁻⁷ radius interval to 10⁻⁵. Analytically w′ vanishes at both endpoints. These sampled checks support the algebra; they are not a numerical proof over all real radii.

The omission adversary matters: naive components w U′ and (1 − w) U′ still reconstruct the total exactly, yet neither is the derivative of its declared potential. Testing only their sum would miss the defect. Individual gradient checks expose an error of 0.82351; omitting just one product-rule term also breaks total reconstruction by 0.82351. The complete implementation's maximum sampled gradient error is 3.89×10⁻¹⁰ and force reconstruction error is 2.78×10⁻¹⁷.

## Observed result and limits

| Finest noncircular result | Initial schedule | One doubling | Gate |
| --- | ---: | ---: | ---: |
| Relative energy excursion | **1.06177×10⁻⁴, fails** | 2.65479×10⁻⁵ | 10⁻⁴ |
| Reference position agreement | **1.72656×10⁻⁶, fails** | 4.31640×10⁻⁷ | 10⁻⁶ |
| Reference velocity agreement | **2.14196×10⁻⁶, fails** | 5.35491×10⁻⁷ | 10⁻⁶ |
| Endpoint position error | 1.46755×10⁻⁴ | 3.66892×10⁻⁵ | 10⁻³ |
| Endpoint velocity error | 1.82065×10⁻⁴ | 4.55166×10⁻⁵ | 10⁻³ |

The circular fixture passes at both finest resolutions. After doubling, its normalized endpoint position/velocity errors are 2.98237×10⁻⁵ and 3.00517×10⁻⁵ over four periods. Both fixtures show approximately fourfold endpoint-error improvement on halving the step. The largest refined reversal residual is below 7×10⁻¹⁴; the largest complete-split endpoint difference is below 5×10⁻¹⁴.

The first failure is retained locally in `artifacts/field-trajectory-first.json`. Final baseline and refined receipts, generated from the same source hash, are `artifacts/field-trajectory-baseline-final.json` and `artifacts/field-trajectory-refined-final.json`. Their elapsed durations cover all fixture integration, references, diagnostics and split checks, excluding serialization and process startup. They are single local cost observations, not a comparative performance benchmark.

Seven tests pass. Before implementation, the one-step Verlet test failed for an unchanged state and the component-gradient test failed for a missing product-rule term. The implementation is bounded to 16 bodies and 131072 steps per call, but qualification covers only these two-body fixtures. It does not qualify arbitrary many-body dynamics, long horizons, close-encounter adaptivity, floating-point determinism across machines, a mesh split, or FFT trajectory integration. The [earlier hard-cutoff correction](field-gravity.md) remains unchanged and retains its continuity failure.
