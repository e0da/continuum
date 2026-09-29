# Continuum and Gimbal boundary

**Decision:** Gimbal's native Rust runtime and Space Race game are the primary development path. Continuum remains a KSP mechanics/performance reference and integration lab. Its portable experiments may inform Gimbal, but Gimbal does not require a Continuum runtime. A future Gimbal KSP mode may target qualified compatibility with user-supplied content and supported mods; that work is optional.

This is a product and dependency boundary, not a claim that standalone KSP content loading or a complete replacement runtime exists today. `KspContinuum` remains the addon namespace.

## Dependency direction

```text
KSP + Unity <── KspContinuum addon ──> captured fixtures and measurements
                                             │ selected findings
                                             ▼
                              Gimbal native Rust runtime + Space Race

Optional later: user-supplied KSP content ──> qualified Gimbal KSP mode
```

The arrows from Continuum to Gimbal carry evidence or explicitly selected code, not a runtime dependency. Gimbal owns its simulation architecture and game rules. The KSP addon may use host APIs, but host objects must stop at the boundary of any portable fixture. Optional KSP compatibility code may translate content into Gimbal concepts without defining the base game's runtime.

The arrow from the addon to captured fixtures is an evidence path, not shared runtime authority. During a live replacement experiment, the [one-writer and transactional publication rules](active-physics-takeover.md) still apply.

## Ownership placement

| Owner | Belongs here | Does not belong here |
| --- | --- | --- |
| Continuum lab | KSP behavior captures, portable solver experiments, performance comparisons, and candidate time/frame/physics models | Ownership of Gimbal's native runtime or Space Race rules |
| KSP adapter | KSP/Unity lifecycle observation; capture and translation of host state; provider and mod inventory; command-line and panel controls; guarded authority acquisition; publication, readback, rollback, and evidence export | General simulation policy, game progression, a second copy of engine state rules, or silent ownership of incumbent mod hooks |
| Gimbal | Native Rust simulation/runtime architecture; Space Race rules; application lifecycle, rendering, interaction, saves, and user-facing history | Required dependency on Continuum or KSP host integration |
| Optional Gimbal KSP compatibility | Import of legally user-supplied KSP assets and data; mapping parts, vessels, bodies, saves, and declared mod contracts into Gimbal concepts; KSP fidelity profiles | A promise that arbitrary plugins can run without qualification |

Portable findings should cross into Gimbal only when a concrete Gimbal workload benefits from them. A KSP-shaped input can exercise a portable experiment without making that shape part of Gimbal's runtime.

## Role of the current KSP addon in optional KSP work

The addon can support four jobs when KSP replacement or compatibility is pursued:

1. **Observe:** capture inputs, outputs, lifecycle order, topology changes, and end-to-end cost from pinned KSP configurations.
2. **Compare:** run Continuum strategies in shadow mode against stock or a declared mod provider.
3. **Substitute:** take authority over one bounded channel or vessel class only after the ownership contract is proved, then measure behavior and cost against the same checkpoint.
4. **Export:** turn qualified traces and content mappings into portable fixtures that can run without KSP.

It is not Gimbal's architectural center. Harmony patches, Unity components, MFI integration, floating-origin handling, and KSP identifiers remain adapter concerns described by the [replacement](replacement.md), [state ownership](ksp-state-ownership.md), [integration](integration-map.md), and [compatibility](compatibility.md) documents.

KSP and supported mods remain behavioral oracles for any compatibility profile. Space Race may deliberately choose different physics or rules; that difference should be named rather than presented as KSP equivalence.

## Optional KSP compatibility ladder

These rungs apply only if KSP replacement or a Gimbal KSP mode is pursued. Gimbal's native Rust runtime and Space Race game can progress without passing them. Each KSP rung leaves the previous path runnable until its replacement has passed the stated comparison.

1. **Portable kernel:** a Continuum subsystem runs from owned data without KSP or Unity assemblies. Acceptance requires deterministic fixtures, explicit error bounds, and backend-independent contracts.
2. **Captured oracle:** the addon captures the complete inputs and observable outputs needed for one bounded KSP behavior. Acceptance requires provenance, lifecycle identity, and replayable fixtures; missing channels remain explicit.
3. **Shadow parity:** Continuum evaluates the captured behavior while KSP remains authoritative. Acceptance requires profile-specific tolerances for outcomes and invariants across representative workloads.
4. **Live substitution:** the addon gives Continuum exclusive authority over that bounded behavior in a disposable KSP instance. Acceptance requires state agreement, lower or otherwise justified end-to-end cost, rejection of stale work, and verified restoration without mutating the source save.
5. **Standalone compatibility slice:** If a Gimbal KSP mode is pursued, Gimbal loads the corresponding user-supplied content without KSP or Unity. Acceptance covers simulation, controls, rendering, save/load, and the declared compatibility profile together.
6. **Expanding compatibility:** repeat by subsystem and mod profile. For a Gimbal KSP mode, claim independence from the KSP runtime only for content and behavior that passed standalone qualification.

The narrow live-performance experiment remains available at rung 4, but it is no longer a gate for Gimbal. The [adaptive-fidelity work](replacement.md#adaptive-fidelity-and-long-time-spans) and existing portable kernels can be evaluated against Gimbal workloads independently.

## Explicit limits and open questions

- KSP asset and data loading needs a documented user-supplied-content and redistribution boundary before distribution.
- Binary KSP plugins cannot be assumed portable to Gimbal. Compatibility may require a declared data adapter, source-level port, or reimplementation against a future Gimbal mod API.
- Any code transfer into Gimbal needs a concrete consumer and an explicit ownership decision; repository boundaries alone do not enforce independence.
- Rendering, audio, UI, and content streaming contracts need their own decisions when a standalone vertical slice makes them immediate.
- Exact KSP compatibility and an improved Gimbal physics profile are distinct acceptance targets and should produce distinct receipts.
