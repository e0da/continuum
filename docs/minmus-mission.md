# Minmus integration mission

The optional mission addon turns an actual launch-to-landing attempt into a Continuum integration workload. It creates a unique sandbox, launches the stock Kerbal X with crew, then records controls while MechJeb attempts ascent, transfer, correction, capture and landing. A neutral input replay is a separate preflight check when no foreign control owner is present; installed MechJeb may already own callbacks on the command pod. Skipping that check is not replay qualification. It never grants fuel, moves the vessel into orbit, or replaces existing saves.

Build against your installed MechJeb 2.15.3.0 package (assembly version 2.15.0.0, file version 2.15.3.0):

```sh
export MECHJEB_DLL="/path/to/MechJeb2/Plugins/MechJeb2.dll"
dotnet build src/KspContinuum.Mission -c Release
python3 scripts/package.py --mission
```

The optional addon targets .NET Framework 4.8 to match that MechJeb release. The base plugin remains net472. Package output includes only our DLLs, docs and example inputs. Install dependencies and the local experiment with CKAN in an exclusively owned disposable instance; do not copy game libraries into the package.

Launch that instance with `--continuum-minmus`. Use a visible window to watch; use `-batchmode -nographics` for unattended headless trials. You can watch rendered trials. Headless trials reduce rendering overhead, but the two modes are not qualified as control-equivalent: mission commands and phase transitions currently run in Unity Update. Neither mode establishes deterministic whole-game replay; label performance measurements by mode. Avoid manual flight inputs while the mission controller owns the craft.

Attempts that pass the initial compatibility checks write `PluginData/mission-*` telemetry, phase events and a terminal receipt. Earlier failures remain in the game log. Startup refuses loaded `DarkMultiPlayer` or `LmpClient` before creating a save; this detects known client assemblies, not network connection state. Milestone saves use unique names inside the new mission sandbox and are linked by simulation time. They are useful inspection/recovery artifacts, not proof that all controller/worker state restores deterministically. Input recordings are linked separately and retain their own observed boundary/qualification limits.

Rendered runs request screenshots when they reach the launchpad and milestones, and remain open after success or failure. `screenshots.csv` distinguishes requested captures from confirmed PNG files. Use `--continuum-exit` to opt into automatic exit; batch runs exit automatically. Failed preflight runs count as mission attempts, even if the craft never leaves the pad.

Success requires the actual mission vessel on Minmus in LANDED state, with the intended command part, lander engine and gear surviving, zero throttle, surface speed below 0.2 m/s, and qualifying Update observations spanning at least 30 seconds of universal time at normal physics rate. An observed failure or clock rewind resets settling; the check does not prove conditions between observations. A predicted landing, completed maneuver node or encounter does not pass. Failures preserve a reason and, when possible, a milestone save.

The intended staging limit preserves the Poodle-powered upper landing assembly. Transfer and capture are separate: observe actual Minmus SOI entry before planning the insertion burn. Exact MechJeb APIs and runtime initialization are pinned experiment dependencies, not a general compatibility claim.

The flight is stock physics controlled by MechJeb and orchestrated/instrumented by Continuum. It does not prove a replacement physics engine, complete mission replay, unattended mission planning for arbitrary craft, or multimedia reconstruction. See the validation receipt for actual trial outcomes.

The first completed flight passed the declared intact-touchdown check, but the observer saw repeated flipping and the final image and saved rotation show it finished approximately sideways. Upright attitude and angular settling are not current acceptance requirements. See [validation](validation.md) before interpreting a passed mission as a clean landing.
