# Coasting radius events

`CoastingEventScheduler.FindFirst` discovers the first inward or outward crossing of a body-centered radius within a
bounded time horizon. For each admitted elliptic two-body seed it derives semi-major axis, eccentricity, mean motion,
and the requested crossing's eccentric anomaly. It selects the earliest forward occurrence analytically, then brackets
and refines that body alone through `SampleBodyAt`. Calling `AdvanceTo(event.TimeSeconds)` stops engine time exactly at
the predicted boundary. Presentation publications never become event or integration inputs.

The scheduler admits finite bound elliptic seeds with nonzero eccentricity and a requested radius strictly between
periapsis and apoapsis. Eccentricity below `1e-8`, circular, parabolic, hyperbolic, and boundary-tangent cases abstain.
Search start is bounded to one million revolutions from the immutable seed epoch, and tolerance must advance
representable time. Refinement is limited to 32 bracket expansions and 64 bisections. This is a useful
surface-altitude or configured radial-shell boundary, not SOI,
terrain, atmosphere, collision, closest-approach, or arbitrary root detection.

The analytic fixture starts at periapsis on a Kerbin ellipse with the qualification station's approximate semi-major
axis and eccentricity. Crossing the semi-major-axis radius occurs at eccentric anomaly pi/2, so the expected time is
known independently. Tests vary work batch sizes and presentation cadences; all must refine the same event time within
`2e-7` seconds and radius within 2 mm. A 3.3-period horizon containing four outward crossings must still select the
first. The same test executable exercises 1, 64, and 512 independent
bodies and prints diagnostic elapsed time without imposing a machine-specific performance gate.
