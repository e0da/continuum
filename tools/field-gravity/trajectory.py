#!/usr/bin/env python3
"""Bounded direct Plummer trajectory and smooth analytic split fixture."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import platform
import sys
import time

EPSILON = .6
INNER, OUTER = 1.2, 2.4
STEPS_PER_PERIOD = (128, 256, 512, 1024)
GATES = {'energy': 1e-4, 'angularMomentum': 1e-10, 'centerOfMass': 1e-10,
         'momentum': 1e-10, 'reversal': 1e-8, 'endpoint': 1e-3,
         'refinementMin': 3.0, 'refinementMax': 5.0, 'referenceAgreement': 1e-6,
         'splitReconstruction': 1e-12, 'splitGradient': 1e-6,
         'splitContinuity': 1e-5, 'splitTrajectory': 1e-10}


def norm(v):
    return math.sqrt(sum(x*x for x in v))


def cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def split_pair(r):
    if not math.isfinite(r) or not 0 <= r <= 1e12:
        raise ValueError('Split radius must be finite and within [0,1e12].')
    potential = -1/math.sqrt(r*r+EPSILON**2)
    derivative = r/(r*r+EPSILON**2)**1.5
    if r <= INNER:
        w, dw = 1.0, 0.0
    elif r >= OUTER:
        w, dw = 0.0, 0.0
    else:
        t = (r-INNER)/(OUTER-INNER)
        w = 1-10*t**3+15*t**4-6*t**5
        dw = -30*t*t*(1-t)**2/(OUTER-INNER)
    return {'potential': potential, 'radialDerivative': derivative, 'weight': w, 'weightDerivative': dw,
            'nearPotential': w*potential, 'farPotential': (1-w)*potential,
            'nearRadialDerivative': w*derivative+dw*potential,
            'farRadialDerivative': (1-w)*derivative-dw*potential,
            'naiveNear': w*derivative, 'naiveFar': (1-w)*derivative}


def accelerations(p, masses, mode='direct'):
    a = [[0.0]*3 for _ in masses]
    for i in range(len(masses)):
        for j in range(i+1, len(masses)):
            delta = [p[j][k]-p[i][k] for k in range(3)]
            r2 = sum(x*x for x in delta)
            if mode == 'direct':
                coefficient = (r2+EPSILON**2)**-1.5
            elif mode == 'split':
                r = math.sqrt(r2)
                s = split_pair(r)
                coefficient = (s['nearRadialDerivative']+s['farRadialDerivative'])/r if r else 0.0
            else:
                raise ValueError('Force mode must be direct or split.')
            for k in range(3):
                value = delta[k]*coefficient
                a[i][k] += masses[j]*value
                a[j][k] -= masses[i]*value
    return a


def invariants(p, v, masses):
    energy = sum(.5*m*sum(x*x for x in vel) for m, vel in zip(masses, v))
    for i in range(len(masses)):
        for j in range(i+1, len(masses)):
            energy -= masses[i]*masses[j]/math.sqrt(sum((p[i][k]-p[j][k])**2 for k in range(3))+EPSILON**2)
    momentum = [sum(m*vel[k] for m, vel in zip(masses, v)) for k in range(3)]
    angular = [0.0]*3
    for m, pos, vel in zip(masses, p, v):
        term = cross(pos, vel)
        for k in range(3):
            angular[k] += m*term[k]
    center = [sum(m*pos[k] for m, pos in zip(masses, p))/sum(masses) for k in range(3)]
    return {'energy': energy, 'angular': angular, 'momentum': momentum, 'center': center}


def integrate(positions, velocities, masses, dt, steps, mode='direct', track=True, force_provider=None):
    if mode not in ('direct', 'split'):
        raise ValueError('Force mode must be direct or split.')
    if not math.isfinite(dt) or not 0 < abs(dt) <= 1 or type(steps) is not int or not 1 <= steps <= 131072:
        raise ValueError('Use finite nonzero |dt|<=1 and 1 through 131072 integer steps.')
    if not 1 <= len(masses) <= 16 or len(positions) != len(masses) or len(velocities) != len(masses):
        raise ValueError('Use 1 through 16 bodies with matching state arrays.')
    if any(not math.isfinite(m) or not 1e-6 <= m <= 1e6 for m in masses):
        raise ValueError('Finite positive masses must lie within [1e-6,1e6].')
    for array in (positions, velocities):
        if any(len(row) != 3 or any(not math.isfinite(x) or abs(x) > 1e6 for x in row) for row in array):
            raise ValueError('Initial vectors must be finite triples with components bounded by 1e6.')
    p, v = [list(row) for row in positions], [list(row) for row in velocities]
    def force(state):
        if force_provider is None:
            return accelerations(state, masses, mode)
        value = force_provider(state, masses)
        if len(value) != len(masses) or any(len(row) != 3 or any(not math.isfinite(x) for x in row) for row in value):
            raise ValueError('Force provider must return one finite acceleration triple per body.')
        return value
    a = force(p)
    initial = invariants(p, v, masses) if track else None
    maxima = dict.fromkeys(('relativeEnergy', 'relativeAngularMomentum', 'absoluteCenterDrift', 'absoluteMomentumDrift'), 0.0)
    snapshots = []
    min_r, max_r = math.inf, 0.0
    for step in range(steps+1):
        if track:
            current = invariants(p, v, masses)
            maxima['relativeEnergy'] = max(maxima['relativeEnergy'], abs(current['energy']-initial['energy'])/max(abs(initial['energy']), 1e-30))
            maxima['relativeAngularMomentum'] = max(maxima['relativeAngularMomentum'], math.dist(current['angular'], initial['angular'])/max(norm(initial['angular']), 1e-30))
            expected_center = [initial['center'][k]+initial['momentum'][k]/sum(masses)*(step*dt) for k in range(3)]
            maxima['absoluteCenterDrift'] = max(maxima['absoluteCenterDrift'], math.dist(current['center'], expected_center))
            maxima['absoluteMomentumDrift'] = max(maxima['absoluteMomentumDrift'], math.dist(current['momentum'], initial['momentum']))
            if len(masses) == 2:
                separation = math.dist(p[0], p[1]); min_r = min(min_r, separation); max_r = max(max_r, separation)
            if step % max(1, steps//128) == 0 or step == steps:
                snapshots.append({'time': step*dt, 'positions': [list(x) for x in p],
                                  'relativeEnergyError': abs(current['energy']-initial['energy'])/max(abs(initial['energy']), 1e-30)})
        if step == steps:
            break
        new_p = [[p[i][k]+v[i][k]*dt+.5*a[i][k]*dt*dt for k in range(3)] for i in range(len(masses))]
        new_a = force(new_p)
        v = [[v[i][k]+.5*(a[i][k]+new_a[i][k])*dt for k in range(3)] for i in range(len(masses))]
        p, a = new_p, new_a
    if any(not math.isfinite(x) for state in (p, v) for row in state for x in row):
        raise ValueError('Nonfinite integrated state.')
    return {'positions': p, 'velocities': v, 'diagnostics': maxima, 'samples': snapshots,
            'separationRange': [min_r, max_r] if track and len(masses) == 2 else None}


def case(name):
    if name not in ('circular', 'noncircular'):
        raise ValueError('Unknown fixture.')
    separation = 2.0 if name == 'circular' else 2.8
    masses = [2.0, 5.0]
    omega = math.sqrt(sum(masses)/(separation**2+EPSILON**2)**1.5)
    speed = omega*separation*(1 if name == 'circular' else .7)
    factors = [-masses[1]/sum(masses), masses[0]/sum(masses)]
    return {'name': name, 'masses': masses, 'period': 2*math.pi/omega, 'periods': 4 if name == 'circular' else 2,
            'omega': omega, 'positionScale': separation, 'velocityScale': omega*separation, 'inclination': .4,
            'positions': [[f*separation, 0, 0] for f in factors],
            'velocities': [[0, f*speed*math.cos(.4), f*speed*math.sin(.4)] for f in factors]}


def circular_exact(fixture, time):
    theta = fixture['omega']*time
    r, omega, tilt = fixture['positionScale'], fixture['omega'], fixture['inclination']
    factors = [-fixture['masses'][1]/sum(fixture['masses']), fixture['masses'][0]/sum(fixture['masses'])]
    return {'positions': [[f*r*math.cos(theta), f*r*math.sin(theta)*math.cos(tilt), f*r*math.sin(theta)*math.sin(tilt)] for f in factors],
            'velocities': [[-f*r*omega*math.sin(theta), f*r*omega*math.cos(theta)*math.cos(tilt), f*r*omega*math.cos(theta)*math.sin(tilt)] for f in factors]}


def state_error(actual, expected, fixture):
    return {label: math.sqrt(sum(math.dist(a, b)**2 for a, b in zip(actual[key], expected[key]))/len(fixture['masses']))/fixture[scale]
            for label, key, scale in (('position', 'positions', 'positionScale'), ('velocity', 'velocities', 'velocityScale'))}


def split_checks():
    radii = [0.0, .2, .6, INNER-1e-4, INNER, INNER+1e-4, 1.5, 1.8, 2.1, OUTER-1e-4, OUTER, OUTER+1e-4, 3.0]
    reconstruction, potential_error, gradient, naive_gradient, one_sided = [], [], [], [], []
    values = []
    for radius in radii:
        s = split_pair(radius)
        reconstruction.append(abs(s['nearRadialDerivative']+s['farRadialDerivative']-s['radialDerivative']))
        potential_error.append(abs(s['nearPotential']+s['farPotential']-s['potential']))
        if radius > 0:
            h = 1e-6
            for part, naive in (('near', 'naiveNear'), ('far', 'naiveFar')):
                finite_difference = (split_pair(radius+h)[part+'Potential']-split_pair(radius-h)[part+'Potential'])/(2*h)
                gradient.append(abs(s[part+'RadialDerivative']-finite_difference))
                naive_gradient.append(abs(s[naive]-finite_difference))
        one_sided.append(abs(s['naiveNear']+s['farRadialDerivative']-s['radialDerivative']))
        values.append({'radius': radius, **s})
    continuity = []
    for boundary in (INNER, OUTER):
        low, high = split_pair(boundary-1e-7), split_pair(boundary+1e-7)
        continuity.append({'boundary': boundary, 'radiusDifference': 2e-7,
                           'nearDerivativeDifference': abs(high['nearRadialDerivative']-low['nearRadialDerivative']),
                           'farDerivativeDifference': abs(high['farRadialDerivative']-low['farRadialDerivative'])})
    adversary = max(naive_gradient) > .01 and max(one_sided) > .01
    qualified = max(reconstruction+potential_error) <= GATES['splitReconstruction'] and max(gradient) <= GATES['splitGradient'] and adversary
    qualified = qualified and all(max(row['nearDerivativeDifference'], row['farDerivativeDifference']) <= GATES['splitContinuity'] for row in continuity)
    qualified = qualified and all(split_pair(x)['weightDerivative'] == 0 for x in (INNER, OUTER))
    return {'qualified': qualified, 'innerRadius': INNER, 'outerRadius': OUTER,
            'maxForceReconstructionError': max(reconstruction), 'maxPotentialReconstructionError': max(potential_error),
            'maxComponentGradientError': max(gradient), 'omissionMaxComponentGradientError': max(naive_gradient),
            'oneSidedOmissionMaxReconstructionError': max(one_sided), 'omissionAdversaryDetected': adversary,
            'bothNaiveComponentsStillReconstruct': max(abs(v['naiveNear']+v['naiveFar']-v['radialDerivative']) for v in values) <= GATES['splitReconstruction'],
            'boundaryChecks': continuity, 'radialSamples': values}


def experiment(refinement_level=0):
    if type(refinement_level) is not int or refinement_level not in (0, 1):
        raise ValueError('Refinement level must be 0 or 1; this fixture permits only one escalation.')
    started = time.perf_counter()
    schedule = [n*2**refinement_level for n in STEPS_PER_PERIOD]
    reference_schedule = [n*2**refinement_level for n in (8192, 16384)]
    trajectories = []
    for name in ('circular', 'noncircular'):
        fixture = case(name)
        reference_agreement = None
        if name == 'circular':
            reference = circular_exact(fixture, fixture['period']*fixture['periods'])
            reference_kind = 'independent analytic circular Plummer solution'
        else:
            references = [integrate(fixture['positions'], fixture['velocities'], fixture['masses'], fixture['period']/n, n*fixture['periods'], track=False)
                          for n in reference_schedule]
            reference_agreement = state_error(references[0], references[1], fixture)
            reference = references[1]
            reference_kind = 'direct Verlet step refinement; not an independent integration algorithm'
        refinements = []
        for count in schedule:
            dt, steps = fixture['period']/count, count*fixture['periods']
            run = integrate(fixture['positions'], fixture['velocities'], fixture['masses'], dt, steps)
            back = integrate(run['positions'], run['velocities'], fixture['masses'], -dt, steps, track=False)
            endpoint = state_error(run, reference, fixture)
            reversal = state_error(back, fixture, fixture)
            diagnostics = run['diagnostics']
            diagnostics['normalizedCenterDrift'] = diagnostics['absoluteCenterDrift']/fixture['positionScale']
            diagnostics['normalizedMomentumDrift'] = diagnostics['absoluteMomentumDrift']/(sum(fixture['masses'])*fixture['velocityScale'])
            passed = diagnostics['relativeEnergy'] <= GATES['energy'] and diagnostics['relativeAngularMomentum'] <= GATES['angularMomentum']
            passed = passed and diagnostics['normalizedCenterDrift'] <= GATES['centerOfMass'] and diagnostics['normalizedMomentumDrift'] <= GATES['momentum']
            passed = passed and max(endpoint.values()) <= GATES['endpoint'] and max(reversal.values()) <= GATES['reversal']
            refinements.append({'stepsPerPeriod': count, 'steps': steps, 'dt': dt, 'qualified': passed,
                                'endpointError': endpoint, 'reversalError': reversal, **run})
        ratios = [{key: a['endpointError'][key]/b['endpointError'][key] for key in ('position', 'velocity')}
                  for a, b in zip(refinements, refinements[1:])]
        refinement_pass = all(GATES['refinementMin'] <= ratio <= GATES['refinementMax'] for row in ratios[-2:] for ratio in row.values())
        fine = refinements[-1]
        split_run = integrate(fixture['positions'], fixture['velocities'], fixture['masses'], fine['dt'], fine['steps'], mode='split', track=False)
        split_error = state_error(split_run, fine, fixture)
        reference_pass = reference_agreement is None or max(reference_agreement.values()) <= GATES['referenceAgreement']
        shell_crossed = fine['separationRange'][0] < INNER and fine['separationRange'][1] > OUTER
        qualified = fine['qualified'] and refinement_pass and reference_pass and max(split_error.values()) <= GATES['splitTrajectory']
        if name == 'noncircular':
            qualified = qualified and shell_crossed
        trajectories.append({'fixture': fixture, 'referenceKind': reference_kind,
                             'referenceStepsPerPeriod': None if name == 'circular' else reference_schedule,
                             'referenceAgreement': reference_agreement, 'refinements': refinements,
                             'endpointRefinementRatios': ratios, 'refinementQualified': refinement_pass,
                             'completeSplitEndpointDifference': split_error, 'crossesBothSwitchBoundaries': shell_crossed,
                             'qualified': qualified})
    split = split_checks()
    return {'schema': 'ksp-continuum-field-trajectory/v1', 'model': {'G': 1, 'softening': EPSILON, 'units': 'normalized',
            'boundary': 'isolated direct pairs; no mesh or periodic images', 'integrator': 'fixed-step velocity Verlet'},
            'gates': GATES, 'trajectories': trajectories, 'split': split,
            'schedule': {'refinementLevel': refinement_level, 'stepsPerPeriod': schedule,
                         'noncircularReferenceStepsPerPeriod': reference_schedule},
            'measurement': {'elapsedSeconds': time.perf_counter()-started,
                            'scope': 'all fixture integration, references, diagnostics and split checks; excludes serialization and process startup'},
            'qualified': all(row['qualified'] for row in trajectories) and split['qualified'],
            'fftTrajectoryQualified': False, 'speedClaim': False,
            'provenance': {'python': platform.python_version(), 'implementation': platform.python_implementation(),
                           'sourceSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--refinement-level', type=int, default=0, help='0: original schedule; 1: one bounded doubling, same gates.')
    args = parser.parse_args()
    try:
        if args.output.exists():
            raise ValueError('Output already exists; choose a new report path.')
        report = experiment(args.refinement_level)
        with args.output.open('x', encoding='utf-8') as stream:
            stream.write(json.dumps(report, indent=2, allow_nan=False)+'\n')
        print(json.dumps({'qualified': report['qualified'], 'fftTrajectoryQualified': False}))
        return 0 if report['qualified'] else 2
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
