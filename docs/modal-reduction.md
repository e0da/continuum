# Modal rocket reduction

`MODAL-ROCKET-001` asks whether a rocket-like spring chain can retain gross motion and a few flexible modes without pretending that every load is low dimensional. It is a deterministic, pure-Python toy. It does not use KSP parts, nonlinear joints, rotation, contact, breakage or a production solver.

Run it with Python 3.11 or later. The output must be a new file in an existing directory.

```sh
python3 -B -m unittest tests.test_modal_rocket -v
python3 -B tools/modal-rocket/experiment.py --output artifacts/modal-rocket.json
```

The model is a one-dimensional free-free chain of 32 unit masses, uniform springs and dashpots. Its exact discrete-cosine eigenvectors provide an independently checkable basis. The full model advances 64 position/velocity scalars. The candidate retains the center-of-mass mode and the first six flexible modes, advancing 14 modal scalars. Both use the same fixed step and force history. State-count reduction is reported only as model structure; this experiment does not time or claim a performance win.

## Fixed decision gates

Two fixed workloads distinguish where the representation should and should not work:

- `smooth` raises thrust smoothly at one end and adds a broad, time-limited gust. It must keep maximum shape RMS error at or below 8%, tip-shape error at or below 12%, and center-of-mass error at or below 10^-10 m.
- `localizedImpulse` gives one end mass an instantaneous 1 m/s velocity. It uses the same frozen gates and is expected to fail because the event excites omitted high-frequency modes.

The experiment qualifies only when the smooth workload passes and the impulse workload fails. This unusual combined criterion protects both the useful domain and the declared boundary. A candidate that makes the impulse pass may be interesting, but it changes this experiment and requires new predeclared cost and accuracy gates.

The current result is:

| Workload | Shape RMS error | Tip-shape error | Result |
| --- | ---: | ---: | --- |
| Smooth thrust and gust | 1.683% | 0.942% | passes |
| Localized impulse | 24.172% | 24.060% | retained failure |

Maximum center-of-mass disagreement is below 1.8×10^-15 m in both runs. That supports the gross-mode bookkeeping in this fixture. It does not establish conservation under KSP frame changes, staging or collisions. The receipt includes all gates, checks, bounded schedules, sampled trajectories, environment identity and source hash. Serialization rejects nonfinite values, and exclusive creation prevents overwriting prior evidence.

## Representation contract

Continuum will assess unusual representations through an object-operation-invariant contract:

- **Objects** are typed physical state, geometry, interactions and aggregates. Here they are chain state, modal coordinates and the fixed basis.
- **Operations** compose or split objects, change frames, advance time, couple systems, and project or lift between representations. Here projection and reconstruction connect physical and modal state.
- **Invariants** cover units, frame behavior, momentum or energy accounting, topology identity, deterministic ordering and bounded representation error. Here exact basis orthonormality, the center-of-mass mode and frozen behavioral gates are checked.

A compact representation is interchangeable only for observations inside its declared envelope. Its encoding does not redefine the physical meaning of force, torque, pose or energy.

## Focused research shelf

The next representations worth small experiments are deliberately limited:

1. **Spatial wrenches and screw transforms** could make force-at-offset aggregation and local-frame batching explicit. A translated-thruster bench should require invariant resultant motion and wrench-power pairing.
2. **Reduced contact patches** could replace noisy clusters of landed point contacts with a support patch while retaining detailed contacts near impact and edges. Creep, penetration, energy injection and transition pops decide it.
3. **Port-Hamiltonian accounting** could provide an energy ledger across joints, contact and solver handoffs even if it does not become the integrator. Failure to expose injected energy would kill the added layer.
4. **Lie-group rigid-body stepping** could reduce long-horizon orientation and momentum drift. A torque-free asymmetric-body bench should compare error and complete step cost against ordinary quaternion integration.
5. **Directional and low-rank aerodynamic bases** could compress captured response over direction, Mach and control state. They wait for real aero evidence and must preserve positivity, rotational behavior, tail error and exact fallback.

The modal result earns a later nonlinear subassembly experiment, not integration into KSP. That follow-on should include rotation, changing thrust, a topology change and a comparison with measured stock behavior before any live authority is considered.
