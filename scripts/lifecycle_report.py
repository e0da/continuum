#!/usr/bin/env python3
"""Summarize bounded lifecycle observations without inferring global callback order."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import sys

MAX_BYTES = 4 * 1024 * 1024
STAGES = {'TimingManager.FashionablyLate', 'TimingManager.FlightIntegrator',
          'TimingManager.BetterLateThanNever', 'Host.Update', 'Host.FixedUpdate',
          'Host.WaitForFixedUpdate'}
STATUSES = {'not-started', 'arming', 'running', 'unavailable', 'timeout', 'invalidated',
            'bounded', 'invalid', 'interrupted'}
CLEANUP = {'registered-readback-confirmed', 'removed-owned-callbacks',
           'owner-destroyed', 'cleanup-error', 'not-registered'}
FORCE_SCOPE = 'raw part component census; gravity, stock aerodynamics, contact impulses and direct Rigidbody writes not reconstructed'
ORDER_SCOPE = 'observed session sequence; host FixedUpdate counter is not a certified physics-step identity; terminal cycle may be partial'


class ReportError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ReportError(message)


def integer(value, label, low=0, high=2**63-1):
    require(type(value) is int and low <= value <= high, label + ' must be a bounded integer')
    return value


def number(value, label, low=-math.inf, high=math.inf):
    require(type(value) in (float, int), label + ' must be numeric')
    try:
        finite = math.isfinite(value)
    except OverflowError:
        finite = False
    require(finite and low <= value <= high, label + ' must be finite and in bounds')
    return value


def text(value, label, nullable=False):
    if value is None and nullable:
        return value
    require(isinstance(value, str) and 0 < len(value) <= 4096, label + ' must be bounded text')
    return value


def identity(value, nullable=False):
    if value is None and nullable:
        return value
    require(isinstance(value, str) and re.fullmatch(
        r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', value), 'invalid identity')
    return value


def vector(value):
    require(isinstance(value, dict) and set(value) == {'X', 'Y', 'Z'}, 'invalid vector')
    for component in value.values():
        number(component, 'vector component')


def parts_count(parts):
    require(isinstance(parts, list) and len(parts) <= 512, 'invalid parts array')
    ids, native_ids, holders = set(), set(), 0
    for sample in parts:
        require(isinstance(sample, dict) and isinstance(sample.get('census'), dict), 'invalid census')
        p = sample['census']
        pid = integer(p.get('flightId'), 'flightId', 1, 2**32-1)
        native = integer(p.get('nativePartInstanceId'), 'native part', -2**31, 2**31-1)
        require(pid not in ids and native not in native_ids, 'duplicate part')
        ids.add(pid)
        native_ids.add(native)
        require(integer(p.get('parentFlightId'), 'parent', 0, 2**32-1) != pid, 'self parent')
        if p.get('rigidBodyPartFlightId') is not None:
            integer(p['rigidBodyPartFlightId'], 'physical owner', 1, 2**32-1)
        require('nativeRigidbodyInstanceId' in p, 'missing body availability')
        available = p['nativeRigidbodyInstanceId'] is not None
        if available:
            integer(p['nativeRigidbodyInstanceId'], 'native body', -2**31, 2**31-1)
        for field in ('force', 'torque'):
            vector(p.get(field))
        for record, fields in ((p, ('worldCenterOfMass',)),
                               (sample, ('position', 'velocity', 'angularVelocity', 'rotationXYZ', 'rotationW'))):
            for field in fields:
                require(field in record and (record[field] is not None) == available, 'inconsistent pose availability')
                if available:
                    number(record[field], field) if field == 'rotationW' else vector(record[field])
        forces = p.get('forces')
        require(isinstance(forces, list) and len(forces) <= 64, 'invalid force holders')
        holders += len(forces)
        for force in forces:
            require(isinstance(force, dict), 'invalid holder')
            vector(force.get('force'))
            vector(force.get('worldPosition'))
            require('worldLeverArm' in force and (force['worldLeverArm'] is not None) == available,
                    'inconsistent lever arm')
            if available:
                vector(force['worldLeverArm'])
    for sample in parts:
        p = sample['census']
        require(p['parentFlightId'] == 0 or p['parentFlightId'] in ids, 'missing parent')
        require(p.get('rigidBodyPartFlightId') is None or p['rigidBodyPartFlightId'] in ids, 'missing physical owner')
    return len(parts), holders


def captured_signature(context, parts):
    fields = ('scene', 'mainBodyInstanceId', 'vesselId', 'frameKey',
              'topologyGeneration', 'frameGeneration', 'originEvents')
    frame = tuple(context[key] for key in fields)
    velocity = tuple(context['frameVelocity'][axis] for axis in ('X', 'Y', 'Z'))
    ownership = tuple(tuple(sample['census'].get(key) for key in (
        'flightId', 'parentFlightId', 'rigidBodyPartFlightId',
        'nativePartInstanceId', 'nativeRigidbodyInstanceId')) for sample in parts)
    return frame, velocity, ownership


def summarize(data):
    require(isinstance(data, dict) and data.get('schema') == 'ksp-continuum-lifecycle-trace/v1', 'unsupported schema')
    for key in ('sessionId', 'nativeAssemblyMvid', 'unityAssemblyMvid', 'pluginAssemblyMvid'):
        identity(data.get(key))
    require(data.get('evidence') in ('native-adapter-observation', 'managed-api-test-double'), 'unsupported evidence')
    require(data.get('status') in STATUSES and data.get('cleanupStatus') in CLEANUP, 'invalid lifecycle status')
    require(data.get('physicsWrites') is False and data.get('installedOrderQualified') is False, 'unsupported authority claim')
    require(data.get('forceScope') == FORCE_SCOPE and data.get('orderingScope') == ORDER_SCOPE, 'unsupported observation scope')
    text(data.get('detail'), 'detail', nullable=True)
    events = data.get('events')
    require(isinstance(events, list) and len(events) <= 8192, 'invalid events')
    require(integer(data.get('completedEvents'), 'completedEvents', 0, 8192) == len(events), 'event count mismatch')
    host_total = integer(data.get('hostFixedObservations'), 'host observations', 0, 120)
    byte_count = integer(data.get('encodedEventBytes'), 'encodedEventBytes', 0, 3*1024*1024)
    require(integer(data.get('maxEncodedEventBytes'), 'byte bound') == 3*1024*1024, 'unsupported byte bound')
    require(bool(events) == (byte_count > 0), 'encoded byte count inconsistent with events')
    elapsed = number(data.get('elapsedWallSeconds'), 'elapsed wall', 0)
    skipped = integer(data.get('skippedArmingCallbacks'), 'arming callbacks', 0, 2**31-1)
    totals = {key: integer(data.get(key), key) for key in ('topologyGeneration', 'frameGeneration', 'originEvents')}
    previous = {'wallSeconds': 0, 'hostFixedObservations': 0,
                'topologyGeneration': 0, 'frameGeneration': 0, 'originEvents': 0}
    thread = None
    groups = {}
    counts = Counter()
    part_total = holder_total = 0
    captured_context = None
    for index, event in enumerate(events, 1):
        require(isinstance(event, dict) and isinstance(event.get('context'), dict), 'invalid event')
        c = event['context']
        require(c.get('sessionId') == data['sessionId'], 'mixed session')
        require(integer(c.get('sequence'), 'sequence', 1) == index, 'noncontiguous sequence')
        require(c.get('stage') in STAGES, 'unsupported stage')
        current_thread = integer(c.get('threadId'), 'thread', 1, 2**31-1)
        require(thread is None or thread == current_thread, 'mixed observer threads')
        thread = current_thread
        identity(c.get('vesselId'), nullable=True)
        text(c.get('scene'), 'scene')
        text(c.get('frameKey'), 'frameKey')
        integer(c.get('unityFrame'), 'unityFrame', 0, 2**31-1)
        integer(c.get('mainBodyInstanceId'), 'main body', -2**31, 2**31-1)
        for key in ('topologyGeneration', 'frameGeneration', 'originEvents'):
            value = integer(c.get(key), key, 0 if key == 'originEvents' else 1, totals[key])
            require(value >= previous[key], 'generation moved backwards')
            previous[key] = value
        host = integer(c.get('hostFixedObservations'), 'host observation', previous['hostFixedObservations'], host_total)
        expected = previous['hostFixedObservations'] + (c['stage'] == 'Host.FixedUpdate')
        require(host == expected, 'host observation counter inconsistent with retained stages')
        previous['hostFixedObservations'] = host
        wall = number(c.get('wallSeconds'), 'event wall', previous['wallSeconds'], elapsed)
        require(wall < 30, 'event exceeds capture wall bound')
        previous['wallSeconds'] = wall
        for key in ('universalTime', 'fixedTimeSeconds'):
            number(c.get(key), key)
        require(number(c.get('stepSeconds'), 'step', 0) > 0, 'nonpositive step')
        for key in ('warpRate', 'timeScale'):
            number(c.get(key), key, 0)
        for key in ('loaded', 'packed', 'holdPhysics', 'paused', 'eligible'):
            require(type(c.get(key)) is bool, 'invalid context boolean')
        vector(c.get('frameVelocity'))
        require(event.get('sampleStatus') in ('captured-component-only', 'invalidated-no-census'), 'invalid sample status')
        if event['sampleStatus'] == 'invalidated-no-census':
            require(index == len(events) and data['status'] == 'invalidated' and event.get('parts') == [], 'invalid invalidation event')
        else:
            require(c['eligible'] and c['scene'] == 'FLIGHT' and c['vesselId'] is not None
                    and c['loaded'] and not c['packed'] and not c['holdPhysics'] and not c['paused']
                    and c['warpRate'] == 1 and c['timeScale'] == 1, 'ineligible census')
        parts, holders = parts_count(event.get('parts'))
        if event['sampleStatus'] == 'captured-component-only':
            require(parts > 0, 'empty captured census')
            signature = captured_signature(c, event['parts'])
            require(captured_context is None or signature == captured_context,
                    'captured context or topology changed without invalidation')
            captured_context = signature
        part_total += parts
        holder_total += holders
        counts[c['stage']] += 1
        groups.setdefault(host, []).append(c['stage'])
    require(host_total >= previous['hostFixedObservations'], 'terminal host counter moved backwards')
    require(part_total == integer(data.get('retainedParts'), 'retainedParts', 0, 8192), 'retained part mismatch')
    require(holder_total == integer(data.get('retainedHolders'), 'retainedHolders', 0, 16384), 'retained holder mismatch')
    variants = Counter(tuple(stages) for stages in groups.values())
    return {
        'schema': 'ksp-continuum-lifecycle-summary/v1',
        **{key: data[key] for key in ('sessionId', 'nativeAssemblyMvid', 'unityAssemblyMvid', 'pluginAssemblyMvid',
                                    'evidence', 'status', 'cleanupStatus', 'forceScope', 'orderingScope')},
        'detail': data.get('detail'), 'physicsWrites': False, 'installedOrderQualified': False,
        'physicsStepGroupingQualified': False, 'validation': 'bounded receipt structure and observed counters; not installed behavior',
        'threadId': thread, 'elapsedWallSeconds': elapsed, 'observedEvents': len(events),
        'skippedArmingCallbacks': skipped, 'hostFixedObservations': host_total, 'retainedParts': part_total, 'retainedHolders': holder_total,
        'declaredEncodedEventBytes': byte_count, 'generationTotals': totals,
        'stageCounts': dict(sorted(counts.items())),
        'hostObservationGroups': [{'hostFixedObservation': host, 'stages': stages} for host, stages in groups.items()],
        'observedOrderVariants': [{'stages': list(stages), 'groupCount': count} for stages, count in variants.items()],
        'unavailableForceSources': ['gravity', 'stock-aerodynamics', 'contact-impulses', 'direct-rigidbody-writes'],
    }


def reject_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON key')
        result[key] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        with args.input.open('rb') as source:
            raw = source.read(MAX_BYTES + 1)
        require(len(raw) <= MAX_BYTES, 'input exceeds 4 MiB')
        data = json.loads(raw, object_pairs_hook=reject_duplicates)
        report = summarize(data)
        report['sourceSha256'] = hashlib.sha256(raw).hexdigest()
        payload = json.dumps(report, indent=2, allow_nan=False) + '\n'
        with args.output.open('x', encoding='utf-8') as output:
            output.write(payload)
        return 0
    except (OSError, ValueError, TypeError, RecursionError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
