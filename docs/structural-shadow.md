# Structural shadow experiment

Continuum's flight shadow now retains the public Unity joint graph alongside its first accepted rigid-body snapshot. Each mapped link records its two captured body IDs, concrete joint type, anchors, axes, break thresholds, collision and preprocessing flags, and mass scales. Joints that do not connect two captured dynamic bodies are counted as unmapped instead of disappearing or being assigned guessed endpoints.

For `ConfigurableJoint` links, the payload also retains every public motion, limit, spring, drive, target, projection, coordinate-mode and body-swap setting needed to describe the public constraint configuration. Those settings participate in the topology signature, so a change rejects pending shadow work rather than silently combining epochs.

This is a reconstructable public constraint configuration, not structural replay. It does not contain collider geometry; contacts; Rigidbody solver overrides beyond the existing mass/inertia state; PhysX warm-start or solver-order state; private `PartJoint` setup fields; complete force ownership; or later joint mutations after the first accepted batch. Stock KSP remains authoritative and the shadow path writes nothing to the vessel.

## First stock response experiment

The next native experiment is deliberately smaller than a general vessel replay. It admits only a settled vacuum craft whose capture proves exactly two dynamic bodies, one mapped configurable joint, no unmapped joints, no contacts, fixed normal-rate steps, and stable topology and reference-frame generations. A separately armed one-shot applies equal and opposite center-of-mass impulses along the captured joint axis. A sham run and three fresh repeated runs retain consecutive full body states and relative axial, off-axis, and angular motion.

The preregistered candidate fits a two-state recurrence to the first 40 axial samples and recursively predicts the next 80 without refitting. Each run must remain eligible and pass normalized held-out displacement RMS at or below 5 percent, peak timing within one fixed step, and decay-rate error at or below 10 percent. A shuffled or wrong-parameter negative control must be materially worse, and the signal must exceed sham drift. Any callback ambiguity, contact, missing epoch, config mutation, extra constraint, frame transition, or excitation imbalance invalidates the entire receipt.

Passing this experiment qualifies a fitted single-mode predictor for that frozen stock regime. It does not qualify a physical constraint solver, deterministic replay, PhysX equivalence, live takeover, or a speedup. Those claims require prediction from independently supplied physical parameters on untouched traces, broader mass/impulse/timestep/joint holdouts, and the existing atomic authority-handoff gates.

## First oracle

The first decisive workload is a purpose-built two-body, one-joint craft in a settled vacuum orbit with engines, SAS and RCS disabled. After the lifecycle trace qualifies the installed callback order, a separately armed experiment can apply a recorded equal-and-opposite impulse and retain about 120 fixed steps. Relative body coordinates remove common floating-origin translation and become the oracle for an offline solver.

`StructuralResponse.FitAndPredict` implements the smallest candidate: a deterministic two-state recurrence fitted on an initial window and recursively evaluated on held-out samples. It rejects missing context, duplicate epochs, nonfinite samples, unidentifiable motion and divergent predictions. A portable damped-oscillator fixture proves the fitting and held-out path; it does not claim that KSP joint motion follows one mode.

Before a native run, freeze the workload and acceptance envelope. The initial research target is normalized held-out displacement RMS at or below 5%, peak timing within one fixed step, decay-rate error at or below 10%, and at least 95% qualified endpoint coverage across three independent captures. Failure is useful evidence for a richer modal, nonlinear or direct constraint strategy.

## Remaining bridge

`StructuralExperimentReport` now defines the bounded native receipt before the vessel-writing bridge exists. It separates receipt validity, run eligibility and scientific qualification; preserves both bodies' raw center-of-mass pose and velocity at every consecutive epoch; binds derived axial motion back to those raw states; records requested impulse rather than claiming observed application; and requires stable topology, frame, origin and timestep context. Contact observation is explicit: a complete capture may remain `provisional-contact-unobserved`, while only `observed-none` can be eligible. Capture code cannot assert that the candidate passed its scientific gates.

The vessel-writing bridge remains gated on installed lifecycle evidence. KSP callback names do not establish whether an injection is before the PhysX solve or an observation is after it. The next native qualification must identify a pre-solver injection callback and post-solver observation callback from behavior, then retain that lifecycle receipt with the structural run. Until that evidence exists, Continuum must not arm the impulse path.

The direct `PhysicsFixedUpdate` bracket and its behavioral receipt now define that seam in source, but still require an installed run. Qualification and the future structural runner share one `PhysicsBoundaryHooks` owner, so they cannot install competing brackets or diverge in adjacency, audit, foreign-loop preservation, or cleanup behavior.

The runner remains gated on four additional receipt changes: an exactly-once impulse command witness; evidence-backed admission of exactly two dynamic bodies and one enabled mapped `ConfigurableJoint` with observed contact state; a raw baseline event bound to the injection boundary; and fixed-timestep equality between the installed boundary qualification and structural run. A native capture must not report `complete` until those fields are validated. Sham and repeated impulse runs will remain separate captures and a later aggregate receipt will own scientific comparison.

Once qualified, the bridge will own the bounded impulse and main-thread trace at those exact seams. It must use owned collision sentinels, record exact excitation provenance, body and joint identities, topology and frame generations, callback cleanup, and unavailable force channels. It must invalidate the whole window on contact, topology, origin, timestep or eligibility change. Solver timing may be reported separately, but no stock speedup follows until the live profiler attributes comparable stock work.
