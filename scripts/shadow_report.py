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


def paired_stat(samples, max_field, rms_field):
    count = sum(sample['gravityComparedBodies'] for sample in samples)
    scale = max(sample[rms_field] for sample in samples)
    rms = 0 if scale == 0 else scale * math.sqrt(sum(
        (sample[rms_field] / scale)**2 * sample['gravityComparedBodies'] / count
        for sample in samples))
    return {'maximum': max(sample[max_field] for sample in samples), 'rms': rms}


def nullable_ratio(numerator, denominator):
    if denominator <= 0:
        return None
    ratio = numerator / denominator
    return ratio if math.isfinite(ratio) else None


def near(actual, expected, label):
    require(math.isclose(actual, expected, rel_tol=1e-12, abs_tol=1e-15), label + ' contradicts paired metrics')


def gravity_summary(data, samples):
    present = any(key.startswith('gravity') for key in data) or any(
        any(key.startswith('gravity') or key.startswith('zeroFrameAdjusted') for key in sample)
        for sample in samples)
    if not present:
        return None
    require(data.get('gravityStrategy') == 'central-point-mass-frozen-acceleration/v1', 'unsupported gravity strategy')
    for field in ('gravityExecution', 'gravityFrameAdjustedScope', 'gravityInterpretation'):
        text(data.get(field), field)
    predictive = data.get('framePredictionStrategy') is not None
    if predictive:
        require(data.get('framePredictionStrategy') == 'krakensbane-last-correction-persistence/v1',
                'unsupported frame prediction strategy')
        text(data.get('framePredictionScope'), 'framePredictionScope')
    model_counts, comparison_counts, frame_counts, sources = Counter(), Counter(), Counter(), Counter()
    raw, adjusted = [], []
    predicted = []
    prediction_errors = []
    timing_fields = ('gravityPredictionMilliseconds', 'gravityComparisonMilliseconds')
    timings = {field: [] for field in timing_fields}
    raw_deltas, raw_ratios, adjusted_deltas = [], [], []
    gravity_adjusted_count = zero_adjusted_count = 0
    comparison_statuses = COMPARISONS | {'skipped-worker-stale'} | {'skipped-on-' + s for s in TERMINAL}
    for sample in samples:
        model = sample.get('gravityModelStatus')
        require(model in ('not-captured', 'captured-frozen-acceleration',
                          'unavailable-no-central-body', 'unavailable-invalid-central-model'), 'invalid gravity model status')
        comparison = sample.get('gravityComparisonStatus')
        frame_status = sample.get('gravityFrameAdjustedStatus')
        require(comparison in comparison_statuses, 'invalid gravity comparison status')
        require(frame_status in comparison_statuses | {'unavailable-invalid-frame-delta'}, 'invalid adjusted status')
        model_counts[model] += 1
        comparison_counts[comparison] += 1
        frame_counts[frame_status] += 1
        source = text(sample.get('gravityAccelerationSource'), 'gravityAccelerationSource')
        require(source in ('not-captured', 'analytic-fixture',
                           'FlightGlobals.getGeeForceAtPosition(position,mainBody)'), 'unsupported acceleration source')
        sources[source] += 1
        for field in timing_fields:
            timings[field].append(finite(sample.get(field), field, 0, 1e9))
        for field in ('gravityComparisonAvailable', 'gravityFrameAdjustedVelocityAvailable',
                      'zeroFrameAdjustedVelocityAvailable'):
            require(type(sample.get(field)) is bool, field + ' must be boolean')
        available = sample['gravityComparisonAvailable']
        gravity_adjusted = sample['gravityFrameAdjustedVelocityAvailable']
        zero_adjusted = sample['zeroFrameAdjustedVelocityAvailable']
        gravity_adjusted_count += gravity_adjusted
        zero_adjusted_count += zero_adjusted
        if predictive:
            for field in ('rawKrakensbaneLastCorrection', 'predictedKrakensbaneFrameVelocityDelta'):
                vector = sample.get(field)
                require(isinstance(vector, list) and len(vector) in (0, 3), 'invalid ' + field)
                for value in vector:
                    finite(value, field, -1e100, 1e100)
            require(len(sample['rawKrakensbaneLastCorrection'])
                    == len(sample['predictedKrakensbaneFrameVelocityDelta']),
                    'frame persistence vector availability mismatch')
            if sample['rawKrakensbaneLastCorrection']:
                for correction, delta in zip(sample['rawKrakensbaneLastCorrection'],
                                             sample['predictedKrakensbaneFrameVelocityDelta']):
                    near(delta, -correction, 'frame persistence algebra')
            predicted_status = sample.get('gravityPredictedFrameStatus')
            require(predicted_status in comparison_statuses, 'invalid predicted frame status')
            gravity_predicted = sample.get('gravityPredictedFrameVelocityAvailable')
            zero_predicted = sample.get('zeroPredictedFrameVelocityAvailable')
            require(type(gravity_predicted) is bool and type(zero_predicted) is bool,
                    'predicted availability must be boolean')
            require(gravity_predicted == (predicted_status == 'compared'),
                    'predicted status contradicts availability')
            require(not zero_predicted or gravity_predicted, 'predicted zero lacks paired gravity')
            require(not (gravity_predicted or zero_predicted) or available,
                    'predicted frame metric lacks raw comparison')
            predictive_metrics = (
                ('gravityPredictedFrameVelocityMaxMetersPerSecond',
                 'gravityPredictedFrameVelocityRmsMetersPerSecond', gravity_predicted),
                ('zeroPredictedFrameVelocityMaxMetersPerSecond',
                 'zeroPredictedFrameVelocityRmsMetersPerSecond', zero_predicted))
            for maximum_field, rms_field, metric_available in predictive_metrics:
                maximum = finite(sample.get(maximum_field), maximum_field, 0, 1e100)
                rms = finite(sample.get(rms_field), rms_field, 0, 1e100)
                require(rms <= maximum, 'predicted frame RMS exceeds maximum')
                require(metric_available or maximum == rms == 0,
                        'unavailable predicted metric contains data')
            predicted_delta = finite(sample.get('gravityPredictedFrameVelocityRmsDeltaFromZero'),
                                     'predicted gravity delta', -1e100, 1e100)
            error_vector = sample.get('frameVelocityDeltaPredictionError')
            require(isinstance(error_vector, list) and len(error_vector) in (0, 3),
                    'invalid frame prediction error')
            error_norm = finite(sample.get('frameVelocityDeltaPredictionErrorMetersPerSecond'),
                                'frame prediction error norm', 0, 1e100)
            if gravity_predicted and zero_predicted:
                require(len(sample['predictedKrakensbaneFrameVelocityDelta']) == 3,
                        'predicted comparison lacks captured forecast')
                near(predicted_delta,
                     sample['gravityPredictedFrameVelocityRmsMetersPerSecond']
                     - sample['zeroPredictedFrameVelocityRmsMetersPerSecond'],
                     'predicted gravity delta')
                require(len(error_vector) == 3, 'missing frame prediction error')
                endpoint = sample.get('gravityEndpointFrameVelocityDelta')
                for error, forecast, observed in zip(
                    error_vector, sample['predictedKrakensbaneFrameVelocityDelta'], endpoint
                ):
                    near(error, forecast - observed, 'frame prediction error algebra')
                near(error_norm, math.sqrt(sum(value * value for value in error_vector)),
                     'frame prediction error norm')
                predicted.append(sample)
                prediction_errors.append(error_norm)
            else:
                require(predicted_delta == 0 and error_norm == 0 and not error_vector,
                        'unavailable frame prediction contains results')
        require(available == (comparison == 'compared'), 'gravity availability contradicts status')
        count = integer(sample.get('gravityComparedBodies'), 'gravityComparedBodies', 0, sample['bodies'])
        for field in ('gravityMu', 'gravityOrbitalMu'):
            value = finite(sample.get(field), field, 0, 1e100)
            require(model != 'captured-frozen-acceleration' or value > 0, 'captured gravity requires positive parameters')
        for field in ('gravityCenterUnityWorld', 'gravityMeanAcceleration'):
            values = sample.get(field)
            require(isinstance(values, list) and len(values) == (3 if model == 'captured-frozen-acceleration' else 0),
                    'gravity model vector availability mismatch')
            for value in values:
                finite(value, field, -1e100, 1e100)
        require((source != 'not-captured') == (model == 'captured-frozen-acceleration'), 'gravity source availability mismatch')
        metric_pairs = (
            ('gravityPositionMaxMeters', 'gravityPositionRmsMeters', available),
            ('gravityVelocityMaxMetersPerSecond', 'gravityVelocityRmsMetersPerSecond', available),
            ('gravityFrameAdjustedVelocityMaxMetersPerSecond', 'gravityFrameAdjustedVelocityRmsMetersPerSecond', gravity_adjusted),
            ('zeroFrameAdjustedVelocityMaxMetersPerSecond', 'zeroFrameAdjustedVelocityRmsMetersPerSecond', zero_adjusted))
        for max_field, rms_field, metric_available in metric_pairs:
            maximum = finite(sample.get(max_field), max_field, 0, 1e100)
            rms = finite(sample.get(rms_field), rms_field, 0, 1e100)
            require(rms <= maximum, 'gravity RMS exceeds maximum')
            require(metric_available or maximum == rms == 0, 'unavailable gravity metric contains data')
        delta = finite(sample.get('gravityVelocityRmsDeltaFromZero'), 'gravity raw delta', -1e100, 1e100)
        adjusted_delta = finite(sample.get('gravityFrameAdjustedVelocityRmsDeltaFromZero'), 'adjusted delta', -1e100, 1e100)
        require('gravityVelocityRmsRatioToZero' in sample, 'missing gravity ratio availability')
        ratio = sample['gravityVelocityRmsRatioToZero']
        if ratio is not None:
            finite(ratio, 'gravity ratio', 0, sys.float_info.max)
        endpoint_delta = sample.get('gravityEndpointFrameVelocityDelta')
        require(isinstance(endpoint_delta, list) and len(endpoint_delta) in (0, 3), 'invalid endpoint frame delta')
        for value in endpoint_delta:
            finite(value, 'endpoint frame delta', -1e100, 1e100)
        if available:
            require(model == 'captured-frozen-acceleration' and sample['observedComparisonAvailable']
                    and count == sample['comparedBodies'] == sample['bodies'], 'unpaired gravity comparison')
            near(delta, sample['gravityVelocityRmsMetersPerSecond'] - sample['observedVelocityRmsMetersPerSecond'], 'gravity raw delta')
            baseline = sample['observedVelocityRmsMetersPerSecond']
            expected_ratio = nullable_ratio(sample['gravityVelocityRmsMetersPerSecond'], baseline)
            if expected_ratio is None or not math.isfinite(expected_ratio):
                require(ratio is None, 'undefined gravity ratio must be null')
            else:
                require(ratio is not None, 'missing defined gravity ratio')
                near(ratio, expected_ratio, 'gravity ratio')
            require(len(endpoint_delta) == 3, 'compared gravity lacks frame delta')
            observed_frame = sample.get('comparisonRawKrakensbaneFrameVelocity')
            require(isinstance(observed_frame, list) and len(observed_frame) == 3, 'missing observed frame velocity')
            for component, start, end in zip(endpoint_delta, sample['rawKrakensbaneFrameVelocity'], observed_frame):
                finite(end, 'observed frame velocity', -1e100, 1e100)
                near(component, end - start, 'endpoint frame delta')
            raw.append(sample)
            raw_deltas.append(delta)
            if ratio is not None:
                raw_ratios.append(ratio)
        else:
            require(count == 0 and delta == 0 and ratio is None and not endpoint_delta,
                    'unavailable gravity comparison contains paired results')
        require(not (gravity_adjusted or zero_adjusted) or available, 'adjusted metric lacks raw comparison')
        require(not gravity_adjusted or frame_status == 'compared', 'adjusted gravity status mismatch')
        require(not zero_adjusted or gravity_adjusted, 'adjusted zero lacks paired gravity')
        if gravity_adjusted and zero_adjusted:
            near(adjusted_delta, sample['gravityFrameAdjustedVelocityRmsMetersPerSecond']
                 - sample['zeroFrameAdjustedVelocityRmsMetersPerSecond'], 'adjusted gravity delta')
            adjusted.append(sample)
            adjusted_deltas.append(adjusted_delta)
        else:
            require(adjusted_delta == 0, 'unavailable adjusted pair has delta')
    raw_summary = adjusted_summary = None
    if raw:
        position = paired_stat(raw, 'gravityPositionMaxMeters', 'gravityPositionRmsMeters')
        velocity = paired_stat(raw, 'gravityVelocityMaxMetersPerSecond', 'gravityVelocityRmsMetersPerSecond')
        zero = paired_stat(raw, 'observedVelocityMaxMetersPerSecond', 'observedVelocityRmsMetersPerSecond')
        raw_summary = {'gravityPositionMeters': position, 'gravityVelocityMetersPerSecond': velocity,
                       'matchedZeroVelocityMetersPerSecond': zero,
                       'velocityRmsDeltaFromZero': velocity['rms'] - zero['rms'],
                       'velocityRmsRatioToZero': nullable_ratio(velocity['rms'], zero['rms']),
                       'sampleRmsDeltas': distribution(raw_deltas), 'sampleRmsRatios': distribution(raw_ratios),
                       'undefinedSampleRatios': len(raw) - len(raw_ratios)}
    if adjusted:
        gravity = paired_stat(adjusted, 'gravityFrameAdjustedVelocityMaxMetersPerSecond', 'gravityFrameAdjustedVelocityRmsMetersPerSecond')
        zero = paired_stat(adjusted, 'zeroFrameAdjustedVelocityMaxMetersPerSecond', 'zeroFrameAdjustedVelocityRmsMetersPerSecond')
        adjusted_summary = {'gravityMetersPerSecond': gravity, 'zeroMetersPerSecond': zero,
                            'rmsDeltaFromZero': gravity['rms'] - zero['rms'],
                            'sampleRmsDeltas': distribution(adjusted_deltas)}
    predicted_summary = None
    if predicted:
        gravity = paired_stat(predicted, 'gravityPredictedFrameVelocityMaxMetersPerSecond',
                              'gravityPredictedFrameVelocityRmsMetersPerSecond')
        zero = paired_stat(predicted, 'zeroPredictedFrameVelocityMaxMetersPerSecond',
                           'zeroPredictedFrameVelocityRmsMetersPerSecond')
        predicted_summary = {
            'strategy': data['framePredictionStrategy'],
            'scope': data['framePredictionScope'],
            'pairedSamples': len(predicted),
            'pairedBodyComparisons': sum(s['gravityComparedBodies'] for s in predicted),
            'gravityMetersPerSecond': gravity,
            'zeroMetersPerSecond': zero,
            'rmsDeltaFromZero': gravity['rms'] - zero['rms'],
            'frameDeltaErrorMetersPerSecond': distribution(prediction_errors),
        }
    return {'strategy': data['gravityStrategy'], 'execution': data['gravityExecution'],
            'interpretation': data['gravityInterpretation'], 'frameAdjustedScope': data['gravityFrameAdjustedScope'],
            'modelStatuses': dict(model_counts), 'comparisonStatuses': dict(comparison_counts),
            'frameAdjustedStatuses': dict(frame_counts), 'accelerationSources': dict(sources),
            'rawPairedSamples': len(raw), 'rawPairedBodyComparisons': sum(s['gravityComparedBodies'] for s in raw),
            'gravityAdjustedAvailableSamples': gravity_adjusted_count, 'zeroAdjustedAvailableSamples': zero_adjusted_count,
            'adjustedPairedSamples': len(adjusted), 'adjustedPairedBodyComparisons': sum(s['gravityComparedBodies'] for s in adjusted),
            'raw': raw_summary, 'adjustedVelocity': adjusted_summary,
            'predictedFrameVelocity': predicted_summary,
            'timingsMilliseconds': {key: distribution(values) for key, values in timings.items()},
            'aggregation': 'body-weighted RMS on matched samples; deltas compare like-for-like frames; adjusted results condition on observed future frame velocity'}


def summary(data):
    require(isinstance(data, dict) and data.get('schema') == 'ksp-continuum-flight-shadow/v2',
            'requires observed-comparison v2 receipt; v1 arithmetic is not observed physics')
    require(data.get('status') in TERMINAL, 'unsupported terminal capture status')
    evidence = text(data.get('evidence'), 'evidence')
    require(evidence in ('native-adapter-observation', 'portable-helper-fixture', 'managed-api-test-double'),
            'unsupported evidence provenance')
    for field in ('scope', 'framePolicy', 'comparisonScope', 'units'):
        text(data.get(field), field)
    strategy = data.get('workerStrategy', 'independent-constant-force/v1')
    require(strategy in ('independent-constant-force/v1', 'translational-rigid-cluster/v1'),
            'unsupported worker strategy')
    strategy_scope = data.get('workerStrategyScope')
    if strategy_scope is None:
        require(strategy == 'independent-constant-force/v1', 'missing worker strategy scope')
        strategy_scope = 'Legacy v2 receipt: each captured body advanced independently under its captured force.'
    text(strategy_scope, 'workerStrategyScope')
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
        require(sample['analyticAvailable'] or
                (sample['analyticMaxPositionError'] == 0 and sample['analyticMaxVelocityError'] == 0),
                'unavailable analytic result contains data')
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
        'workerStrategy': strategy, 'workerStrategyScope': strategy_scope,
        'installedComparisonQualified': False, 'solverAccuracyQualified': False,
        'counts': {'submitted': submitted, 'accepted': accepted, 'stale': stale,
                   'abandoned': submitted-accepted-stale},
        'compared': compared, 'comparisonSkipped': skipped, 'comparisonStatuses': dict(sorted(comparisons.items())),
        'residualSource': 'producer-reported sample residuals; raw observed body vectors are not in this receipt',
        'residuals': residuals, 'residualAggregation': 'body-weighted sample RMS; zero-force model discrepancy, not solver error',
        'timingsMilliseconds': {key: distribution(values) for key, values in timings.items()},
        'sampleBodyCounts': distribution(body_counts), 'physicalInput': physical,
        'wallSeconds': data['wallSeconds'], 'gravity': gravity_summary(data, samples),
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
