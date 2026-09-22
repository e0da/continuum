# KSP Continuum engineering rules

## Languages and runtimes

- Use C# for Unity, KSP integration, managed simulation code, and related domain tooling.
- Use Rust for native systems code, compute kernels, command-line tooling, build orchestration, and test harnesses.
- Use Elixir only when the work materially benefits from OTP supervision, durable processes, or distributed coordination.
- Do not add Python, shell scripts, JavaScript, or TypeScript.
- Do not add C or C++ unless an unavoidable external ABI or upstream library requires it. Keep that boundary minimal, mechanically tested, and return immediately to safe Rust or managed C#.
- Jolt-specific C++ must remain isolated from project-owned simulation policy and orchestration. Prefer a narrow stable ABI with Rust ownership around it when live native integration begins.

Ease of hand-authoring is not a language-selection criterion. Agents perform the implementation, so choose for correctness, safety, runtime fit, performance, and long-term ownership.

## Delivery

Use git and gh, never Graphite. Work on reviewable branches; `main` is integrated. Keep independent work in separate worktrees.

Behavioral experiments must remain reproducible and deterministic where claimed. Preserve numerical gates while migrating implementations; passing a replacement test harness is not evidence that changed physics are equivalent.

KSP instances, saves, game files, local paths, credentials, generated reports, and downloaded dependencies are runtime state and must not be committed.
