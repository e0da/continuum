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
    @classmethod
    def setUpClass(cls):
        with tempfile.TemporaryDirectory() as temporary:
            receipt = Path(temporary) / 'gravity.json'
            run = subprocess.run(['dotnet', 'run', '--project',
                                  str(ROOT / 'tests/KspContinuum.Shadow.Tests'), '-c', 'Release',
                                  '--', '--export-gravity-fixture', str(receipt)], cwd=ROOT,
                                 capture_output=True, text=True, timeout=60)
            if run.returncode:
                raise RuntimeError(run.stdout + run.stderr)
            cls.gravity_fixture = json.loads(receipt.read_text())

    def test_real_gravity_paired_raw_and_adjusted_summary(self):
        code, report = self.invoke(self.gravity_fixture)
        self.assertEqual(code, 0)
        self.assertIsNotNone(report.get('gravity'))
        gravity = report['gravity']
        self.assertEqual(gravity['rawPairedSamples'], 1)
        self.assertEqual(gravity['adjustedPairedSamples'], 1)
        self.assertAlmostEqual(gravity['raw']['gravityVelocityMetersPerSecond']['rms'], .2)
        self.assertAlmostEqual(gravity['raw']['velocityRmsDeltaFromZero'], .2)
        self.assertIsNone(gravity['raw']['velocityRmsRatioToZero'])
        self.assertAlmostEqual(gravity['adjustedVelocity']['zeroMetersPerSecond']['rms'], .2)
        self.assertEqual(gravity['adjustedVelocity']['gravityMetersPerSecond']['rms'], 0)
        self.assertAlmostEqual(gravity['adjustedVelocity']['rmsDeltaFromZero'], -.2)
        predicted = gravity['predictedFrameVelocity']
        self.assertEqual(predicted['strategy'], 'krakensbane-last-correction-persistence/v1')
        self.assertEqual(predicted['gravityMetersPerSecond']['rms'], 0)
        self.assertAlmostEqual(predicted['zeroMetersPerSecond']['rms'], .2)
        self.assertEqual(predicted['frameDeltaErrorMetersPerSecond']['maximum'], 0)
        self.assertEqual(gravity['accelerationSources'], {'analytic-fixture': 1})
        self.assertIn('gravityPredictionMilliseconds', gravity['timingsMilliseconds'])
        self.assertFalse(report['solverAccuracyQualified'])

    def test_gravity_aggregate_compares_matched_weighted_populations(self):
        data = copy.deepcopy(self.gravity_fixture)
        second = copy.deepcopy(data['samples'][0])
        second.update(tick=2, parts=2, bodies=2, comparedBodies=2, gravityComparedBodies=2,
                      observedVelocityMaxMetersPerSecond=1, observedVelocityRmsMetersPerSecond=1,
                      gravityVelocityMaxMetersPerSecond=.6, gravityVelocityRmsMetersPerSecond=.6,
                      gravityVelocityRmsDeltaFromZero=-.4, gravityVelocityRmsRatioToZero=.6,
                      zeroFrameAdjustedVelocityMaxMetersPerSecond=.5,
                      zeroFrameAdjustedVelocityRmsMetersPerSecond=.5,
                      gravityFrameAdjustedVelocityMaxMetersPerSecond=.2,
                      gravityFrameAdjustedVelocityRmsMetersPerSecond=.2,
                      gravityFrameAdjustedVelocityRmsDeltaFromZero=-.3)
        data['samples'].append(second)
        data.update(submitted=2, accepted=2, compared=2)
        code, report = self.invoke(data)
        self.assertEqual(code, 0)
        gravity = report['gravity']
        self.assertEqual(gravity['rawPairedBodyComparisons'], 3)
        expected_gravity = ((.2**2 + 2*.6**2)/3)**.5
        expected_zero = (2/3)**.5
        self.assertAlmostEqual(gravity['raw']['gravityVelocityMetersPerSecond']['rms'], expected_gravity)
        self.assertAlmostEqual(gravity['raw']['velocityRmsDeltaFromZero'], expected_gravity - expected_zero)
        self.assertAlmostEqual(gravity['raw']['velocityRmsRatioToZero'], expected_gravity / expected_zero)
        self.assertEqual(gravity['raw']['undefinedSampleRatios'], 1)
        self.assertAlmostEqual(gravity['adjustedVelocity']['rmsDeltaFromZero'],
                               (2*.2**2/3)**.5 - ((.2**2+2*.5**2)/3)**.5)

    def test_overflowing_gravity_ratio_stays_unavailable(self):
        data = copy.deepcopy(self.gravity_fixture)
        data['samples'][0]['observedVelocityMaxMetersPerSecond'] = 1e-320
        data['samples'][0]['observedVelocityRmsMetersPerSecond'] = 1e-320
        code, report = self.invoke(data)
        self.assertEqual(code, 0)
        self.assertIsNone(report['gravity']['raw']['velocityRmsRatioToZero'])

    def test_legacy_v2_has_no_gravity_section(self):
        code, report = self.invoke(fixture())
        self.assertEqual(code, 0)
        self.assertIsNone(report.get('gravity'))

    def test_malformed_gravity_section_is_rejected(self):
        changes = [
            ('gravityComparisonAvailable', 1), ('gravityComparedBodies', True),
            ('gravityPositionRmsMeters', 100), ('gravityMu', -1),
            ('gravityComparisonStatus', 'not-accepted'),
            ('gravityVelocityRmsDeltaFromZero', 99), ('gravityVelocityRmsRatioToZero', 1),
            ('gravityFrameAdjustedVelocityRmsDeltaFromZero', 99),
            ('zeroFrameAdjustedVelocityAvailable', False),
            ('gravityEndpointFrameVelocityDelta', [99, 0, 0]),
            ('predictedKrakensbaneFrameVelocityDelta', [99, 0, 0]),
            ('frameVelocityDeltaPredictionError', [99, 0, 0]),
            ('gravityPredictionMilliseconds', -1),
        ]
        for field, value in changes:
            with self.subTest(field=field):
                data = copy.deepcopy(self.gravity_fixture)
                data['samples'][0][field] = value
                self.assertEqual(self.invoke(data), (1, None))
        data = copy.deepcopy(self.gravity_fixture)
        del data['gravityStrategy']
        self.assertEqual(self.invoke(data), (1, None))

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
