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

## Native structural runner

`StructuralExperimentReport` v2 is the bounded receipt written by the native runner. It separates receipt validity, run eligibility and scientific qualification; preserves both bodies' raw center-of-mass pose and velocity at every consecutive epoch; and binds derived axial motion back to a full pre-physics baseline event. A complete impulse receipt carries an exactly-once command witness from that same owned callback, including both returned `AddForce` calls. A sham proves that neither call occurred. Every response sample retains both native body identities and an exact callback sequence.

Admission evidence binds the run to exactly two dynamic bodies and one enabled mapped joint with no unmapped joints. Its topology ID is recomputed from the native body and joint identities plus the complete validated `StructuralLink` payload. The frozen joint-transform rotation maps the link's local axis to the recorded world impulse axis. Eligible runs also bind one contact sentinel to each body, require a complete epoch window and zero observed contacts or joint breaks, and prove removal of the owned physics-boundary hooks. A complete capture may remain `provisional-contact-unobserved` only when all contact evidence is explicitly absent. Installed Unity, KSP and plugin identities, the lifecycle qualification receipt and its common fixed timestep all bind the capture. Capture code still cannot assert that the candidate passed its scientific gates.

The flight panel now exposes **Run structural sham on active vessel** and **Run structural impulse on active vessel**. Each click performs a fresh native-boundary qualification, releases the qualification hooks, then admits an unpacked active vessel with exactly two dynamic rigidbodies and one active `ConfigurableJoint`. The runner captures its baseline in the owned pre-physics callback, issues the exact float `AddForce` pair only for impulse mode, and records 120 post-physics samples.

The runner rechecks the vessel, joint payload, timestep, floating origin and hook integrity at every boundary. Any change invalidates the run without publishing partial samples. After the last sample it removes its PlayerLoop hooks, destroys and verifies both contact sentinels, validates the completed receipt, and writes `GameData/KspContinuum/PluginData/structural-experiment-*.json`.

The first live workload is deliberately narrow: use a settled, unpacked two-body craft at normal time rate. Sham and repeated impulse runs remain separate captures; a later aggregate will compare them scientifically. This runner measures stock structural response. It does not replace the stock joint solver or claim a speedup yet.
