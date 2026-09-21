# Conservative encounter planning

The portable encounter fixture implements the first step of the [interaction-regime design](interaction-regimes.md): choose how far each object can advance before a possible local interaction requires refinement. It consumes prescribed trajectories. It does not integrate bodies, resolve contact, replace KSP time, or run an n-body model.

## Input contract

Each immutable `EncounterMotion` contains a stable ID, generation, common epoch and frame name, initial center/velocity, enclosing radius, validity duration, lookahead deadline, position/velocity error and a nullable residual-acceleration bound. The optional `nominalAcceleration` vector defaults to zero and declares the center curve `p(t) = p₀ + v₀t + ½at²`. `AccelerationBound` bounds residual acceleration relative to that curve: explicit zero guarantees the declared nominal trajectory; a missing residual bound means unknown, even with a supplied nominal acceleration.

This is a constant-acceleration polynomial, not a Kepler orbit propagator. An orbit adapter would need to bound its deviation from the polynomial over its validity duration; sampling an orbit and fitting a curve alone does not supply that guarantee.

All predictions must use the same epoch and nonrotating coordinate frame. A frame name is a caller declaration, not a verified coordinate transform. The enclosing sphere must cover rotating geometry. Residual acceleration and uncertainty bounds must remain valid throughout the screening interval, including permitted changes of input. The planner cannot infer these guarantees from an arbitrary mod callback.

The shared screening horizon is the minimum requested/valid duration across the input set. Within that horizon, individual dependency deadlines and encounters can shorten an object's advance. This first version does not advance different islands beyond another prediction's validity expiry; it needs fresh predictions at that boundary.

## Output and ownership

A plan contains sorted potential encounter intervals, per-object advance limits, connected groups and work counters. A potential encounter is not a confirmed collision. A lower interval boundary is a conservative stopping point; overlapping uncertainty tubes may stop objects that would in fact miss. The interval is the first region screening could not exclude, not a guarantee that a true impact occurs between its endpoints.

`EncounterScheduler.Replace` admits a new immutable prediction set and invalidates previous plans. `IsCurrent` requires both the owning scheduler instance and its revision. Returning to an earlier state does not revive an old plan. Inserting debris or changing a command requires replacement and screening again before applying any recommendation. The scheduler is a synchronous planning owner, not a concurrent event-processing service or a state-commit API. `IsCurrent` is a freshness check, not an atomic check-and-apply transaction; a future game-side publisher must own that boundary. Work is count-bounded, with no asynchronous cancellation or worker dispatch in this version.

Unknown bounds, unresolved numerical conditions and exhausted screening capacity produce explicit non-complete results with zero proposed advance for the whole scene. Any retained candidate prefix is diagnostic, not proof that unexamined pairs are clear. The conservative fallback can be expensive; it deliberately sacrifices progress when the model cannot justify independent work.

## Numerical and work limits

The broad phase sweeps enclosing boxes along the axis with the largest global swept extent, breaking ties X/Y/Z. This heuristic avoids the measured fixed-X degeneracy; it does not guarantee subquadratic work. Both swept boxes and pair screening include the quadratic term over the entire time interval, including interior turns. Pair screening uses outward interval arithmetic and subdivides time from left to right until it can exclude an interval or reaches the requested tolerance.

Each arithmetic operation pads outward by one binary64 representable step, assuming ordinary IEEE-754 operations with subnormal values. This is not a certified interval library or a proof across platforms. Precision lost before input capture must be covered by the caller's error bounds. Arithmetic overflow or an unrepresentable requested time resolution produces `NumericalUncertainty` and zero advance.

A plan accepts at most 4,096 objects. Default budgets are 100,000 pair tests, 10,000 candidates and 200,000 interval tests, with a 0.0001 s time tolerance. Maximum configurable budgets are 10 million pair/interval tests and 1 million candidates. Exhaustion returns an explicit incomplete result rather than accepting an unchecked suffix.

## Runnable fixture

```sh
dotnet run --project tests/KspContinuum.Encounter.Tests -c Release
dotnet run --project tests/KspContinuum.Encounter.Oracle.Tests -c Release
python3 -m unittest discover -s tests -p test_encounter_bench.py -v
dotnet run --project tools/KspContinuum.EncounterBench -c Release -- \
  --quiet 1024 --samples 5 --output artifacts/encounter-first.json
```

The output path must not exist. Exit 0 means the declared fixture gates passed; exit 2 retains a completed report with failed gates; exit 1 reports invalid input or an execution error. Inputs, candidate intervals, groups, work counts and repeated wall times are recorded. Timing excludes input construction, result hashing, JSON serialization and process startup, and uses one warmup per scene in a fixed scene order. It is not a matched-performance comparison against stock KSP or a global fine-step solver.

The mixed fixture puts two 10 m-radius spheres at ±50 km moving toward each other at 5 km/s. Under those declared trajectories, first contact is at 9.998 s, while a 20 s endpoint check would see them separated again. Quiet objects sit far outside their swept envelopes. Gates require the approaching pair to stop by analytic contact and the unrelated quiet objects to retain the full horizon.

A second sparse scene rotates the separation onto a different axis to prevent a convenient frame orientation from hiding unnecessary pair work. Additional cases cover tangency, a near miss, acceleration uncertainty, zero lookahead, unknown bounds, inserted debris and dense-scene budget exhaustion. Input permutations must preserve the semantic plan, and replaced/foreign plans must be rejected. This proves a bounded contract for these fixtures; input-order determinism does not prove cross-platform or worker-count determinism. The current planner runs synchronously on one thread.

## Qualification boundary

The independent oracle uses analytic translating and accelerating sphere cases, including narrow contact that uniform coarse sampling misses and an interior curved encounter that endpoint chords miss. Actual orbital prediction adapters, terrain/atmosphere, rotating frame transforms, contact/joint dynamics, dissipative replay, concurrent scheduling and multiplayer authority remain separate work. No momentum or energy conservation claim follows from planning alone because the fixture performs no state transfer or physical integration.

Sparse candidate work is the intended advantage. Dense swept envelopes can still expose quadratic pair work and connect all objects into one group. Bounds prevent unlimited work but do not make those cases cheap. The important mixed-workload question is whether one encounter forces unrelated quiet objects to shorten their horizons; the fixture measures that separately from elapsed runtime.

See [trajectory representations and the exact-rational ASC experiment](trajectory-representations.md) for the next enclosure comparison.

## Curved-path fixture

The runner also places a radius-1 sphere at `(0,100,0)` with velocity `(0,-20,0)` and nominal acceleration `(0,2,0)`, alongside a stationary radius-1 sphere at the origin. Its center follows `y=(t−10)²` over 20 seconds. The start and end are both 100 m away; their connecting chord never approaches the origin. The actual curve reaches first contact at `10−√2` seconds and then separates again. The fixture requires a conservative stop before contact while unrelated quiet objects keep their full 20 seconds. Moving the stationary sphere to x=3 gives a clear curved near miss.

The serialized receipt includes each nominal acceleration vector. Older v1 receipts without that field were produced for the earlier linear-only fixture and imply zero nominal acceleration. The semantic plan hash describes output; it is not an input fingerprint or a replay identity.

Interval arithmetic can conservatively overestimate the curve's extent because repeated occurrences of time lose correlation. A localized possible-contact interval can occur before actual contact; its width does not bound that early-stop error. This favors safety over progress. Tighter polynomial envelopes and orbit adapters need their own measured qualification.

## First measured experiment (linear-only implementation)

The local seven-sample fixtures passed with 32, 256, 1,024 and 4,094 quiet objects plus the approaching pair. In the 4,096-object mixed scene, all 4,094 quiet objects retained 20 s of advance. The approaching pair stopped at 9.997940063476562 s, before analytic contact at 9.998 s. Screening required one pair test and 33 interval tests. Median full-plan time on that local run was 7.8038 ms; this is a single-machine diagnostic, not a stock-physics speedup.

An initial fixed-X implementation examined all 496 pairs in a 32-object scene separated only along Y. The adaptive-axis implementation examined zero pairs in that scene and also in the 4,094-object sparse scenes. A dense 64-object scene deliberately exhausted its 64-candidate budget on the 65th pair test and withheld advancement for every object.

The local experiment manifest binds source hashes and four immutable JSON receipts. The first failed orientation receipt is retained separately as evidence from the earlier implementation. Focused tests pass 44 assertions and an independent analytic oracle passes 224 assertions. The external console consumer tests the serialized output, exit behavior and exclusive receipt creation. These are standalone planner results; KSP remained closed and nothing was installed.

## Curved fixture result

The seven-sample 4,096-object fixture retained the full 20 seconds for all 4,094 quiet objects and stopped the curved pair at 8.585281372070312 seconds, before analytic contact at 8.585786437626904 seconds. It performed one pair test and 167 interval tests. Median full-plan time was 8.9650 ms on this local run; the earlier linear result is not a controlled performance baseline. Focused tests pass 55 assertions and the independent analytic oracle passes 282. Native Plugin and Mission compilation succeeds; nothing was installed.
