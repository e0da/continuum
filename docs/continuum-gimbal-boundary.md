# Continuum and Gimbal boundary

**Decision:** Continuum is the reusable simulation engine. Gimbal is the game and experience layer built on Continuum. Gimbal KSP is a Gimbal game mode that targets compatibility with user-supplied KSP content and supported mods without requiring the KSP or Unity runtime. The current KSP addon is the strangler bridge and behavioral oracle used to measure, compare, and gradually replace host-owned systems.

This is a product and dependency boundary. It does not claim that Gimbal, standalone KSP content loading, or a complete replacement runtime exists today.

## Dependency direction

```text
Gimbal game modes ────────> Continuum engine contracts
        │                          ▲
        │                          │
        └── Gimbal KSP ──> KSP content compatibility
                                   ▲
                                   │ captured fixtures and comparisons
KSP + Unity <──── KSP addon ───────┘
```

Dependencies point inward toward Continuum contracts. Continuum must not reference Gimbal, KSP, Unity, CKAN, a particular save format, or a game mode. Gimbal may compose Continuum services and define play rules. KSP compatibility code may translate content into engine and game concepts, but it must not become a required engine dependency. The KSP addon may reference Continuum contracts and host APIs; host objects must stop at its adapter boundary.

The arrow from the addon to captured fixtures is an evidence path, not shared runtime authority. During a live replacement experiment, the [one-writer and transactional publication rules](active-physics-takeover.md) still apply.

## Ownership placement

| Owner | Belongs here | Does not belong here |
| --- | --- | --- |
| Continuum | Time and reference-frame models; simulation state and identity; spatial partitioning; fidelity transitions; deterministic scheduling; structural, contact, aerodynamic, orbital, thermal, and resource solver interfaces; checkpoint, replay, branch, and event primitives; backend selection and measurement contracts | Career progression, mission rewards, Kerbal-specific rules, UI, KSP classes, Unity objects, or assumptions about one content format |
| KSP adapter | KSP/Unity lifecycle observation; capture and translation of host state; provider and mod inventory; command-line and panel controls; guarded authority acquisition; publication, readback, rollback, and evidence export | General simulation policy, game progression, a second copy of engine state rules, or silent ownership of incumbent mod hooks |
| Gimbal | Application lifecycle; rendering and interaction; camera and media direction; game-mode selection; saves and user-facing history; strategy and mission experiences; presentation of engine events | Solver internals or KSP host integration |
| Gimbal KSP compatibility | Import of legally user-supplied KSP assets and data; mapping parts, vessels, bodies, saves, and declared mod contracts into Continuum/Gimbal concepts; KSP game-mode rules and fidelity profiles | Dependencies from Continuum back into KSP concepts, or a promise that arbitrary plugins can run without qualification |

An interface belongs in Continuum when it expresses simulation or runtime capability without importing KSP vocabulary; credible use by more than one experience is useful evidence, not a quota. A KSP-shaped input may still exercise an engine interface through a translation fixture; that does not make the shape part of the engine.

## Role of the current KSP addon

The addon has four jobs during migration:

1. **Observe:** capture inputs, outputs, lifecycle order, topology changes, and end-to-end cost from pinned KSP configurations.
2. **Compare:** run Continuum strategies in shadow mode against stock or a declared mod provider.
3. **Substitute:** take authority over one bounded channel or vessel class only after the ownership contract is proved, then measure behavior and cost against the same checkpoint.
4. **Export:** turn qualified traces and content mappings into portable fixtures that can run without KSP.

It is not the architectural center of Continuum. Harmony patches, Unity components, MFI integration, floating-origin handling, and KSP identifiers remain adapter concerns described by the [replacement](replacement.md), [state ownership](ksp-state-ownership.md), [integration](integration-map.md), and [compatibility](compatibility.md) documents.

KSP and supported mods remain the behavioral oracle for compatibility profiles. They are not the definition of every Continuum model: a Gimbal mode may deliberately choose different physics or rules, provided the profile names that difference and does not claim KSP equivalence.

## Migration and acceptance ladder

Each rung must leave the previous path runnable until its replacement has passed the stated comparison. Progress is by capability, not by copying KSP subsystem boundaries blindly.

1. **Portable kernel:** a Continuum subsystem runs from owned data without KSP or Unity assemblies. Acceptance requires deterministic fixtures, explicit error bounds, and backend-independent contracts.
2. **Captured oracle:** the addon captures the complete inputs and observable outputs needed for one bounded KSP behavior. Acceptance requires provenance, lifecycle identity, and replayable fixtures; missing channels remain explicit.
3. **Shadow parity:** Continuum evaluates the captured behavior while KSP remains authoritative. Acceptance requires profile-specific tolerances for outcomes and invariants across representative workloads.
4. **Live substitution:** the addon gives Continuum exclusive authority over that bounded behavior in a disposable KSP instance. Acceptance requires state agreement, lower or otherwise justified end-to-end cost, rejection of stale work, and verified restoration without mutating the source save.
5. **Standalone vertical slice:** Gimbal loads the corresponding user-supplied content and reproduces a complete playable slice on Continuum without KSP or Unity. Acceptance covers simulation, controls, rendering, save/load, and the declared compatibility profile together.
6. **Expanding compatibility:** repeat by subsystem and mod profile. The KSP runtime becomes optional only for the set of content and behavior that has passed standalone qualification.

The near-term performance experiment remains rung 4 for a narrow structural case; this decision does not replace its evidence gates with a broad engine rewrite. The [adaptive-fidelity work](replacement.md#adaptive-fidelity-and-long-time-spans) and existing portable kernels can advance independently at rungs 1–3.

## Explicit limits and open questions

- KSP asset and data loading needs a documented user-supplied-content and redistribution boundary before distribution.
- Binary KSP plugins cannot be assumed portable to Gimbal. Compatibility may require a declared data adapter, source-level port, or reimplementation against a future Gimbal mod API.
- The repository layout for Gimbal and shared contracts is undecided. Dependency direction should be enforced before code moves; repository boundaries alone do not enforce it.
- Rendering, audio, UI, and content streaming contracts need their own decisions when a standalone vertical slice makes them immediate.
- Exact KSP compatibility and an improved Gimbal physics profile are distinct acceptance targets and should produce distinct receipts.
