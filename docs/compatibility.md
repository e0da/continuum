# Community integration boundaries

Continuum runs synthetic physics scenes, observes vessel state and offers guarded analog input replay. The optional mission addon drives MechJeb in a dedicated sandbox. It neither replaces the flight integrator nor changes vessel bodies. No general mod compatibility has been qualified. Before a shadow solver or takeover, use established community hooks where their ownership and timing contracts fit.

## Existing integration owners

| Project | Existing contract | Consequence for future work |
| --- | --- | --- |
| [ModularFlightIntegrator](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularFlightIntegrator.cs) | Public overrides for integration, mass, aero and other passes; most accept one static delegate and refuse a second owner. Some thermal hooks are multicast. | Check registration success, register once, and avoid delegates retaining scene objects. The inspected API has no unregister method. A refused hook must leave the incumbent in control. |
| [KSP Community Fixes](https://github.com/KSPModdingLibs/KSPCommunityFixes/blob/bc2e03ad1d1b0dcdb1d7d91b8f0f2d3d21cdb63a/KSPCommunityFixes/Performance/FlightIntegratorPerf.cs) | Replaces several integrator passes and avoids redundant Rigidbody mass/CoM writes. | Measure against its enabled optimizations. An MFI base-method fallback may reach a patched method, so it is not necessarily stock behavior. |
| [KJR Continued](https://github.com/KSP-RO/Kerbal-Joint-Reinforcement-Continued/blob/b60bd0c2951bb27bbfd6131b93304d6ecd3e2f6b/KerbalJointReinforcement/KerbalJointReinforcement/KJRManager.cs) | Creates extra joints and handles vessel modification, rails, destruction, robotic lock and EVA construction events. | Proposed policy: abstain from aggregation with an unqualified joint owner. A partnership must specify who creates, removes and restores each constraint. |
| [HarmonyKSP](https://github.com/KSPModdingLibs/HarmonyKSP) | Community Harmony distribution; recommends the CKAN dependency `Harmony2`. | If managed patches become necessary, depend on the shared distribution rather than bundle a competing copy. Use a unique patch owner and explicit ordering. |

MFI's [manager](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/MFIManager.cs) disables the stock integrator wrapper. Do not introduce another independent replacement assuming both can execute. Its [ModularVesselPrecalculate](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularVesselPrecalculate.cs) class is internal: public-looking methods inside it are not an ordinary consumable public API. MFI also adjusts execution order for FAR/Principia interactions; an arbitrary Unity FixedUpdate callback does not establish equivalent ordering.

[Harmony patch ordering](https://harmony.pardeike.net/v2/articles/priorities.html) can coordinate managed patches, but it is not a solver ownership protocol. Its [native-method limitations](https://harmony.pardeike.net/v2/articles/patching-edgecases.html) also prevent treating it as a universal native Unity physics hook.

## Lifecycle contract before takeover

KSPCF's [ActiveRadiatorPerf](https://github.com/KSPModdingLibs/KSPCommunityFixes/blob/bc2e03ad1d1b0dcdb1d7d91b8f0f2d3d21cdb63a/KSPCommunityFixes/Performance/ActiveRadiatorPerf.cs) provides useful precedent: a per-vessel batch component registers at `TimingStage.FlightIntegrator`, invalidates caches on vessel modification, ends on vessel unload, and removes callbacks in `OnDestroy`. This is a pattern to evaluate for observation and shadow work, not proof that its timing captures every force.

Before controlling a vessel, define and verify:

- Force collection and state publication relative to existing aero, thermal, resource and control readers/writers, including FAR and Principia where supported.
- One motion owner per body and one owner per joint. Record which integration methods remain active; do not apply both stock and replacement forces.
- Topology generation changes at docking, separation, breakage, construction and robotics; reject asynchronous results from an older generation.
- Packing/unpacking, warp, floating-origin, scene and save/load transitions, including cache invalidation and callback removal.
- Explicit supported mod versions/configurations and a refusal path for unsupported combinations. Background simulation providers also need an event/interval contract before long warp can advance them.

Qualification should progress from stock, to each relevant mod individually, to declared combinations. Compare trajectories, momentum, contacts, lifecycle transitions and end-to-end cost. Record enabled patches and exact versions; do not add support libraries silently between benchmark runs. Inspect installed releases before relying on these source-snapshot observations. No new runtime dependency is required by the current harness.

## Multiplayer planning boundary

Multiplayer compatibility is a requirement, not a demonstrated feature. [DarkMultiPlayer v0.3.8.5](https://github.com/godarklight/DarkMultiPlayer/tree/v0.3.8.5) coordinates vessel updates through server-arbitrated locks and offers multiple shared/subspace warp modes. [LunaMultiplayer 0.29.2](https://github.com/LunaMultiplayer/LunaMultiplayer/releases/tag/0.29.2) also has control/update locks and subspaces; its release notes describe protections against network vessel snapshots racing topology changes. Read their [timewarp](https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Timewarp) and [lock](https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Lock-system) contracts before implementing an adapter.

Future worker jobs and recordings need session, authority, vessel and subspace identities alongside universal time and topology generation. Lock loss, subspace movement, docking/undocking and vessel-ID replacement invalidate pending results. Local histories in different subspaces cannot be merged into one authoritative order just by sorting their relative timestamps.

Initial shared-session policy is observational capture only, with no claim of a multiplayer-aware event history. Active replay requires an isolated single-player environment. Do not acquire locks, publish vessel state, rewind shared history or command multiplayer warp as part of a local replay. A future adapter must cooperate with the provider's authority and topology protocol rather than bypass it.

Active control conservatively refuses loaded client assemblies named `DarkMultiPlayer` or `LmpClient`, matching the pinned [DMP client project](https://github.com/godarklight/DarkMultiPlayer/blob/v0.3.8.5/Client/Client.csproj) and [LMP client project](https://github.com/LunaMultiplayer/LunaMultiplayer/blob/0.29.2/LmpClient/LmpClient.csproj). Presence does not imply connection; absence does not establish safety for renamed forks or other providers. This guard is not a multiplayer integration or compatibility test.
