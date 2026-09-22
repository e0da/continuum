# Structural shadow experiment

Continuum's flight shadow now retains the public Unity joint graph alongside its first accepted rigid-body snapshot. Each mapped link records its two captured body IDs, concrete joint type, anchors, axes, break thresholds, collision and preprocessing flags, and mass scales. Joints that do not connect two captured dynamic bodies are counted as unmapped instead of disappearing or being assigned guessed endpoints.

This is topology and inventory evidence, not a reconstructable constraint payload or structural replay. It does not contain configurable-joint motions, limits, springs, drives and targets; collider geometry; contacts; PhysX warm-start state; private `PartJoint` setup fields; complete force ownership; or later joint mutations. Stock KSP remains authoritative and the shadow path writes nothing to the vessel.

## First oracle

The first decisive workload is a purpose-built two-body, one-joint craft in a settled vacuum orbit with engines, SAS and RCS disabled. After the lifecycle trace qualifies the installed callback order, a separately armed experiment can apply a recorded equal-and-opposite impulse and retain about 120 fixed steps. Relative body coordinates remove common floating-origin translation and become the oracle for an offline solver.

`StructuralResponse.FitAndPredict` implements the smallest candidate: a deterministic two-state recurrence fitted on an initial window and recursively evaluated on held-out samples. It rejects missing context, duplicate epochs, nonfinite samples, unidentifiable motion and divergent predictions. A portable damped-oscillator fixture proves the fitting and held-out path; it does not claim that KSP joint motion follows one mode.

Before a native run, freeze the workload and acceptance envelope. The initial research target is normalized held-out displacement RMS at or below 5%, peak timing within one fixed step, decay-rate error at or below 10%, and at least 95% qualified endpoint coverage across three independent captures. Failure is useful evidence for a richer modal, nonlinear or direct constraint strategy.

## Remaining bridge

The next implementation increment owns the bounded impulse and main-thread trace at the lifecycle-qualified seam. It must record exact excitation provenance, body and joint identities, topology and frame generations, callback cleanup, and unavailable force channels. It must invalidate the whole window on a topology, origin, timestep or eligibility change. Solver timing may be reported separately, but no stock speedup follows until the live profiler attributes comparable stock work.
