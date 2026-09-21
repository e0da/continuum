#!/usr/bin/env python3
"""MODAL-ROCKET-001: bounded normal-mode reduction of a uniform rocket chain."""

import argparse
import hashlib
import json
import math
import platform
import sys
from pathlib import Path

SCHEMA = "ksp-continuum-modal-rocket/v1"
BODY_COUNT = 32
RETAINED_FLEX_MODES = 6
MASS_KG = 1.0
STIFFNESS_NPM = 400.0
DASHPOT_NS_PM = 0.4
STEP_SECONDS = 0.001
DURATION_SECONDS = 2.0
SAMPLE_STRIDE = 100

GATES = {
    "smooth": {
        "maxShapeRmsRelative": 0.08,
        "maxTipShapeErrorRelative": 0.12,
        "maxCenterOfMassErrorM": 1e-10,
    },
    "localizedImpulse": {
        "maxShapeRmsRelative": 0.08,
        "maxTipShapeErrorRelative": 0.12,
        "maxCenterOfMassErrorM": 1e-10,
    },
}


class ExperimentError(Exception):
    pass


def dot(left, right):
    return math.fsum(a * b for a, b in zip(left, right))


def modes(count=BODY_COUNT):
    """Return orthonormal exact eigenvectors and stiffness eigenvalues."""
    vectors = []
    values = []
    for mode in range(count):
        scale = math.sqrt(1.0 / count) if mode == 0 else math.sqrt(2.0 / count)
        vectors.append([
            scale * math.cos(mode * math.pi * (index + 0.5) / count)
            for index in range(count)
        ])
        values.append(4.0 * STIFFNESS_NPM * math.sin(mode * math.pi / (2.0 * count)) ** 2)
    return vectors, values


def chain_acceleration(position, velocity, force):
    count = len(position)
    acceleration = [value / MASS_KG for value in force]
    for index in range(count - 1):
        extension = position[index + 1] - position[index]
        relative_velocity = velocity[index + 1] - velocity[index]
        tension = STIFFNESS_NPM * extension + DASHPOT_NS_PM * relative_velocity
        acceleration[index] += tension / MASS_KG
        acceleration[index + 1] -= tension / MASS_KG
    return acceleration


def modal_acceleration(coordinates, rates, modal_force, eigenvalues):
    return [
        modal_force[index] / MASS_KG
        - eigenvalues[index] * coordinates[index] / MASS_KG
        - (DASHPOT_NS_PM / STIFFNESS_NPM) * eigenvalues[index] * rates[index] / MASS_KG
        for index in range(len(coordinates))
    ]


def verlet(position, velocity, acceleration, next_force, acceleration_function):
    dt = STEP_SECONDS
    next_position = [x + dt * v + 0.5 * dt * dt * a for x, v, a in zip(position, velocity, acceleration)]
    predicted_velocity = [v + dt * a for v, a in zip(velocity, acceleration)]
    next_acceleration = acceleration_function(next_position, predicted_velocity, next_force)
    next_velocity = [v + 0.5 * dt * (a + b) for v, a, b in zip(velocity, acceleration, next_acceleration)]
    # Re-evaluate velocity-dependent damping after the corrected velocity.
    next_acceleration = acceleration_function(next_position, next_velocity, next_force)
    return next_position, next_velocity, next_acceleration


def smooth_force(time_seconds, vectors):
    ramp = 0.5 * (1.0 - math.cos(math.pi * min(time_seconds / 0.4, 1.0)))
    thrust = [0.0] * BODY_COUNT
    thrust[0] = 2.0 * ramp
    gust_envelope = math.sin(math.pi * min(max((time_seconds - 0.6) / 0.8, 0.0), 1.0)) ** 2
    return [
        thrust[index] + 0.35 * gust_envelope * (vectors[1][index] + 0.4 * vectors[2][index])
        for index in range(BODY_COUNT)
    ]


def zero_force(_time_seconds, _vectors):
    return [0.0] * BODY_COUNT


def reconstruct(coordinates, vectors):
    return [math.fsum(q * vector[index] for q, vector in zip(coordinates, vectors)) for index in range(BODY_COUNT)]


def shape(values):
    mean = math.fsum(values) / len(values)
    return [value - mean for value in values]


def run_workload(name, all_vectors, eigenvalues):
    retained_vectors = all_vectors[:RETAINED_FLEX_MODES + 1]
    retained_values = eigenvalues[:RETAINED_FLEX_MODES + 1]
    full_position = [0.0] * BODY_COUNT
    full_velocity = [0.0] * BODY_COUNT
    if name == "localizedImpulse":
        full_velocity[0] = 1.0
        force_function = zero_force
    elif name == "smooth":
        force_function = smooth_force
    else:
        raise ExperimentError("unknown workload")

    reduced_position = [dot(vector, full_position) for vector in retained_vectors]
    reduced_velocity = [dot(vector, full_velocity) for vector in retained_vectors]
    initial_force = force_function(0.0, all_vectors)
    full_acceleration = chain_acceleration(full_position, full_velocity, initial_force)
    modal_force = [dot(vector, initial_force) for vector in retained_vectors]
    reduced_acceleration = modal_acceleration(reduced_position, reduced_velocity, modal_force, retained_values)

    maximum_rms_error = 0.0
    maximum_tip_error = 0.0
    maximum_reference_rms = 0.0
    maximum_reference_tip = 0.0
    maximum_com_error = 0.0
    samples = []
    steps = round(DURATION_SECONDS / STEP_SECONDS)
    for step in range(steps + 1):
        reduced_physical = reconstruct(reduced_position, retained_vectors)
        full_shape = shape(full_position)
        reduced_shape = shape(reduced_physical)
        errors = [candidate - reference for candidate, reference in zip(reduced_shape, full_shape)]
        rms_error = math.sqrt(dot(errors, errors) / BODY_COUNT)
        reference_rms = math.sqrt(dot(full_shape, full_shape) / BODY_COUNT)
        tip_error = abs(errors[-1])
        reference_tip = abs(full_shape[-1])
        com_error = abs(math.fsum(reduced_physical) / BODY_COUNT - math.fsum(full_position) / BODY_COUNT)
        maximum_rms_error = max(maximum_rms_error, rms_error)
        maximum_tip_error = max(maximum_tip_error, tip_error)
        maximum_reference_rms = max(maximum_reference_rms, reference_rms)
        maximum_reference_tip = max(maximum_reference_tip, reference_tip)
        maximum_com_error = max(maximum_com_error, com_error)
        if step % SAMPLE_STRIDE == 0:
            samples.append({
                "step": step,
                "timeSeconds": step * STEP_SECONDS,
                "fullCenterOfMassM": math.fsum(full_position) / BODY_COUNT,
                "reducedCenterOfMassM": math.fsum(reduced_physical) / BODY_COUNT,
                "fullTipShapeM": full_shape[-1],
                "reducedTipShapeM": reduced_shape[-1],
                "shapeRmsErrorM": rms_error,
            })
        if step == steps:
            break
        next_time = (step + 1) * STEP_SECONDS
        next_force = force_function(next_time, all_vectors)
        full_position, full_velocity, full_acceleration = verlet(
            full_position, full_velocity, full_acceleration, next_force, chain_acceleration)
        next_modal_force = [dot(vector, next_force) for vector in retained_vectors]
        reduced_position, reduced_velocity, reduced_acceleration = verlet(
            reduced_position, reduced_velocity, reduced_acceleration, next_modal_force,
            lambda q, qdot, force: modal_acceleration(q, qdot, force, retained_values))

    rms_relative = maximum_rms_error / max(maximum_reference_rms, 1e-15)
    tip_relative = maximum_tip_error / max(maximum_reference_tip, 1e-15)
    metrics = {
        "maxShapeRmsErrorM": maximum_rms_error,
        "maxReferenceShapeRmsM": maximum_reference_rms,
        "maxShapeRmsRelative": rms_relative,
        "maxTipShapeErrorM": maximum_tip_error,
        "maxReferenceTipShapeM": maximum_reference_tip,
        "maxTipShapeErrorRelative": tip_relative,
        "maxCenterOfMassErrorM": maximum_com_error,
    }
    gates = GATES[name]
    checks = {key: metrics[key] <= limit for key, limit in gates.items()}
    return {
        "name": name,
        "expectedToQualify": name == "smooth",
        "qualified": all(checks.values()),
        "metrics": metrics,
        "gates": gates,
        "checks": checks,
        "samples": samples,
    }


def build_report():
    vectors, eigenvalues = modes()
    orthonormal_error = max(
        abs(dot(left, right) - (1.0 if i == j else 0.0))
        for i, left in enumerate(vectors) for j, right in enumerate(vectors)
    )
    workloads = [run_workload("smooth", vectors, eigenvalues), run_workload("localizedImpulse", vectors, eigenvalues)]
    experiment_qualified = workloads[0]["qualified"] and not workloads[1]["qualified"]
    source = Path(__file__).read_bytes()
    return {
        "schema": SCHEMA,
        "experiment": "MODAL-ROCKET-001",
        "qualified": experiment_qualified,
        "interpretation": "The smooth workload passes and the localized impulse remains a retained failure.",
        "productionPhysicsQualified": False,
        "stockBehaviorQualified": False,
        "performanceWinClaimed": False,
        "deterministicWithinPythonBuild": True,
        "model": {
            "bodyCount": BODY_COUNT,
            "massPerBodyKg": MASS_KG,
            "springStiffnessNPerM": STIFFNESS_NPM,
            "dashpotNsPerM": DASHPOT_NS_PM,
            "boundary": "free-free one-dimensional uniform nearest-neighbor chain",
            "fullStateScalars": 2 * BODY_COUNT,
            "retainedRigidModes": 1,
            "retainedFlexibleModes": RETAINED_FLEX_MODES,
            "reducedStateScalars": 2 * (RETAINED_FLEX_MODES + 1),
            "modeConstruction": "exact discrete cosine eigenvectors",
            "maxOrthonormalityError": orthonormal_error,
        },
        "schedule": {
            "stepSeconds": STEP_SECONDS,
            "durationSeconds": DURATION_SECONDS,
            "stepsPerWorkload": round(DURATION_SECONDS / STEP_SECONDS),
            "sampleStride": SAMPLE_STRIDE,
        },
        "bounds": {
            "fixedWorkloadCount": 2,
            "maximumBodies": BODY_COUNT,
            "maximumFlexibleModes": RETAINED_FLEX_MODES,
            "maximumStepsPerWorkload": round(DURATION_SECONDS / STEP_SECONDS),
        },
        "workloads": workloads,
        "environment": {
            "python": platform.python_version(),
            "implementation": platform.python_implementation(),
        },
        "sourceSha256": hashlib.sha256(source).hexdigest(),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New JSON file; parent must exist")
    arguments = parser.parse_args(argv)
    try:
        if not arguments.output.parent.is_dir():
            raise ExperimentError("output parent does not exist")
        report = build_report()
        payload = json.dumps(report, indent=2, sort_keys=True, allow_nan=False) + "\n"
        with arguments.output.open("x", encoding="utf-8") as stream:
            stream.write(payload)
        print("Wrote {} to {}".format(report["experiment"], arguments.output))
        return 0 if report["qualified"] else 2
    except (ExperimentError, FileExistsError, OSError, ValueError) as error:
        print("modal-rocket: {}".format(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
