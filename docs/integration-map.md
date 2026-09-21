# KSP integration responsibilities

Use targeted inspection to establish contracts before intercepting behavior. Prefer primary documentation and community source; inspect owned game assemblies when a specific lifecycle or ordering question remains. Record the identity inspected, the consequence and a testable boundary. Do not distribute proprietary source or assemblies.

## Control path observed in the owned KSP 1.12.5 assembly

Assembly-CSharp SHA-256: `8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27`; MVID: `10657063-2fc3-43a7-84fa-d39e75e877bf`.

| Owner | Responsibility observed | Integration consequence |
| --- | --- | --- |
| FlightInputHandler | Supplies control state through the vessel input path | Distinguish pilot state from later autopilot changes |
| Vessel.FeedInputFeed | Invokes pre-autopilot, autopilot, post-autopilot, then fly-by-wire delegates | Post-autopilot is not the last vessel hook; recorder checks tail ownership |
| Vessel/Part | After readiness checks, dispatches the shared control state recursively to parts | Tail capture is before part consumption, not proof of final actuator values |
| VesselAutopilot | Writes steering and can re-enable itself from the SAS action group | An input lock alone does not disable SAS; initial replay requires both sources off |
| StageManager | Fires onStageActivate before activating parts and finishing stage bookkeeping | Log notification separately from observed topology; analog player stops at it |
| GamePersistence / FlightDriver | Create/start game and launch a craft using a crew manifest | Optional mission runner creates a unique sandbox and checks launchpad occupancy |

The four vessel callback fields contain constructor no-ops in this exact build. The replay allowlist binds their method identities to the inspected module; it does not allow arbitrary code merely because it lives in the stock assembly. This detects delegate conflicts, not all Harmony patches or direct state writers. Cleanup subtracts only owned delegates.

## Community owners

See [compatibility boundaries](compatibility.md). ModularFlightIntegrator, KSPCommunityFixes, joint reinforcement and background models may each own work a replacement would otherwise duplicate. For the optional mission runner, MechJeb 2.15.3.0 is a controller under test: it remains responsible for guidance and burn execution, while Continuum records observations and evaluates explicit mission conditions. Its transfer operation's automatic capture plan is disabled; actual Minmus SOI entry is observed before planning capture.

Open integration work includes downstream force ownership, callback determinism, complete checkpoint state, controller export/import and mod-qualified frame/warp transitions. The [force observation boundary](force-observation.md) records the recommended read-only component seam and the known MFI, FAR and Principia hook conflicts; the adapter and installed compatibility tests remain future work. Inspect other boundaries when the next implementation decision needs them, rather than disassembling the entire game speculatively.
