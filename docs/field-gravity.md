# Isolated field-gravity force experiment

This runnable experiment compares a padded-grid field candidate against direct softened Newtonian forces. Both use the same normalized units, G=1, Plummer softening ε=0.6, isolated boundary model, masses, and body positions. It measures a single force evaluation, including capture, grid construction, convolution and sampling. It has no trajectory integration or KSP bridge.

The first local mesh-only run **did not qualify the full fixture**. At the finest 65³ physical grid, the dense cluster had 5.833% normalized RMS acceleration error, exceeding the predeclared 5% gate. Sparse and rotated-pair cases passed at that resolution. This is a retained approximation limit, not a reason to relax the gate. The optional near-field experiment below tests a correction alongside this unchanged baseline.

## Run and reproduce

Use a separate environment with the pinned NumPy dependency. This slice was exercised with Python 3.13 and NumPy 2.2.6; it does not require NumPy in the normal application environment.

```sh
python3.13 -m venv artifacts/field-gravity-venv
artifacts/field-gravity-venv/bin/python -m pip install --only-binary=:all: -r tools/field-gravity/requirements.txt
artifacts/field-gravity-venv/bin/python -B -m unittest tests.test_field_gravity -v
artifacts/field-gravity-venv/bin/python -B tools/field-gravity/run.py --output artifacts/field-gravity-new.json
```

The output path must be new. Exit 0 means every case and translation check for the selected strategy passes at the finest requested grid; exit 2 means a complete report was written but qualification failed; exit 1 means invalid setup or an output error. All requested resolutions and failures remain in the report. Defaults are grids 17,33,65 and three samples per case; `--grids 17 --samples 1` is a short diagnostic. The CLI accepts only ascending subsets of those grids and 1–5 samples. Ten optional tests skip explicitly in environments without NumPy; run them in the pinned environment for actual evidence.

## ASC model and physical contract

| Object/state | Operation | Required invariant or limit |
| --- | --- | --- |
| Snapshot | Copy positions and masses to float64 | 1–128 bodies; finite positions in closed [0,8]³; positive masses bounded to [10⁻⁶,10⁶] |
| Physical force law | Direct pair acceleration | aᵢ=Σⱼ≠ᵢ Gmⱼ(xⱼ−xᵢ)/(|xⱼ−xᵢ|²+ε²)³ᐟ²; no periodic images |
| Endpoint grid | N nodes per axis with h=8/(N−1) | Coordinates 0,h,…,8, not cell centers; all refinements hold ε fixed |
| Cloud-in-cell (CIC) source | Deposit each mass to eight adjacent nodes with trilinear weights | Weights sum to one; mass stored per node, so no h³ density factor is required |
| Padded field | Embed source in a (2N)³ zero-filled array and convolve with the sampled force kernel | Every physical-node displacement −(N−1)…N−1 is represented uniquely |
| Body readout | Interpolate grid acceleration using the same eight weights | Reciprocal, matching deposition/readout; no separate nearest-node lookup |
| Assessment | Compare full acceleration vectors against direct pairs | Accuracy, isolated self-force, net-force and mass gates; retain failed rows |

At the upper face x=8, interpolation uses the last physical cell with fractional coordinate one. At the lower face it uses the first cell with fractional coordinate zero. No source mass wraps across a face. Positions outside the box are rejected; the box is an allocation region, not a periodic universe.

The kernel is the gradient of the isolated softened potential Green function Φ(r)=−G/√(|r|²+ε²): K(r)=−∇Φ(r)=−Gr/(|r|²+ε²)³ᐟ², with r=target−source. The FFT method convolves deposited **mass** with this vector kernel directly; it does not differentiate a periodically solved potential or subtract a mean density. K(0)=0. The scalar potential’s zero at infinity defines this isolated model; there is no periodic-Poisson zero-mode correction.

## Why padding and complex transforms represent this operation

Let wᵢ,a be the CIC weight of body i on node a. Deposited node mass is M_b=Σⱼmⱼwⱼ,b. The candidate evaluates

```
a_i^grid = sum_a w_i,a sum_b K(x_a - x_b) M_b
         = sum_j m_j sum_a,b w_i,a w_j,b K(x_a - x_b).
```

The discrete convolution is linear in deposited mass. Complex Fourier multiplication evaluates that convolution; it does not change its physical kernel. For each axis, the physical node offset is within [−N+1,N−1]. Padding to 2N maps every such offset to a unique circular-array index, so restricting the result to physical nodes reproduces the corresponding isolated discrete sum. The unused ±N offset plane is set to zero, preserving odd symmetry. The endpoint-pair analytic test checks the longest separation across the box and would expose periodic-image contamination.

NumPy’s real forward transform and inverse represent the Hermitian-symmetric spectrum of a real field. This fixture uses the default backward normalization, with explicit inverse shape and axes. See the primary [rfftn](https://numpy.org/doc/2.2/reference/generated/numpy.fft.rfftn.html) and [irfftn](https://numpy.org/doc/2.2/reference/generated/numpy.fft.irfftn.html) documentation. Complex coefficients provide a convenient representation and convolution algorithm; they do not provide extra independent physical state or unlimited capacity.

Because K is odd and source/readout weights match, the self term cancels in exact arithmetic: pair the (a,b) term with (b,a). The same symmetry makes Σᵢmᵢaᵢ vanish for an isolated set. Both are checked numerically. CIC still spreads each point over a grid-dependent cloud, so its pair force generally differs from direct point-particle Plummer force at the same ε. This is precisely the approximation under test. Fixed physical softening is never changed to hide resolution error.

## Oracles, predeclared gates, and retained evidence

The direct oracle computes symmetric body pairs without using the grid kernel builder, deposition, FFT, or interpolation. A separate two-body formula checks magnitude, direction and mass scaling, including both endpoint-aligned and oblique off-grid geometry. The initial zero-force implementation failed the analytic test with expected x acceleration 0.0774704249447216 (witnessed RED); all six tests passed after implementation (GREEN).

For nonzero reference field, define A_rms=√meanᵢ|aᵢ^direct|². Each row must have RMS error/A_rms ≤0.05 and maxᵢ|aᵢ^grid−aᵢ^direct|/A_rms ≤0.15. Using a global scale avoids dividing by a nearly zero force on an individual body. This can mask some relative error on weak-force bodies, so raw per-body accelerations are retained. When the reference field is identically zero, require max|a|/(GΣm/ε²) ≤10⁻¹⁰ instead. Net force is normalized by G(Σm)²/ε² and must be ≤10⁻¹²; relative deposited-mass error must be ≤10⁻¹². These gates were selected before the implementation’s first full run.

Fixtures include one off-grid isolated body, a two-body endpoint case, an oblique off-grid pair, translated pair, sparse 32-body scene, translated sparse scene, and dense 32-body cluster. Positions and masses are stored in each report, alongside seed 1729. Translation is a noninteger-cell displacement [0.173,−0.219,0.137]; translation differences must satisfy the same RMS/worst gates. Direct translation residuals are also reported. Refinement trends are diagnostics: roundoff-level self-force and aligned-pair errors need not decrease monotonically.

First observed normalized RMS errors:

| Case | 17³ | 33³ | 65³ |
| --- | ---: | ---: | ---: |
| Oblique pair | 3.201% | 1.218% | 0.198% |
| Sparse | 12.038% | 3.291% | 0.891% |
| Translated sparse | 11.328% | 3.519% | 0.945% |
| Cluster | 52.730% | 22.132% | **5.833%, fails** |

The six noncluster cases pass at 65³; the cluster misses the RMS gate despite satisfying the worst-error gate. Off-grid isolated self-force is approximately 10⁻¹⁷ in its normalized units. These are finite-fixture observations, not rigorous interval error bounds or universal convergence rates. The report’s finest-grid qualification remains false; no trajectories are advanced.

## Timing and resource limits

Each sample reconstructs the kernel and source arrays. The timer includes input copy and validation, allocation, deposition, kernel construction, forward/inverse transforms, interpolation and conversion to ordinary output lists. It excludes fixture generation, accuracy assessment, report hashing/serialization, and a discarded small setup run. Direct/field ordering alternates within each case. Raw timings, host architecture, Python/NumPy versions and source SHA are retained. There is no process worker, transfer layer, kernel cache, JIT, or GPU in this comparison.

The direct baseline is a scalar Python pair loop; the field path calls compiled NumPy FFT routines. Their timings describe these implementations only and cannot establish an algorithmic crossover against an optimized direct, tree, FMM, or production particle-mesh backend. A timing row that fails accuracy is not a matched-accuracy speed result. Even a passing row supports only its recorded fixture, host and accuracy budget.

In the local three-sample macOS arm64 run, the passing sparse 33³ case took median 10.05 ms for the field path versus 0.85 ms direct; at 65³ it took 88.56 ms versus 0.86 ms. The field candidate was slower at these small body counts. The raw report is retained locally under ignored `artifacts/field-gravity-final.json`; its arrays and provenance make this conclusion inspectable without publishing runtime artifacts.

The largest padded grid is 130³ (2,197,000 real cells). Bodies, grid sizes and repetitions are bounded before allocation. Force components are solved and sampled sequentially rather than retaining three padded fields. A 512 MiB planning budget covers the fixture’s primary arrays and expected FFT workspace on the tested environment; it is not an enforced process RSS limit or a bound on every NumPy implementation’s internal workspace. A separate local macOS `/usr/bin/time -l` run over all seven 65³ cases recorded 143,278,080 bytes maximum resident set size (about 136.6 MiB), retained in ignored `artifacts/field-gravity-memory.txt`. The API is an experiment surface, not a hardened general service.

The mesh-only slice excludes integration error, orbital conservation over time, close-encounter handling beyond the chosen fixed softening, adaptive mesh corrections, contacts, joints, and live stock-force capture. Its observed cluster failure prevents a general force-replacement claim. The follow-on below keeps the same physical law and accuracy gates while accounting for correction cost.

## Follow-on: local pair replacement

```sh
artifacts/field-gravity-venv/bin/python -B tools/field-gravity/run.py --near-field --output artifacts/field-gravity-next-new.json
```

`--near-field` adds one strategy and three adversarial scenes; it does not change the mesh or direct implementations, original seven fixtures, ε, or accuracy gates. The correction selects pairs whose physical separation is at most 2ε=1.2, independent of grid resolution. This cutoff was fixed before the first corrected run. It enumerates every unordered body pair, then evaluates 64 stencil-node pairs for each selected pair. This is a bounded scientific implementation, not a scalable neighbor-search structure.

Let D_ij=(x_j−x_i)/(|x_j−x_i|²+ε²)³ᐟ² be the unit-source-mass exact acceleration, and let M_ij=Σ_a,b w_i,a w_j,b K(x_a−x_b) be the unit-mass interaction already included by the mesh. For each selected pair, the candidate adds m_j(D_ij−M_ij) to a_i and subtracts m_i(D_ij−M_ij) from a_j. Thus it **replaces** the mesh pair contribution instead of adding another copy of gravity. Pair force remains equal and opposite. The candidate computes D and M itself; it never reads the direct oracle's output. Self and far-pair contributions remain unchanged.

If all body pairs are selected, the corrected result reduces algebraically to direct softened forces, up to floating-point differences. A dense-cluster success therefore verifies subtraction/replacement and its cost; it is not evidence of a cheaper or novel direct solver. The extra asymmetric eight-body fixture selects only two of 28 pairs, so it tests a genuinely mixed near/far result. Two more fixtures sit 10⁻⁶ inside and outside the cutoff. Existing translated scenes still exercise grid sensitivity.

The corrected timer starts before a complete fresh mesh solve. It includes repeated capture/stencil setup for correction, all-pairs neighbor search, selected pair replacement, finite-output validation and output conversion. It reuses no previously timed baseline result. `meshTotalSeconds`, `correctionCaptureSeconds`, `neighborSearchSeconds` and `pairCorrectionSeconds` expose costs; `totalSeconds` is the complete corrected call. Default three-sample runs rotate direct/mesh/corrected order so each occupies each position once. Single-sample runs cannot balance order and are diagnostic only.

Report compatibility is explicit: `qualified`, `comparisons`, `fieldAcceleration`, `finestGridQualified` and `allRequestedGridsQualified` still describe the **uncorrected mesh**. The new `correctedQualified`, `correctedComparisons`, `correctedAcceleration`, `correctedFinestGridQualified` and `correctedAllRequestedGridsQualified` describe the correction. CLI exit status selects the corrected finest-grid decision only when `--near-field` is present. Neither decision qualifies trajectories. The uncorrected cluster failure remains in every corrected-mode report.

### Follow-on observations

The independent close-pair analytic test first failed when the candidate merely returned the mesh result: x acceleration 1.3313583255659909 versus expected 3.11493994709756. After pair replacement, all ten tests passed, including mixed-pair no-double-counting, isolated/far outputs unchanged, cutoff selection, report decisions, and retained coarse-grid failures. The original capture, stencil, direct, field, assessment and fixture functions were checked structurally identical to baseline `72028a4`.

The first corrected run contains ten cases × three resolutions × three samples. The fixed gates pass for all corrected cases and translation checks at 65³. They do not pass at every requested resolution: the just-outside-cutoff 17³ case retains 8.847% RMS error.

| Scene | Selected pairs | Corrected RMS at 17³ | At 33³ | At 65³ |
| --- | ---: | ---: | ---: | ---: |
| Dense cluster | 496 / 496 | 2.39×10⁻¹⁴% | 3.26×10⁻¹⁴% | 3.90×10⁻¹⁴% |
| Sparse | 13 / 496 | 2.084% | 0.446% | 0.113% |
| Mixed near/far | 2 / 28 | 0.0419% | 0.0175% | 0.00319% |
| Just outside cutoff | 0 / 1 | **8.847%, fails** | 2.994% | 0.0567% |

For the local dense cluster at 17³, medians were 7.06 ms corrected, 2.12 ms mesh-only and 0.85 ms scalar direct. At 65³ they were 91.84, 86.87 and 0.84 ms. Correction resolves this force error at additional cost and is slower than direct evaluation on the measured small scenes. Differences between independently timed calls can be smaller than timing noise; do not infer negative correction overhead by subtracting medians from different calls. Use the per-call phase timings instead.

The hard cutoff is an explicit failure of continuity qualification. Moving the cutoff adversary's separation by 2×10⁻⁶ changes the two-body direct acceleration vector by about 6.24×10⁻⁶ in Euclidean norm. The corrected vector changes by 0.2367, 0.0801 and 0.00151 at 17³, 33³ and 65³. This shows an artificial switching jump even where static force gates pass. `cutoffContinuityQualified` and `trajectoryQualification` remain false. No trajectory or orbital-conservation claim is justified without addressing and independently testing this transition.

The raw receipt is retained locally as ignored `artifacts/field-gravity-next-first.json`. It retains actual inputs, all baseline and corrected force arrays, individual samples and failure rows. This follow-on supports a narrow conclusion: exact local pair replacement can fix the selected cluster's force error, while a mixed near/far scene also passes; its small-case cost and switching artifact prevent a production replacement claim. It does not select a new cutoff, larger softening or relaxed gate based on these results.
