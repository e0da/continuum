"""Bounded spatial smooth-split stencil oracle, checked against padded FFT snapshots."""
import importlib.util
from pathlib import Path
import argparse
import hashlib
import json
import math
import platform
import sys
import time
import numpy as np

spec = importlib.util.spec_from_file_location('trajectory', Path(__file__).with_name('trajectory.py'))
trajectory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trajectory)
spec = importlib.util.spec_from_file_location('field_gravity', Path(__file__).with_name('run.py'))
mesh = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mesh)


def far_coefficient(r2):
    r = np.sqrt(r2)
    t = np.clip((r-trajectory.INNER)/(trajectory.OUTER-trajectory.INNER), 0, 1)
    w = 1-10*t**3+15*t**4-6*t**5
    dw = -30*t*t*(1-t)**2/(trajectory.OUTER-trajectory.INNER)
    u = -1/np.sqrt(r2+trajectory.EPSILON**2)
    derivative = (1-w)*r/(r2+trajectory.EPSILON**2)**1.5-dw*u
    return np.divide(derivative, r, out=np.zeros_like(r), where=r != 0)


def capture(positions, masses, nodes):
    if nodes not in mesh.GRIDS:
        raise ValueError('Grid nodes must be 17, 33 or 65.')
    if len(masses) > 16:
        raise ValueError('Spatial stencil oracle is bounded to 16 bodies.')
    return mesh.capture(positions, masses, trajectory.EPSILON)


def near_force(p, m):
    result = np.zeros_like(p)
    for i in range(len(m)):
        for j in range(i+1, len(m)):
            delta = p[j]-p[i]
            r = float(np.linalg.norm(delta))
            derivative = trajectory.split_pair(r)['nearRadialDerivative']
            value = delta*(derivative/r) if r else np.zeros(3)
            result[i] += m[j]*value
            result[j] -= m[i]*value
    return result


def spatial_force(positions, masses, nodes):
    p, m = capture(positions, masses, nodes)
    entries = mesh.stencil(p, nodes)
    coordinates = np.stack([np.column_stack(cell) for cell, _ in entries], axis=1)*(mesh.LENGTH/(nodes-1))
    weights = np.stack([weight for _, weight in entries], axis=1)
    result = near_force(p, m)
    # Odd kernels and equal deposit/sample weights cancel each body's self term exactly.
    for i in range(len(m)):
        for j in range(i+1, len(m)):
            offsets = coordinates[j][None, :, :] - coordinates[i][:, None, :]
            weighted = weights[i][:, None]*weights[j][None, :]*far_coefficient(np.sum(offsets*offsets, axis=2))
            pair = np.sum(offsets*weighted[:, :, None], axis=(0, 1))
            result[i] += m[j]*pair
            result[j] -= m[i]*pair
    if not np.isfinite(result).all():
        raise ValueError('Nonfinite spatial force.')
    return result.tolist()


def fft_force(positions, masses, nodes):
    p, m = capture(positions, masses, nodes)
    entries = mesh.stencil(p, nodes)
    shape = (2*nodes,)*3
    density = np.zeros(shape)
    for cell, weights in entries:
        np.add.at(density, cell, weights*m)
    spectrum = np.fft.rfftn(density)
    indices = np.arange(2*nodes)
    displacement = np.where(indices < nodes, indices, indices-2*nodes)*(mesh.LENGTH/(nodes-1))
    x, y, z = displacement[:, None, None], displacement[None, :, None], displacement[None, None, :]
    coefficient = far_coefficient(x*x+y*y+z*z)
    result = near_force(p, m)
    for axis, coordinate in enumerate((x, y, z)):
        kernel = -coordinate*coefficient
        kernel[nodes, :, :] = 0; kernel[:, nodes, :] = 0; kernel[:, :, nodes] = 0
        grid = np.fft.irfftn(np.fft.rfftn(kernel)*spectrum, s=shape, axes=(0, 1, 2))
        for cell, weights in entries:
            result[:, axis] += weights*grid[cell]
    if not np.isfinite(result).all():
        raise ValueError('Nonfinite FFT force.')
    return result.tolist()


def snapshots(nodes):
    cases = [('isolated', [[3.173, 2.291, 4.867]], [3]),
             ('boundary', [[0, 4, 4], [8, 4, 4]], [2, 5]),
             ('shell_aligned', [[3, 4, 4], [4.5, 4, 4]], [2, 5]),
             ('shell_offgrid', [[3.173, 3.619, 3.883], [4.831, 4.281, 4.159]], [2, 5]),
             ('mixed', [[3.173, 3.619, 3.883], [4.831, 4.281, 4.159], [6.1, 2.13, 3.3]], [2, 5, 3])]
    rows = []
    for name, p, m in cases:
        start = time.perf_counter()
        direct, direct_timing = mesh.direct(p, m, trajectory.EPSILON)
        spatial = spatial_force(p, m, nodes)
        spatial_done = time.perf_counter()
        fft = fft_force(p, m, nodes)
        fft_done = time.perf_counter()
        agreement = max(math.dist(a, b) for a, b in zip(spatial, fft))
        torque = np.sum(np.cross(np.array(p)-np.average(p, axis=0, weights=m),
                                 np.array(spatial)*np.array(m)[:, None]), axis=0)
        rows.append({'case': name, 'nodesPerAxis': nodes, 'positions': p, 'masses': m,
                     'directAcceleration': direct, 'spatialAcceleration': spatial, 'fftAcceleration': fft,
                     'forceError': mesh.assess(spatial, direct, m, trajectory.EPSILON),
                     'torqueAboutCenterOfMass': torque.tolist(),
                     'fftStencilMaxDifference': agreement, 'qualified': agreement <= 1e-11,
                     'timingSeconds': {'direct': direct_timing['totalSeconds'],
                         'stencil': spatial_done-start-direct_timing['totalSeconds'], 'fft': fft_done-spatial_done}})
    return rows


def candidate(fixture, nodes, reference, direct_row, schedule):
    gates = trajectory.GATES
    def provider(p, m):
        # Fixed inertial placement; a moving origin would change the spatial operator.
        return spatial_force([[x+4 for x in row] for row in p], m, nodes)
    refinements = []
    for count in schedule:
        started = time.perf_counter()
        dt, steps = fixture['period']/count, count*fixture['periods']
        run = trajectory.integrate(fixture['positions'], fixture['velocities'], fixture['masses'], dt, steps,
                                   force_provider=provider)
        back = trajectory.integrate(run['positions'], run['velocities'], fixture['masses'], -dt, steps,
                                    track=False, force_provider=provider)
        endpoint = trajectory.state_error(run, reference, fixture)
        reversal = trajectory.state_error(back, fixture, fixture)
        d = run['diagnostics']
        d['normalizedCenterDrift'] = d['absoluteCenterDrift']/fixture['positionScale']
        d['normalizedMomentumDrift'] = d['absoluteMomentumDrift']/(sum(fixture['masses'])*fixture['velocityScale'])
        checks = {'energy': d['relativeEnergy'] <= gates['energy'],
                  'angularMomentum': d['relativeAngularMomentum'] <= gates['angularMomentum'],
                  'centerOfMass': d['normalizedCenterDrift'] <= gates['centerOfMass'],
                  'momentum': d['normalizedMomentumDrift'] <= gates['momentum'],
                  'endpoint': max(endpoint.values()) <= gates['endpoint'],
                  'reversal': max(reversal.values()) <= gates['reversal']}
        refinements.append({'stepsPerPeriod': count, 'steps': steps, 'dt': dt,
                            'endpointError': endpoint, 'reversalError': reversal, **run,
                            'checks': checks, 'qualified': all(checks.values()),
                            'elapsedSeconds': time.perf_counter()-started})
    ratios = [{key: a['endpointError'][key]/b['endpointError'][key] for key in ('position', 'velocity')}
              for a, b in zip(refinements, refinements[1:])]
    refinement_pass = all(gates['refinementMin'] <= x <= gates['refinementMax']
                          for row in ratios[-2:] for x in row.values())
    fine = refinements[-1]
    crossing = fine['separationRange'][0] < trajectory.INNER and fine['separationRange'][1] > trajectory.OUTER
    return {'fixture': fixture, 'nodesPerAxis': nodes, 'spacing': mesh.LENGTH/(nodes-1),
            'referenceKind': direct_row['referenceKind'], 'referenceAgreement': direct_row['referenceAgreement'],
            'refinements': refinements, 'endpointRefinementRatios': ratios,
            'refinementQualified': refinement_pass, 'crossesBothSwitchBoundaries': crossing,
            'sameStepDirectDifference': trajectory.state_error(fine, direct_row['refinements'][-1], fixture),
            'qualified': fine['qualified'] and refinement_pass and direct_row['qualified'] and
                         (fixture['name'] == 'circular' or crossing)}


def experiment(grids):
    if not grids or len(set(grids)) != len(grids) or any(n not in mesh.GRIDS for n in grids):
        raise ValueError('Use a nonempty distinct list drawn from grids 17, 33, 65.')
    started = time.perf_counter()
    direct_baseline = trajectory.experiment(1)
    rows, static = [], []
    for direct_row in direct_baseline['trajectories']:
        fixture = direct_row['fixture']
        if fixture['name'] == 'circular':
            reference = trajectory.circular_exact(fixture, fixture['period']*fixture['periods'])
        else:
            n = direct_baseline['schedule']['noncircularReferenceStepsPerPeriod'][-1]
            reference = trajectory.integrate(fixture['positions'], fixture['velocities'], fixture['masses'],
                                               fixture['period']/n, n*fixture['periods'], track=False)
        for nodes in grids:
            rows.append(candidate(fixture, nodes, reference, direct_row, direct_baseline['schedule']['stepsPerPeriod']))
    for nodes in grids:
        static.extend(snapshots(nodes))
    fft_pass = all(row['qualified'] for row in static)
    finest = max(grids)
    return {'schema': 'ksp-continuum-spatial-split/v1', 'model': direct_baseline['model'],
            'spatialModel': {'gridBox': [0, 8], 'inertialTranslation': [4, 4, 4], 'deposition': 'CIC',
                'sampling': 'CIC', 'paddedNodesPerAxis': '2 * nodes', 'innerRadius': trajectory.INNER,
                'outerRadius': trajectory.OUTER, 'near': 'exact analytic pair derivative',
                'far': 'sampled analytic far-force kernel evaluated by 8x8 pair stencil convolution',
                'complexity': 'O(64 N^2) per force call; not a scalable mesh solve',
                'discretePotentialGradientClaim': False},
            'gates': dict(trajectory.GATES), 'fftSnapshotAbsoluteGate': 1e-11,
            'schedule': direct_baseline['schedule'], 'grids': list(grids),
            'directBaseline': direct_baseline, 'trajectories': rows, 'snapshots': static,
            'fftSnapshotsQualified': fft_pass, 'allRequestedGridsQualified': all(r['qualified'] for r in rows),
            'qualified': direct_baseline['qualified'] and fft_pass and
                         all(r['qualified'] for r in rows if r['nodesPerAxis'] == finest),
            'fftTrajectoryQualified': False, 'speedClaim': False,
            'measurement': {'elapsedSeconds': time.perf_counter()-started,
                'scope': 'direct baseline/references; forward and reverse stencil trajectories including capture, stencils, force evaluation and diagnostics; FFT snapshots; excludes serialization/startup',
                'trajectoryTimingIncludesFFT': False},
            'provenance': {'python': platform.python_version(), 'numpy': np.__version__,
                'sourceSha256': {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                                 for name in ('spatial.py', 'trajectory.py', 'run.py')}}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--grids', type=int, nargs='+', default=list(mesh.GRIDS))
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.output.exists():
            raise ValueError('Output already exists; choose a new report path.')
        report = experiment(args.grids)
        with args.output.open('x', encoding='utf-8') as stream:
            stream.write(json.dumps(report, indent=2, allow_nan=False)+'\n')
        print(json.dumps({'qualified': report['qualified'], 'fftSnapshotsQualified': report['fftSnapshotsQualified'],
                          'fftTrajectoryQualified': False}))
        return 0 if report['qualified'] else 2
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
