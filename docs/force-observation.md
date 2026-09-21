# Force observation boundary

Continuum does not currently capture native aggregate force. The flight shadow receipt records rigidbody pose, velocity, angular state, inertia and limited frame context; its force columns are synthetic zeros for a transport check. This document recommends a future observation seam. It does not describe an implemented adapter or qualify compatibility with a flight integrator, aerodynamic model or gravity replacement.

## Recommended seam

Put KSP-specific readers behind a Continuum-owned, read-only force provider. A provider would return a bounded immutable batch with its identity and version, capture time, vessel and topology identity, physics epoch, reference-frame discriminator and per-part observations. Keep Unity and KSP objects on the main thread.

The first experimental provider should copy the public `Part.force`, `Part.torque` and `Part.forces` census at `TimingManager.TimingStage.FashionablyLate`, before the flight-integrator stage. [Principia uses this named stage](https://github.com/mockingbirdnest/Principia/blob/dcf1fb949d792be966e5a7a162a1073ddfa1f9c1/ksp_plugin_adapter/ksp_plugin_adapter.cs) to collect nonconservative part forces before the integrator clears them. The owned KSP 1.12.5 assembly identified in [the integration map](integration-map.md) exposes those fields, `Part.AddForce`, `Part.AddForceAtPosition`, `Part.AddTorque`, and the named timing stages.

Each batch should report availability separately for:

- the `Part` force and torque census;
- stock aerodynamics;
- gravity;
- contacts and constraints;
- direct `Rigidbody` writes.

The initial provider may report only the part census as captured. An unavailable component has no numeric substitute. A batch becomes stale when its vessel, topology generation, physics epoch or frame context no longer matches the physical snapshot it accompanies.

## Why the census is incomplete

Principia has a separate flight-integrator-stage path that reconstructs stock lift and drag because stock aerodynamics does not publish through `Part.AddForce`. PhysX contacts and constraints, direct `Rigidbody` calls, provider-internal terms and later writes are also outside the proposed census. Post-step velocity change cannot recover their ownership: contacts, constraints, frame motion and provider corrections are mixed together.

The resulting receipt is a component observation. It is not total force, a stock trajectory prediction, a deterministic replay input or proof that every force provider was observed.

## Existing hook owners

[ModularFlightIntegrator (MFI)](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/ModularFlightIntegrator.cs) replaces the stock integrator wrapper through its [manager](https://github.com/sarbian/ModularFlightIntegrator/blob/03f07cd6e498ed003f84ee97ce976430e6447256/MFIManager.cs). Its principal integration and aerodynamics registrations are single static delegate slots that refuse a second owner, and the inspected API has no unregister operation. Continuum must not occupy `Integrate` or `UpdateAerodynamics` merely to observe forces. A future owner adapter must check registration success and abstain when another owner already holds a slot.

[FAR registers MFI's aerodynamic override](https://github.com/dkavolis/Ferram-Aerospace-Research/blob/c769cbd3d23ec9fd22538c1eb8b57fd2fcd025c6/FerramAerospaceResearch/FARAeroComponents/ModularFlightIntegratorRegisterer.cs). In the pinned source, FAR accumulates local aerodynamic force and torque, converts them to world space, then normally publishes them with [`Part.AddForce` and `Part.AddTorque`](https://github.com/dkavolis/Ferram-Aerospace-Research/blob/c769cbd3d23ec9fd22538c1eb8b57fd2fcd025c6/FerramAerospaceResearch/FARAeroComponents/FARAeroPartModule.cs). This makes FAR deposits plausible census inputs. Water, fallback and version-specific paths still need installed tests; Continuum must leave FAR's MFI slot untouched.

Principia reads the same part census, then performs separate gravity and motion work. A Continuum callback could copy that raw channel without mutation, but same-stage coexistence, epoch linkage and installed ordering remain unqualified. It must not disable gravity, correct positions, publish vessel state or describe the census as Principia's total force.

No inspected local instance contained MFI, FAR or Principia. Source evidence establishes candidate APIs and conflicts, not installed compatibility.

## Qualification order

1. In a disposable stock instance, apply known `Part.AddForce`, `AddForceAtPosition` and `AddTorque` sentinels to an isolated body. Verify the copied entries before the integrator clears them, bounds, epoch linkage and callback removal. Keep stock aero unavailable.
2. Pin FAR and MFI releases. Verify occupied MFI slots, then compare FAR's source-visible published force and torque with the part census during bounded atmospheric windows. Test special paths separately.
3. Pin Principia. Verify that both observers see the same sentinel census while Continuum performs no ownership calls. Exercise packing, origin shifts and teardown. Trajectory agreement is not force-completeness evidence.

Qualify FAR and Principia separately before any combined claim. If provider identity, callback placement or lifecycle cleanup cannot be established, the adapter must refuse active capture rather than emit an ambiguous batch.
