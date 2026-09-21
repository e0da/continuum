#!/usr/bin/env python3
"""Assess an external Gimbal coast implementation and bounded event adversaries."""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

POSITION_TOLERANCE_M = 0.001
VELOCITY_TOLERANCE_MPS = 0.000001
HERE = Path(__file__).resolve().parent

class FixtureError(Exception):
    pass

def reference_case(name, mu, semimajor, eccentricity, anomaly, revolutions=0):
    """Known eccentric anomaly gives state and elapsed time without solving Kepler's equation."""
    n = math.sqrt(mu / semimajor ** 3)
    root = math.sqrt(1 - eccentricity ** 2)
    denominator = 1 - eccentricity * math.cos(anomaly)
    position = [semimajor * (math.cos(anomaly) - eccentricity),
                semimajor * root * math.sin(anomaly), 0.0]
    velocity = [-semimajor * n * math.sin(anomaly) / denominator,
                semimajor * n * root * math.cos(anomaly) / denominator, 0.0]
    return {"name": name, "mu": mu, "semimajor": semimajor, "eccentricity": eccentricity,
            "dt": (anomaly - eccentricity * math.sin(anomaly) + revolutions * 2 * math.pi) / n,
            "initial": [semimajor * (1 - eccentricity), 0.0, 0.0, 0.0,
                        math.sqrt(mu * (1 + eccentricity) / (semimajor * (1 - eccentricity))), 0.0],
            "expected": position + velocity}

def assess_case(case, actual):
    if len(actual) != 6 or not all(math.isfinite(value) for value in actual):
        raise FixtureError("Donor returned a malformed or nonfinite state")
    position_error = math.dist(actual[:3], case["expected"][:3])
    velocity_error = math.dist(actual[3:], case["expected"][3:])
    return {**case, "actual": actual, "positionErrorM": position_error,
            "velocityErrorMps": velocity_error,
            "passed": position_error <= POSITION_TOLERANCE_M and velocity_error <= VELOCITY_TOLERANCE_MPS}

def event_arguments(position, velocity, radius, duration):
    if len(position) != 3 or len(velocity) != 3:
        raise FixtureError("Event vectors must have three components")
    if not all(math.isfinite(x) for x in (*position, *velocity, radius, duration)) or radius <= 0 or duration <= 0:
        raise FixtureError("Event fixture requires finite vectors and positive radius/duration")


def sphere_events(position, velocity, radius, duration):
    """Roots for one constant-velocity trajectory against a stationary sphere, on [0,duration]."""
    event_arguments(position, velocity, radius, duration)
    a = sum(x * x for x in velocity)
    b = sum(x * y for x, y in zip(position, velocity))
    c = sum(x * x for x in position) - radius * radius
    discriminant = b * b - a * c
    if not all(math.isfinite(x) for x in (a, b, c, discriminant)):
        raise FixtureError("Event arithmetic exceeds finite range")
    if a == 0:
        if c == 0:
            raise FixtureError("Stationary boundary has no isolated event")
        return []
    if discriminant < 0:
        return []
    if discriminant == 0:
        roots = [-b / a]
    else:
        q = -b - math.copysign(math.sqrt(discriminant), b)
        roots = [q / a, c / q]
    return sorted(set(t for t in roots if 0 <= t <= duration))

def endpoint_crossing(position, velocity, radius, duration):
    event_arguments(position, velocity, radius, duration)
    start = sum(x * x for x in position) - radius * radius
    end = sum((x + v * duration) ** 2 for x, v in zip(position, velocity)) - radius * radius
    return start == 0 or end == 0 or (start < 0) != (end < 0)

def write_report(path, report):
    text = json.dumps(report, indent=2, sort_keys=True, allow_nan=False) + "\n"
    with Path(path).open("x", encoding="utf-8") as stream:
        stream.write(text)


def donor_identity(root, expected_revision):
    if not re.fullmatch(r"[0-9a-f]{40}", expected_revision):
        raise FixtureError("Expected revision must be a full lowercase Git SHA")
    def git(*args):
        result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, timeout=10)
        if result.returncode:
            raise FixtureError("Gimbal source must be an accessible Git checkout")
        return result.stdout.strip()
    revision = git("rev-parse", "HEAD")
    if revision != expected_revision:
        raise FixtureError("Gimbal revision mismatch: " + revision)
    if git("status", "--porcelain", "--untracked-files=all"):
        raise FixtureError("Gimbal checkout must be clean, including untracked files")
    for relative in ("crates/orbit/Cargo.toml", "crates/orbit/src/lib.rs", "Cargo.lock"):
        if not (root / relative).is_file():
            raise FixtureError("Gimbal source is missing " + relative)
    return {"revision": revision, "crate": "gimbal-orbit", "sourceSha256": digest(root / "crates/orbit/src/lib.rs"),
            "sourceLockSha256": digest(root / "Cargo.lock")}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def orbital_cases():
    mu = 3.986004418e14
    return [reference_case(name, mu, 1e7, e, anomaly, revolutions) for name, e, anomaly, revolutions in (
        ("circular-quarter", 0, math.pi / 2, 0),
        ("circular-half", 0, math.pi, 0),
        ("circular-backward", 0, -math.pi / 2, 0),
        ("circular-16-turns", 0, math.pi / 2, 16),
        ("eccentric-outbound", .6, math.pi / 2, 0),
        ("eccentric-apocentre", .6, math.pi, 0),
        ("eccentric-inbound", .6, 3 * math.pi / 2, 0),
        ("eccentric-backward", .6, -math.pi / 2, 0),
        ("eccentric-8-turns", .6, math.pi / 2, 8),
        ("high-eccentricity-pericentre", .95, .01, 0),
        ("high-eccentricity-outbound", .95, math.pi / 2, 0),
        ("high-eccentricity-apocentre", .95, math.pi, 0),
    )]


def event_report():
    result = []
    for name, position, velocity, expected in (
        ("double-crossing", (-2, 0, 0), (4, 0, 0), [.25, .75]),
        ("grazing", (-2, 1, 0), (4, 0, 0), [.5]),
        ("miss", (-2, 2, 0), (4, 0, 0), []),
        ("initial-overlap-exit", (0, 0, 0), (2, 0, 0), [.5]),
    ):
        actual = sphere_events(position, velocity, 1, 1)
        result.append({"name": name, "position": position, "velocity": velocity, "radius": 1, "duration": 1,
                       "expectedTimes": expected, "actualTimes": actual,
                       "endpointSignChangeDetected": endpoint_crossing(position, velocity, 1, 1),
                       "passed": actual == expected})
    return {"trajectoryContract": "Constant relative velocity; stationary sphere; inclusive isolated roots in [0,1] s. Conditioned synthetic fixtures only, not certified interval arithmetic or a production event detector.",
            "cases": result, "endpointBaselineExpectedMisses": ["double-crossing", "grazing"],
            "passed": all(item["passed"] for item in result) and not result[0]["endpointSignChangeDetected"] and not result[1]["endpointSignChangeDetected"]}


def run_adapter(root, cargo, cases):
    with tempfile.TemporaryDirectory(prefix="continuum-orbit-fixture-") as directory:
        project = Path(directory)
        (project / "src").mkdir()
        (project / "Cargo.toml").write_text('[package]\nname = "continuum-orbit-fixture"\nversion = "0.0.0"\nedition = "2024"\n\n[dependencies]\ngimbal-orbit = { path = ' + json.dumps(str(root / "crates/orbit")) + ' }\n')
        shutil.copyfile(root / "Cargo.lock", project / "Cargo.lock")
        shutil.copyfile(HERE / "adapter.rs", project / "src/main.rs")
        environment = dict(os.environ, CARGO_TARGET_DIR=str(project / "target"), RUSTUP_AUTO_INSTALL="0")
        rustc = cargo.parent / "rustc"
        if rustc.is_file():
            environment["RUSTC"] = str(rustc)
        started = time.perf_counter()
        build = subprocess.run([str(cargo), "build", "--release", "--offline", "--manifest-path", str(project / "Cargo.toml")],
                               capture_output=True, text=True, env=environment, timeout=180)
        build_seconds = time.perf_counter() - started
        if build.returncode:
            raise FixtureError("Offline adapter build failed; no dependency installation attempted:\n" + build.stderr[-6000:])
        payload = "".join(",".join([str(i)] + [repr(x) for x in [case["mu"], case["dt"], *case["initial"]]]) + "\n" for i, case in enumerate(cases))
        started = time.perf_counter()
        process = subprocess.run([str(project / "target/release/continuum-orbit-fixture")], input=payload,
                                 capture_output=True, text=True, timeout=30)
        elapsed = time.perf_counter() - started
        if process.returncode:
            raise FixtureError("Adapter process failed")
        rows = process.stdout.splitlines()
        if len(rows) != len(cases):
            raise FixtureError("Adapter returned the wrong number of cases")
        results = []
        for index, (line, case) in enumerate(zip(rows, cases)):
            fields = line.split(",")
            if fields[0] != str(index):
                raise FixtureError("Adapter returned an unexpected case identity")
            if fields[1:] == ["ERROR"]:
                results.append({**case, "passed": False, "error": "Donor coast panicked or returned a nonfinite state"})
            elif len(fields) == 8 and fields[1] == "OK":
                results.append(assess_case(case, [float(x) for x in fields[2:]]))
            else:
                raise FixtureError("Malformed adapter response")
        version = subprocess.run([str(cargo), "--version"], capture_output=True, text=True, timeout=10)
        if version.returncode:
            raise FixtureError("Cannot identify Cargo version")
        compiler = subprocess.run([environment.get("RUSTC", "rustc"), "--version"], capture_output=True, text=True, timeout=10)
        if compiler.returncode:
            raise FixtureError("Cannot identify Rust compiler version")
        return results, {"cargoVersion": version.stdout.strip(), "adapterSha256": digest(HERE / "adapter.rs"),
                         "rustcVersion": compiler.stdout.strip(),
                         "resolvedLockSha256": digest(project / "Cargo.lock"), "offlineBuildWallSeconds": build_seconds,
                         "adapterProcessWallSeconds": elapsed,
                         "timingScope": "Build and process wall time observations; process includes startup and I/O, not a solver benchmark."}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gimbal-root", type=Path, required=True)
    parser.add_argument("--expected-revision", required=True)
    parser.add_argument("--cargo", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.output.exists() or args.output.is_symlink() or not args.output.parent.is_dir():
            raise FixtureError("Output must be a new file in an existing directory")
        root = args.gimbal_root.resolve()
        before = donor_identity(root, args.expected_revision)
        cargo = args.cargo or Path(shutil.which("cargo") or Path.home() / ".cargo/bin/cargo")
        if not cargo.is_file():
            raise FixtureError("Cargo is unavailable; supply an existing --cargo executable")
        results, execution = run_adapter(root, cargo, orbital_cases())
        after = donor_identity(root, args.expected_revision)
        if before != after:
            raise FixtureError("Donor source changed during the fixture")
        events = event_report()
        passed = all(case["passed"] for case in results) and events["passed"]
        report = {"schema": "ksp-continuum-orbit-fixture/v1", "passed": passed, "donor": before,
                  "donorUnchanged": True, "runnerSha256": digest(Path(__file__)), "execution": execution,
                  "orbital": {"oracle": "Cartesian ellipse at known eccentric anomaly and analytic time of flight; no Kepler root solved by oracle.",
                              "positionToleranceM": POSITION_TOLERANCE_M, "velocityToleranceMps": VELOCITY_TOLERANCE_MPS,
                              "cases": results}, "events": events,
                  "limits": ["Point-mass two-body ellipses only; no thrust, collisions, atmosphere, mass flow, parabolic/hyperbolic or N-body qualification.",
                             "Event tests use an independent constant-velocity fixture, not the curved donor orbit.",
                             "No rigorous floating-point error certificate, millennial claim, deterministic replay proof, or speedup claim."]}
        write_report(args.output, report)
        print(("PASS" if passed else "FAIL") + " orbital/event fixture; report " + str(args.output))
        return 0 if passed else 2
    except (FixtureError, OSError, ValueError, subprocess.SubprocessError) as error:
        print("orbit-fixture: " + str(error), file=sys.stderr)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
