"""Outside-in validation of the bounded lifecycle receipt consumer."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/lifecycle_report.py'


class LifecycleReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Path(temporary) / 'native-export.json'
            run = subprocess.run(['dotnet', 'run', '--project',
                                  str(ROOT / 'tests/KspContinuum.LifecycleTrace.Tests'),
                                  '-c', 'Release', '--', '--export-fixture', str(fixture)],
                                 cwd=ROOT, capture_output=True, text=True, timeout=60)
            if run.returncode:
                raise RuntimeError(run.stdout + run.stderr)
            cls.fixture = json.loads(fixture.read_text())

    def test_real_adapter_fixture_and_order_variants(self):
        code, r = self.invoke(self.fixture)
        self.assertEqual(code, 0)
        self.assertEqual(r['schema'], 'ksp-continuum-lifecycle-summary/v1')
        self.assertEqual(r['evidence'], 'managed-api-test-double')
        self.assertFalse(r['installedOrderQualified'])
        self.assertFalse(r['physicsStepGroupingQualified'])
        self.assertEqual(r['observedEvents'], 6)
        self.assertIn('skippedArmingCallbacks', r)
        self.assertEqual(r['skippedArmingCallbacks'], self.fixture['skippedArmingCallbacks'])
        self.assertEqual(sum(r['stageCounts'].values()), 6)
        self.assertEqual(len(r['hostObservationGroups']), 2)
        changed = copy.deepcopy(self.fixture)
        events = changed['events']
        events[2]['context']['stage'], events[3]['context']['stage'] = (
            events[3]['context']['stage'], events[2]['context']['stage'])
        code, other = self.invoke(changed)
        self.assertEqual(code, 0)
        self.assertNotEqual(r['observedOrderVariants'], other['observedOrderVariants'])

    def test_terminal_status_is_preserved(self):
        for status in ('bounded', 'timeout', 'invalid', 'interrupted'):
            with self.subTest(status=status):
                data = copy.deepcopy(self.fixture)
                data['status'] = status
                data['detail'] = 'retained diagnostic result'
                if status == 'timeout':
                    data['elapsedWallSeconds'] = 30
                code, r = self.invoke(data)
                self.assertEqual(code, 0)
                self.assertEqual(r['status'], status)
                self.assertFalse(r['installedOrderQualified'])
        data = copy.deepcopy(self.fixture)
        last = data['events'][-1]
        data['retainedParts'] -= len(last['parts'])
        data['retainedHolders'] -= sum(len(p['census']['forces']) for p in last['parts'])
        last['parts'] = []
        last['sampleStatus'] = 'invalidated-no-census'
        data['status'] = 'invalidated'
        code, r = self.invoke(data)
        self.assertEqual(code, 0)
        self.assertEqual(r['status'], 'invalidated')

    def test_malformed_fields_fail_without_publication(self):
        mutations = [
            (('completedEvents',), True), (('skippedArmingCallbacks',), True),
            (('events', 1, 'context', 'loaded'), False), (('retainedParts',), 999),
            (('hostFixedObservations',), 121), (('encodedEventBytes',), 3145729),
            (('installedOrderQualified',), True), (('physicsWrites',), 0),
            (('events', 1, 'context', 'sessionId'), 'foreign'),
            (('events', 1, 'context', 'sequence'), 99),
            (('events', 1, 'context', 'threadId'), 999),
            (('events', 1, 'context', 'stage'), 'invented'),
            (('events', 1, 'context', 'wallSeconds'), float('nan')),
            (('events', 1, 'context', 'wallSeconds'), 30),
            (('events', 1, 'context', 'frameGeneration'), 999),
            (('events', 1, 'context', 'hostFixedObservations'), 0),
            (('events', 1, 'context', 'stepSeconds'), 0),
            (('events', 1, 'parts', 0, 'velocity'), None),
        ]
        for path, value in mutations:
            with self.subTest(path=path):
                data = copy.deepcopy(self.fixture)
                target = data
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = value
                code, output = self.invoke(data)
                self.assertEqual(code, 1)
                self.assertIsNone(output)

    def test_captured_context_change_and_empty_census_are_rejected(self):
        changes = [
            ('vesselId', '00000000-0000-0000-0000-000000000001'),
            ('frameKey', 'different-frame'), ('mainBodyInstanceId', 998),
            ('frameVelocity', {'X': 1, 'Y': 0, 'Z': 0}),
            ('topologyGeneration', 2), ('frameGeneration', 2), ('originEvents', 1),
        ]
        for key, value in changes:
            with self.subTest(context=key):
                data = copy.deepcopy(self.fixture)
                data['events'][-1]['context'][key] = value
                if key in ('topologyGeneration', 'frameGeneration', 'originEvents'):
                    data[key] = value
                code, output = self.invoke(data)
                self.assertEqual(code, 1)
                self.assertIsNone(output)
        data = copy.deepcopy(self.fixture)
        data['events'][-1]['parts'][0]['census']['nativePartInstanceId'] += 1
        with self.subTest(topology='nativePartInstanceId'):
            self.assertEqual(self.invoke(data), (1, None))
        data = copy.deepcopy(self.fixture)
        last = data['events'][-1]
        data['retainedParts'] -= len(last['parts'])
        data['retainedHolders'] -= sum(len(p['census']['forces']) for p in last['parts'])
        last['parts'] = []
        with self.subTest(census='empty'):
            self.assertEqual(self.invoke(data), (1, None))

    def test_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / 'trace.json'
            output = Path(temporary) / 'summary.json'
            source.write_text(json.dumps(self.fixture))
            output.write_bytes(b'keep me')
            result = subprocess.run([sys.executable, str(SCRIPT), '--input', str(source),
                                     '--output', str(output)], capture_output=True)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(output.read_bytes(), b'keep me')

    def invoke(self, data=None, raw=None):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / 'trace.json'
            output = Path(temporary) / 'summary.json'
            source.write_bytes(raw if raw is not None else json.dumps(data).encode())
            result = subprocess.run([sys.executable, str(SCRIPT), '--input', str(source),
                                     '--output', str(output)], capture_output=True, text=True)
            return result.returncode, json.loads(output.read_text()) if output.exists() else None

    def test_invalid_schema_is_rejected_without_output(self):
        code, output = self.invoke({'schema': 'unrelated'})
        self.assertEqual(code, 1)
        self.assertIsNone(output)

    def test_oversized_input_is_rejected_without_output(self):
        code, output = self.invoke(raw=b' ' * (4 * 1024 * 1024 + 1))
        self.assertEqual(code, 1)
        self.assertIsNone(output)


if __name__ == '__main__':
    unittest.main()
