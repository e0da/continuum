#!/usr/bin/env python3
"""Bounded, isolated Plummer-force FFT/CIC candidate against direct pairs."""
import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import platform
import random
import sys
import time

import numpy as np

LENGTH = 8.0
SOFTENING = 0.6
GRIDS = (17, 33, 65)
MAX_BODIES = 128
RMS_LIMIT = 0.05
WORST_LIMIT = 0.15
SELF_LIMIT = 1e-10
MOMENTUM_LIMIT = 1e-12


def capture(positions, masses, epsilon):
    if not 1 <= len(positions) <= MAX_BODIES or len(masses) != len(positions):
        raise ValueError('Use 1 through 128 bodies with matching masses.')
    if not math.isfinite(epsilon) or not .01 <= epsilon <= LENGTH:
        raise ValueError('Softening must be finite and within [0.01, 8].')
    p, m = np.array(positions, dtype=np.float64, copy=True), np.array(masses, dtype=np.float64, copy=True)
    if p.shape != (len(m), 3) or m.shape != (len(p),):
        raise ValueError('Positions must be triples and masses scalars.')
    if not np.isfinite(p).all() or not np.isfinite(m).all() or np.any(p < 0) or np.any(p > LENGTH):
        raise ValueError('Finite positions must lie in the closed [0,8]^3 box.')
    if np.any(m < 1e-6) or np.any(m > 1e6):
        raise ValueError('Masses must lie within [1e-6,1e6].')
    return p, m


def direct(positions, masses, epsilon):
    start = time.perf_counter()
    p, m = capture(positions, masses, epsilon)
    captured = time.perf_counter()
    result = np.zeros_like(p)
    # Independent pair computation, without the grid-kernel construction or FFT.
    for i in range(len(m)):
        for j in range(i + 1, len(m)):
            dx, dy, dz = (float(p[j, axis] - p[i, axis]) for axis in range(3))
            inverse_cube = (dx*dx + dy*dy + dz*dz + epsilon*epsilon)**-1.5
            for axis, delta in enumerate((dx, dy, dz)):
                result[i, axis] += m[j] * delta * inverse_cube
                result[j, axis] -= m[i] * delta * inverse_cube
    output = result.tolist()
    end = time.perf_counter()
    return output, {'captureSeconds': captured-start, 'solveSeconds': end-captured, 'totalSeconds': end-start}


def stencil(p, nodes):
    scaled = p / (LENGTH / (nodes-1))
    base = np.minimum(np.floor(scaled).astype(np.int64), nodes-2)
    fraction = scaled-base
    entries = []
    for corner in itertools.product((0, 1), repeat=3):
        indices = tuple(base[:, axis]+corner[axis] for axis in range(3))
        weights = np.prod(np.where(np.array(corner), fraction, 1-fraction), axis=1)
        entries.append((indices, weights))
    return entries


def field(positions, masses, nodes, epsilon):
    start = time.perf_counter()
    if nodes not in GRIDS:
        raise ValueError('Grid nodes must be 17, 33 or 65.')
    p, m = capture(positions, masses, epsilon)
    captured = time.perf_counter()
    padded = 2*nodes
    shape = (padded,)*3
    entries = stencil(p, nodes)
    grid_mass = np.zeros(shape, dtype=np.float64)
    for indices, weights in entries:
        np.add.at(grid_mass, indices, weights*m)
    mass_error = abs(float(grid_mass.sum())-float(m.sum()))/float(m.sum())
    deposited = time.perf_counter()
    spectrum = np.fft.rfftn(grid_mass)
    del grid_mass
    spacing = LENGTH/(nodes-1)
    indices = np.arange(padded)
    displacement = np.where(indices < nodes, indices, indices-padded)*spacing
    x, y, z = displacement[:, None, None], displacement[None, :, None], displacement[None, None, :]
    inverse_cube = (x*x+y*y+z*z+epsilon*epsilon)**-1.5
    result = np.zeros_like(p)
    sampling_seconds = 0.0
    for axis, coordinate in enumerate((x, y, z)):
        kernel = -coordinate*inverse_cube
        # Offset ±nodes cannot occur between physical nodes. Zero its plane to preserve oddness.
        kernel[nodes, :, :] = 0; kernel[:, nodes, :] = 0; kernel[:, :, nodes] = 0
        transformed = np.fft.rfftn(kernel)
        transformed *= spectrum
        acceleration = np.fft.irfftn(transformed, s=shape, axes=(0, 1, 2))
        before_sample = time.perf_counter()
        for cell, weights in entries:
            result[:, axis] += weights*acceleration[cell]
        sampling_seconds += time.perf_counter()-before_sample
        del kernel, transformed, acceleration
    output = result.tolist()
    end = time.perf_counter()
    if not np.isfinite(result).all():
        raise ValueError('Nonfinite field output.')
    return output, {'captureSeconds': captured-start, 'depositionSeconds': deposited-captured,
                    'kernelAndSolveSeconds': end-deposited-sampling_seconds,
                    'samplingSeconds': sampling_seconds, 'totalSeconds': end-start,
                    'relativeDepositedMassError': mass_error}


def assess(actual, reference, masses, epsilon):
    a, b = np.asarray(actual, dtype=float), np.asarray(reference, dtype=float)
    if a.shape != b.shape or a.shape != (len(masses), 3) or not np.isfinite(a).all() or not np.isfinite(b).all():
        raise ValueError('Force comparison requires matching finite triples.')
    errors = np.linalg.norm(a-b, axis=1)
    reference_rms = float(np.sqrt(np.mean(np.sum(b*b, axis=1))))
    error_rms = float(np.sqrt(np.mean(errors*errors)))
    scale = sum(masses)/epsilon**2
    self_case = reference_rms == 0
    normalized = error_rms/reference_rms if not self_case else None
    worst = float(errors.max())/reference_rms if not self_case else None
    self_error = float(errors.max())/scale if self_case else None
    net_force = float(np.linalg.norm(np.sum(a*np.asarray(masses)[:, None], axis=0)))
    momentum_error = net_force/(sum(masses)**2/epsilon**2)
    passed = (self_error <= SELF_LIMIT if self_case else normalized <= RMS_LIMIT and worst <= WORST_LIMIT)
    return {'normalizedRmsError': normalized, 'worstErrorOverReferenceRms': worst,
            'referenceRmsAcceleration': reference_rms, 'absoluteRmsError': error_rms,
            'absoluteMaxError': float(errors.max()), 'normalizedSelfForce': self_error,
            'normalizedNetForce': momentum_error, 'qualified': bool(passed and momentum_error <= MOMENTUM_LIMIT)}


def fixtures():
    rng = random.Random(1729)
    pair = [[3.173, 3.619, 3.883], [4.431, 4.107, 4.557]]
    sparse = [[rng.uniform(1, 7) for _ in range(3)] for _ in range(32)]
    masses = [rng.uniform(.5, 2) for _ in range(32)]
    cluster = [[3.71+rng.gauss(0, .12) for _ in range(3)] for _ in range(32)]
    shift = [.173, -.219, .137]
    translate = lambda ps: [[x+d for x, d in zip(p, shift)] for p in ps]
    return [
        ('isolated_offgrid', [[3.173, 2.291, 4.867]], [3.0], None),
        ('pair_boundary', [[0, 4, 4], [8, 4, 4]], [2.0, 5.0], None),
        ('pair_rotated', pair, [2.0, 5.0], None),
        ('pair_translated', translate(pair), [2.0, 5.0], 'pair_rotated'),
        ('sparse', sparse, masses, None),
        ('sparse_translated', translate(sparse), masses, 'sparse'),
        ('cluster', cluster, masses, None),
    ]


def experiment(grids, samples):
    rows = []
    # Discard one bounded setup run per implementation before reported timings.
    direct([[4, 4, 4]], [1], SOFTENING)
    field([[4, 4, 4]], [1], 17, SOFTENING)
    for name, positions, masses, translation_of in fixtures():
        for nodes in grids:
            timings, comparisons = [], []
            for sample in range(samples):
                order = ('direct', 'field') if sample % 2 == 0 else ('field', 'direct')
                outputs, times = {}, {}
                for strategy in order:
                    outputs[strategy], times[strategy] = (direct(positions, masses, SOFTENING) if strategy == 'direct'
                                                         else field(positions, masses, nodes, SOFTENING))
                comparisons.append(assess(outputs['field'], outputs['direct'], masses, SOFTENING))
                timings.append({'order': list(order), **times})
            qualified = all(c['qualified'] for c in comparisons) and all(s['field']['relativeDepositedMassError'] <= 1e-12 for s in timings)
            rows.append({'case': name, 'nodesPerAxis': nodes, 'paddedNodesPerAxis': 2*nodes,
                         'spacing': LENGTH/(nodes-1), 'positions': positions, 'masses': masses,
                         'translationOf': translation_of, 'qualified': qualified,
                         'comparisons': comparisons, 'samples': timings,
                         'directAcceleration': outputs['direct'], 'fieldAcceleration': outputs['field']})
    translations = []
    refinement = []
    for row in rows:
        if row['translationOf']:
            base = next(r for r in rows if r['case'] == row['translationOf'] and r['nodesPerAxis'] == row['nodesPerAxis'])
            translations.append({'case': row['case'], 'nodesPerAxis': row['nodesPerAxis'],
                                 'fieldTranslationDifference': assess(row['fieldAcceleration'], base['fieldAcceleration'], row['masses'], SOFTENING),
                                 'directTranslationDifference': assess(row['directAcceleration'], base['directAcceleration'], row['masses'], SOFTENING)})
    for name, _, _, _ in fixtures():
        selected = [r for r in rows if r['case'] == name]
        errors = [r['comparisons'][0]['absoluteRmsError'] for r in selected]
        refinement.append({'case': name, 'grids': list(grids), 'absoluteRmsErrors': errors,
                           'monotoneNonIncreasing': all(b <= a for a, b in zip(errors, errors[1:]))})
    translations_pass = lambda n: all(t['fieldTranslationDifference']['qualified'] for t in translations if t['nodesPerAxis'] == n)
    return {'schema': 'ksp-continuum-field-gravity/v1',
            'model': {'kernel': '-G*r/(r^2+epsilon^2)^(3/2)', 'G': 1.0, 'units': 'normalized',
                      'boxLength': LENGTH, 'softening': SOFTENING, 'boundary': 'isolated, zero-padded linear convolution',
                      'depositionAndSampling': 'matching CIC on endpoint nodes', 'seed': 1729},
            'limits': {'maxBodies': MAX_BODIES, 'maxNodesPerAxis': 65, 'maxPaddedCells': 130**3,
                       'planningMemoryBudgetMiB': 512, 'hardRssLimitEnforced': False},
            'acceptance': {'normalizedRmsError': RMS_LIMIT, 'worstErrorOverReferenceRms': WORST_LIMIT,
                           'normalizedSelfForce': SELF_LIMIT, 'normalizedNetForce': MOMENTUM_LIMIT,
                           'relativeDepositedMassError': 1e-12},
            'provenance': {'python': platform.python_version(), 'numpy': np.__version__,
                           'machine': platform.machine(), 'platform': platform.system(),
                           'sourceSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},
            'timingScope': 'capture, validation, allocation, deposition, kernel construction, FFT solve, sampling and output list; no cached kernel',
            'samplesPerRow': samples, 'rows': rows, 'translationChecks': translations, 'refinement': refinement,
            'finestGridQualified': all(r['qualified'] for r in rows if r['nodesPerAxis'] == max(grids)) and translations_pass(max(grids)),
            'allRequestedGridsQualified': all(r['qualified'] for r in rows) and all(translations_pass(n) for n in grids),
            'trajectoryQualification': False, 'stockPhysicsSpeedupMeasured': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--grids', default='17,33,65')
    parser.add_argument('--samples', type=int, default=3)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        grids = tuple(int(x) for x in args.grids.split(','))
        if not grids or tuple(sorted(set(grids))) != grids or any(n not in GRIDS for n in grids) or not 1 <= args.samples <= 5:
            raise ValueError('Use ascending unique grids from 17,33,65 and 1 through 5 samples.')
        if args.output.exists():
            raise ValueError('Output already exists; choose a new report path.')
        if np.__version__ != '2.2.6':
            raise ValueError('This fixture requires pinned numpy==2.2.6.')
        report = experiment(grids, args.samples)
        with args.output.open('x', encoding='utf-8') as stream:
            stream.write(json.dumps(report, indent=2, allow_nan=False)+'\n')
        print(json.dumps({'finestGridQualified': report['finestGridQualified'], 'rows': len(report['rows'])}))
        return 0 if report['finestGridQualified'] else 2
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
