#!/usr/bin/env python3
"""Summarize observed shadow-model residuals without claiming solver equivalence."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import sys

from qualification_report import ReportError, distribution, finite, integer, parse_physical_input

MAX_BYTES = 4 * 1024 * 1024
TERMINAL = {'complete', 'timeout', 'unavailable', 'interrupted', 'failed'}
COMPARISONS = {'not-accepted', 'waiting', 'compared', 'skipped-context-change',
               'skipped-missed-boundary', 'skipped-timestep-change', 'skipped-timestep-resolution',
               'skipped-invalid-observation'}
TIMINGS = ('captureMilliseconds', 'submitMilliseconds', 'collectMilliseconds',
           'collectAuditMilliseconds', 'handoffWallMilliseconds')
RESIDUALS = ('observedPositionMaxMeters', 'observedPositionRmsMeters',
             'observedVelocityMaxMetersPerSecond', 'observedVelocityRmsMetersPerSecond')


def require(condition, reason):
    if not condition:
        raise ReportError(reason)


def text(value, label, nullable=False):
    if value is None and nullable:
        return value
    require(isinstance(value, str) and 0 < len(value) <= 4096, label + ' must be bounded text')
    return value


def clean_json(value):
    if isinstance(value, float):
        require(math.isfinite(value), 'nonfinite JSON number')
    elif isinstance(value, list):
        for item in value:
            clean_json(item)
    elif isinstance(value, dict):
        for item in value.values():
            clean_json(item)


def unique_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON key')
        result[key] = value
    return result


def summary(data):
    require(isinstance(data, dict) and data.get('schema') == 'ksp-continuum-flight-shadow/v2',
            'requires observed-comparison v2 receipt; v1 arithmetic is not observed physics')
    require(data.get('status') in TERMINAL, 'unsupported terminal capture status')
    evidence = text(data.get('evidence'), 'evidence')
    require(evidence in ('native-adapter-observation', 'portable-helper-fixture', 'managed-api-test-double'),
            'unsupported evidence provenance')
    for field in ('scope', 'framePolicy', 'comparisonScope', 'units'):
        text(data.get(field), field)
    text(data.get('reason'), 'reason', nullable=True)
    for field in ('unity', 'ksp', 'plugin', 'startedUtc'):
        text(data.get(field), field, nullable=True)
    limit = integer(data.get('maxBodies'), 'maxBodies', 1, 512)
    integer(data.get('requestedSamples'), 'requestedSamples', 1, 512)
    submitted = integer(data.get('submitted'), 'submitted', 0, 512)
    accepted = integer(data.get('accepted'), 'accepted', 0, submitted)
    stale = integer(data.get('stale'), 'stale', 0, submitted)
    compared = integer(data.get('compared'), 'compared', 0, accepted)
    skipped = integer(data.get('comparisonSkipped'), 'comparisonSkipped', 0, accepted)
    finite(data.get('wallSeconds'), 'wallSeconds', 0, 1e9)
    for field in ('readyTimeoutSeconds', 'activeTimeoutSeconds'):
        finite(data.get(field), field, 0, 1e9)
    epochs = integer(data.get('physicsEpochs'), 'physicsEpochs', 0, 2**63-1)
    origins = integer(data.get('originEvents'), 'originEvents', 0, 2**63-1)
    samples = data.get('samples')
    require(isinstance(samples, list) and len(samples) == submitted, 'submitted count mismatch')
    counts, comparisons = Counter(), Counter()
    timings = {field: [] for field in TIMINGS}
    body_counts, compared_samples = [], []
    last_tick = 0
    for sample in samples:
        require(isinstance(sample, dict), 'invalid sample')
        status = sample.get('status')
        require(status in ('accepted', 'stale-discarded') or status in {'abandoned-on-' + s for s in TERMINAL},
                'unsupported sample status')
        counts[status] += 1
        tick = integer(sample.get('tick'), 'tick', last_tick + 1, 2**63-1)
        last_tick = tick
        for field in ('topologyGeneration', 'frameGeneration'):
            integer(sample.get(field), field, 0, 2**63-1)
        body_count = integer(sample.get('bodies'), 'bodies', 1, limit)
        integer(sample.get('parts'), 'parts', body_count, 100000)
        body_counts.append(body_count)
        epoch = integer(sample.get('physicsEpoch'), 'physicsEpoch', 0, epochs)
        integer(sample.get('floatingOriginEventCount'), 'origin count', 0, origins)
        for field in ('captureUnityFrame', 'collectUnityFrame', 'comparisonUnityFrame'):
            integer(sample.get(field), field, 0, 2**31-1)
        for field in ('packed', 'analyticAvailable', 'observedComparisonAvailable'):
            require(type(sample.get(field)) is bool, field + ' must be boolean')
        vessel = sample.get('vesselId')
        require(isinstance(vessel, str) and re.fullmatch(
            r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', vessel), 'invalid vessel identity')
        for field in ('body', 'situation'):
            text(sample.get(field), field)
        finite(sample.get('universalTime'), 'universalTime', -1e100, 1e100)
        require(finite(sample.get('stepSeconds'), 'stepSeconds', 0, 1e6) > 0, 'nonpositive step')
        finite(sample.get('warpRate'), 'warpRate', 0, 1e9)
        for field in ('analyticMaxPositionError', 'analyticMaxVelocityError'):
            finite(sample.get(field), field, 0, 1e100)
        for field in TIMINGS:
            timings[field].append(finite(sample.get(field), field, 0, 1e9))
        comparison = sample.get('comparisonStatus')
        require(comparison in COMPARISONS or comparison in {'skipped-on-' + s for s in TERMINAL},
                'unsupported comparison status')
        comparisons[comparison] += 1
        available = sample['observedComparisonAvailable']
        require(available == (comparison == 'compared'), 'comparison availability contradicts status')
        count = integer(sample.get('comparedBodies'), 'comparedBodies', 0, body_count)
        comparison_epoch = integer(sample.get('comparisonPhysicsEpoch'), 'comparison epoch', 0, epochs)
        comparison_origins = integer(sample.get('comparisonFloatingOriginEventCount'),
                                     'comparison origin count', 0, origins)
        for field in ('captureFixedTimeSeconds', 'comparisonFixedTimeSeconds'):
            finite(sample.get(field), field, -1e100, 1e100)
        delta = finite(sample.get('observedDeltaSeconds'), 'observedDeltaSeconds', -1e100, 1e100)
        values = [finite(sample.get(field), field, 0, 1e100) for field in RESIDUALS]
        require(values[1] <= values[0] and values[3] <= values[2], 'RMS exceeds maximum')
        if available:
            frame_velocity = sample.get('comparisonRawKrakensbaneFrameVelocity')
            require(isinstance(frame_velocity, list) and len(frame_velocity) == 3,
                    'comparison frame velocity has wrong shape')
            for component in frame_velocity:
                finite(component, 'comparison frame velocity', -1e100, 1e100)
            require(status == 'accepted' and count == body_count and comparison_epoch == epoch + 1
                    and comparison_origins >= sample['floatingOriginEventCount'] and delta > 0,
                    'comparison does not match accepted next-boundary batch')
            capture_time = sample['captureFixedTimeSeconds']
            observed_time = sample['comparisonFixedTimeSeconds']
            tolerance = max(1e-6, max(abs(capture_time), abs(observed_time)) * 2.384185791015625e-7)
            require(delta == observed_time - capture_time and tolerance < sample['stepSeconds'] * .25
                    and abs(delta - sample['stepSeconds']) <= tolerance, 'inconsistent observed timestep')
            compared_samples.append(sample)
        else:
            require(count == 0 and all(value == 0 for value in values), 'unavailable residual contains claimed data')
    require(counts['accepted'] == accepted and counts['stale-discarded'] == stale,
            'acceptance counts contradict samples')
    require(len(compared_samples) == compared and sum(v for k, v in comparisons.items() if k.startswith('skipped-')) == skipped,
            'comparison counts contradict samples')
    first = data.get('firstAcceptedBatch')
    require(isinstance(first, list) and len(first) <= limit, 'invalid first batch')
    require(bool(first) == (accepted > 0), 'first batch availability contradicts accepted count')
    first_tick = integer(data.get('firstAcceptedTick'), 'firstAcceptedTick', 0, 2**63-1)
    for body in first:
        require(isinstance(body, dict), 'invalid physical body')
        integer(body.get('id'), 'body id', 0, limit-1)
        integer(body.get('nativeInstanceId'), 'native body id', -2**31, 2**31-1)
        finite(body.get('mass'), 'body mass', 0, 1e100)
    physical = parse_physical_input(data, first, samples, first_tick)
    require(physical is not None, 'missing physical-input provenance')
    residuals = None
    if compared_samples:
        count = sum(s['comparedBodies'] for s in compared_samples)
        residuals = {'bodyComparisons': count}
        for label, max_field, rms_field in (
            ('positionMeters', RESIDUALS[0], RESIDUALS[1]),
            ('velocityMetersPerSecond', RESIDUALS[2], RESIDUALS[3])):
            scale = max(s[rms_field] for s in compared_samples)
            rms = 0 if scale == 0 else scale * math.sqrt(sum(
                (s[rms_field]/scale)**2 * s['comparedBodies']/count for s in compared_samples))
            residuals[label] = {'maximum': max(s[max_field] for s in compared_samples), 'rms': rms}
    return {
        'schema': 'ksp-continuum-shadow-summary/v1', 'sourceSchema': data['schema'],
        'status': data['status'], 'reason': data.get('reason'), 'evidence': evidence,
        'scope': data['scope'], 'comparisonScope': data['comparisonScope'], 'framePolicy': data['framePolicy'],
        'installedComparisonQualified': False, 'solverAccuracyQualified': False,
        'counts': {'submitted': submitted, 'accepted': accepted, 'stale': stale,
                   'abandoned': submitted-accepted-stale},
        'compared': compared, 'comparisonSkipped': skipped, 'comparisonStatuses': dict(sorted(comparisons.items())),
        'residualSource': 'producer-reported sample residuals; raw observed body vectors are not in this receipt',
        'residuals': residuals, 'residualAggregation': 'body-weighted sample RMS; zero-force model discrepancy, not solver error',
        'timingsMilliseconds': {key: distribution(values) for key, values in timings.items()},
        'sampleBodyCounts': distribution(body_counts), 'physicalInput': physical,
        'wallSeconds': data['wallSeconds'],
        'sourceRuntime': {key: data.get(key) for key in ('unity', 'ksp', 'plugin', 'startedUtc')},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        with args.input.open('rb') as source:
            raw = source.read(MAX_BYTES + 1)
        require(len(raw) <= MAX_BYTES, 'input exceeds 4 MiB')
        data = json.loads(raw, object_pairs_hook=unique_keys)
        clean_json(data)
        result = summary(data)
        result['sourceSha256'] = hashlib.sha256(raw).hexdigest()
        payload = json.dumps(result, indent=2, allow_nan=False) + '\n'
        with args.output.open('x', encoding='utf-8') as output:
            output.write(payload)
        return 0
    except (OSError, ValueError, TypeError, OverflowError, RecursionError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
