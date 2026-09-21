# Aerodynamic behavioral qualification

Aerodynamic compatibility is a behavioral envelope, not exact reproduction of every stock timestep. A compatible strategy preserves mission capability, handling response, major events and performance distributions. It need not reproduce stock wobble, integration noise or frame-order artifacts.

Each workload freezes its craft, provider/mod fingerprint, save or checkpoint, controls and event policy. Run the stock or selected provider at least five times to measure native repeatability before setting candidate tolerances. A provisional acceptance band is:

```text
candidate error <= max(physical floor, 2 * provider repeatability spread)
```

Use paired median absolute deviation and p95 spread. Freeze the envelope before candidate evaluation. Keep complete attempts, trajectories and craft designs together in calibration or held-out sets.

## Stock-feel fixtures

### Rocket ascent

Start a small stock liquid rocket from a pinned launchpad state. Freeze SAS and replay scripted throttle and attitude controls. The first phase stops before staging so topology remains fixed.

Measure milestone times at 1, 10 and 20 km and Mach 1; velocity and attitude at milestones; apoapsis; peak dynamic pressure; propellant consumed; maximum angular rate; and integrated control effort. Initial physical floors are 0.5 seconds for milestone time, 2% apoapsis, 1% speed, 2 degrees attitude, 1% propellant and 3% peak dynamic pressure.

The rocket must follow the same class of gravity turn, remain controllable and retain an equivalent opportunity to reach orbit.

### Plane flight

Start a stock-style trainer from a settled runway checkpoint. Replay acceleration, rotation, climb, bank reversal, level hold, power-off glide, stall entry and recovery as separate fixed-topology phases.

Measure takeoff speed/time, climb rate, trim pitch/throttle, bank response and overshoot, sideslip, turn radius, glide ratio, stall onset/recovery and control effort. Initial floors are 3% takeoff speed, 5% climb/glide/turn metrics, 0.25 seconds or 5% response time, 3 degrees attitude and 5% stall speed.

Controls must have the expected sign and authority. The aircraft must trim, turn and glide without added sustained oscillation, and recover from the same stall class.

### Reentry

Start a capsule and heat shield from a pinned atmospheric-entry state. Use a ballistic no-control phase and a separate scripted-bank lifting phase.

Measure peak acceleration and heating proxy, altitude/time of peak load, downrange/crossrange, attitude and angular-rate envelope, parachute-safe event state and survival. Initial floors are 5% peak load/heating, 1 km event altitude, 2 seconds event time, 5% downrange and 3 degrees controlled attitude.

The capsule must retain the same stability, deceleration/heating order, parachute eligibility and survival class. Passive aerodynamics may not add unexplained energy.

## Qualification ladder

1. Validate finite one-step forces, frames, aggregation, symmetries, zero-flow behavior and density scaling.
2. Run a 1–2 second open-loop segment and compare linear and angular impulse.
3. Run a 10–60 second phase and bound attitude, velocity, energy and event timing throughout.
4. Run the complete workload and require the same outcome class plus an end-to-end runtime win.
5. Pass a held-out rocket, plane and reentry craft without retuning.
6. Inspect rendered representative runs and tie subjective differences to response lag, damping, overshoot, stability or control authority telemetry.

NaN, force spikes, nondeterminism, wrong control sign, silent provider mixing, unreported fallback, new loss of control, changed survival class, tail regression or slower end-to-end performance rejects active authority regardless of average error.

## Provider profiles

Profiles never pool labels or tolerances implicitly:

- `stock` uses stock atmosphere, body aerodynamics, lifting surfaces and integration.
- `stock-continuum-aero` retains stock game content and mission expectations while Continuum owns the selected aerodynamic strategy.
- `far` uses FAR geometry and force ownership.
- `ro` composes Realism Overhaul configuration, FAR aerodynamics, RealFuels propulsion/resources and RealHeat thermodynamics.
- `ro-rss` adds RSS/Kopernicus bodies and atmosphere.
- `ro-rss-principia` adds Principia trajectory and gravity ownership.

[Realism Overhaul's metadata](https://github.com/KSP-RO/RealismOverhaul/blob/25a536bf603fba8541172e73d81d706241754f19/RealismOverhaul.netkan#L10-L51) requires FAR, RealFuels and RealHeat and recommends RSS. RSS body definitions populate live KSP atmosphere data; strategies consume those live values rather than baking Kerbin or Earth curves. [RealHeat](https://github.com/KSP-RO/RealHeat/blob/7fd548d7f7e722d8fa5cc23952094cc575cfad30/Source/RealHeat.cs#L7-L51) remains a downstream thermal provider. [RealFuels](https://github.com/KSP-RO/RealFuels/blob/dc2891f3e920f2060fdbb8fd4284bfc61e949f71/Source/Engines/SolverRF.cs#L158-L193) remains the propulsion/resource provider. Principia remains an optional exclusive gravity/trajectory owner.

The compatibility ladder adds a FAR Kerbin Mach/angle-of-attack sweep and stall recovery; an RO/RSS sounding rocket through max-Q; ballistic and lifting Earth entries; a Principia coast-to-entry handoff; and topology changes from fairing separation, staging, deployment and damage. Each receipt records exact assemblies, versions, provider ownership and configuration hashes.

## Harness gaps

Continuum's input recorder already captures analog controls, stage notifications and coarse observations, but replay starts from current state, stops before discrete topology changes and does not restore a save. Existing observations also lack full attitude, angular velocity, atmosphere, heating, dynamic pressure, forces and resources.

Before candidate qualification, add an aerodynamic behavior telemetry receipt and a generic phase fixture. The fixture loads a pinned save/checkpoint, verifies craft and provider identity, waits for settled normal-rate physics and replays one analog phase. Repeated stock runs establish the envelope; candidate runs cannot influence it.
