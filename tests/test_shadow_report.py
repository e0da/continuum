"""Exercise the standalone observed-shadow receipt consumer."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/shadow_report.py'


def fixture():
    body = dict(id=0, nativeInstanceId=3, mass=1, constraints=0, sleeping=False,
                rotation=[0, 0, 0, 1], inertiaTensorRotation=[0, 0, 0, 1],
                forceSource='synthetic-zero-not-native-measurement')
    for key in ('position', 'velocity', 'angularVelocity', 'centerOfMass', 'worldCenterOfMass',
                'predictedPosition', 'predictedVelocity', 'force'):
        body[key] = [0, 0, 0]
    body['inertiaTensor'] = [1, 1, 1]
    sample = dict(tick=1, topologyGeneration=1, frameGeneration=1, status='accepted',
                  vesselId='00000000-0000-0000-0000-000000000001', body='Minmus', situation='LANDED',
                  parts=1, bodies=1, captureUnityFrame=1, collectUnityFrame=2, universalTime=1,
                  stepSeconds=.02, analyticAvailable=True, analyticMaxPositionError=0,
                  analyticMaxVelocityError=0, packed=False, warpRate=1,
                  referenceFrame='unity-world-at-capture', rawKrakensbaneFrameVelocity=[0, 0, 0],
                  comparisonRawKrakensbaneFrameVelocity=[0.1, 0.2, 0.3],
                  physicsEpoch=1, floatingOriginEventCount=0, observedComparisonAvailable=True,
                  observedPositionMaxMeters=.2, observedPositionRmsMeters=.2,
                  observedVelocityMaxMetersPerSecond=.4, observedVelocityRmsMetersPerSecond=.4,
                  comparedBodies=1, comparisonStatus='compared', comparisonPhysicsEpoch=2,
                  comparisonFloatingOriginEventCount=1,
                  comparisonUnityFrame=2, captureFixedTimeSeconds=1, comparisonFixedTimeSeconds=1.02,
                  observedDeltaSeconds=1.02-1)
    for key in ('captureMilliseconds', 'submitMilliseconds', 'collectMilliseconds',
                'collectAuditMilliseconds', 'handoffWallMilliseconds'):
        sample[key] = 1
    return dict(schema='ksp-continuum-flight-shadow/v2',
                physicalInputSchema='ksp-continuum-rigidbody-input/v1',
                referenceFrameSchema='ksp-continuum-unity-frame-context/v1',
                aggregateForceStatus='unavailable-not-captured', evidence='portable-helper-fixture',
                scope='Test fixture with zero-force worker model', framePolicy='test frame policy',
                comparisonScope='Observed forced dynamics minus zero-force model; not solver accuracy',
                units='test fixture units', status='complete', reason=None,
                unity='test-double', ksp='test-double', plugin='test', startedUtc='fixture',
                maxBodies=512, requestedSamples=1, submitted=1, accepted=1, stale=0,
                compared=1, comparisonSkipped=0, readyTimeoutSeconds=120,
                activeTimeoutSeconds=30, wallSeconds=1, originEvents=1, physicsEpochs=2,
                samples=[sample], firstAcceptedBatch=[body], firstAcceptedTick=1)


class ShadowReportTests(unittest.TestCase):
    def invoke(self, data=None, raw=None):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / 'receipt.json'
            output = Path(temporary) / 'summary.json'
            source.write_bytes(raw if raw is not None else json.dumps(data).encode())
            run = subprocess.run([sys.executable, str(SCRIPT), '--input', str(source),
                                  '--output', str(output)], capture_output=True, text=True)
            return run.returncode, json.loads(output.read_text()) if output.exists() else None

    def test_real_portable_helper_export(self):
        with tempfile.TemporaryDirectory() as temporary:
            receipt = Path(temporary) / 'helper.json'
            run = subprocess.run(['dotnet', 'run', '--project',
                                  str(ROOT / 'tests/KspContinuum.Shadow.Tests'), '-c', 'Release',
                                  '--', '--export-fixture', str(receipt)], cwd=ROOT,
                                 capture_output=True, text=True, timeout=60)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            code, report = self.invoke(json.loads(receipt.read_text()))
            self.assertEqual(code, 0)
            self.assertEqual(report['evidence'], 'portable-helper-fixture')
            self.assertEqual(report['compared'], 1)
            self.assertEqual(report['residuals']['bodyComparisons'], 2)
            self.assertEqual(report['residuals']['positionMeters']['maximum'], 4)
            self.assertAlmostEqual(report['residuals']['positionMeters']['rms'], (25/2)**.5)
            self.assertEqual(report['residuals']['velocityMetersPerSecond']['maximum'], 5)
            self.assertFalse(report['installedComparisonQualified'])

    def test_observed_residual_summary_and_provenance(self):
        code, report = self.invoke(fixture())
        self.assertEqual(code, 0)
        self.assertEqual(report['evidence'], 'portable-helper-fixture')
        self.assertFalse(report['installedComparisonQualified'])
        self.assertEqual(report['compared'], 1)
        self.assertEqual(report['residuals']['positionMeters']['maximum'], .2)
        self.assertAlmostEqual(report['residuals']['velocityMetersPerSecond']['rms'], .4)
        self.assertEqual(report['counts'], {'submitted': 1, 'accepted': 1, 'stale': 0, 'abandoned': 0})
        self.assertEqual(len(report['sourceSha256']), 64)

    def test_rms_aggregation_is_weighted_by_compared_bodies(self):
        data = fixture()
        second = copy.deepcopy(data['samples'][0])
        second.update(tick=2, bodies=2, parts=2, comparedBodies=2,
                      observedPositionMaxMeters=.3, observedPositionRmsMeters=.3)
        data['samples'].append(second)
        data.update(submitted=2, accepted=2, compared=2, requestedSamples=2)
        code, report = self.invoke(data)
        self.assertEqual(code, 0)
        self.assertEqual(report['residuals']['bodyComparisons'], 3)
        self.assertAlmostEqual(report['residuals']['positionMeters']['rms'], ((.2**2 + 2*.3**2)/3)**.5)
        self.assertEqual(report['residuals']['positionMeters']['maximum'], .3)

    def test_malformed_comparison_rejected(self):
        for field, value in [('comparedBodies', True), ('observedPositionRmsMeters', 2),
                             ('observedPositionMaxMeters', -1), ('comparisonPhysicsEpoch', 1),
                             ('observedComparisonAvailable', 1), ('comparisonStatus', 'invented')]:
            with self.subTest(field=field):
                data = fixture()
                data['samples'][0][field] = value
                self.assertEqual(self.invoke(data), (1, None))
        data = fixture()
        data['compared'] = 0
        self.assertEqual(self.invoke(data), (1, None))

    def test_no_comparison_is_unavailable_not_zero(self):
        data = fixture()
        data['compared'] = 0
        data['comparisonSkipped'] = 1
        sample = data['samples'][0]
        sample['comparisonStatus'] = 'skipped-context-change'
        sample['observedComparisonAvailable'] = False
        sample['comparedBodies'] = 0
        for key in ('observedPositionMaxMeters', 'observedPositionRmsMeters',
                    'observedVelocityMaxMetersPerSecond', 'observedVelocityRmsMetersPerSecond'):
            sample[key] = 0
        code, report = self.invoke(data)
        self.assertEqual(code, 0)
        self.assertIsNone(report['residuals'])
        self.assertEqual(report['comparisonSkipped'], 1)

    def test_skipped_resolution_and_clock_rollback_remain_diagnostic(self):
        data = fixture()
        data['compared'] = 0
        data['comparisonSkipped'] = 1
        sample = data['samples'][0]
        sample['comparisonStatus'] = 'skipped-timestep-resolution'
        sample['observedComparisonAvailable'] = False
        sample['comparedBodies'] = 0
        sample['observedDeltaSeconds'] = -1
        sample['comparisonFixedTimeSeconds'] = 0
        for key in ('observedPositionMaxMeters', 'observedPositionRmsMeters',
                    'observedVelocityMaxMetersPerSecond', 'observedVelocityRmsMetersPerSecond'):
            sample[key] = 0
        code, report = self.invoke(data)
        self.assertEqual(code, 0)
        self.assertIsNone(report['residuals'])

    def test_inconsistent_observation_time_is_rejected(self):
        for key, value in [('observedDeltaSeconds', .04), ('comparisonFixedTimeSeconds', 1.04)]:
            with self.subTest(field=key):
                data = fixture()
                data['samples'][0][key] = value
                self.assertEqual(self.invoke(data), (1, None))

    def test_existing_output_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / 'receipt.json'
            output = Path(temporary) / 'summary.json'
            source.write_text(json.dumps(fixture()))
            output.write_bytes(b'keep')
            run = subprocess.run([sys.executable, str(SCRIPT), '--input', str(source),
                                  '--output', str(output)], capture_output=True)
            self.assertNotEqual(run.returncode, 0)
            self.assertEqual(output.read_bytes(), b'keep')

    def test_v1_arithmetic_is_not_observed_physics(self):
        code, output = self.invoke({'schema': 'ksp-continuum-flight-shadow/v1'})
        self.assertEqual(code, 1)
        self.assertIsNone(output)

    def test_oversize_and_nonfinite_input(self):
        for raw in (b' ' * (4 * 1024 * 1024 + 1), b'{"schema":NaN}',
                    b'{"schema":"x","schema":"y"}'):
            with self.subTest(size=len(raw)):
                self.assertEqual(self.invoke(raw=raw), (1, None))


if __name__ == '__main__':
    unittest.main()
