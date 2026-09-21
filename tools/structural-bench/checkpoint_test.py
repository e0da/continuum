"""Real native checkpoint consumer; equality is replay evidence, not physical truth."""
import json
import math
import subprocess
import sys
import unittest

BINARY = sys.argv.pop(1)


class CheckpointTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.r = json.loads(subprocess.check_output([BINARY], text=True, timeout=60))

    def test_active_checkpoint_and_scope(self):
        r = self.r
        self.assertEqual(r['schema'], 'ksp-continuum-checkpoint/v1')
        self.assertTrue(r['qualified'])
        self.assertTrue(r['savedExact'])
        self.assertTrue(r['restoredBytesEqual'])
        self.assertFalse(r['callbackReplayQualified'])
        self.assertIn('originalBodyIds', r)
        self.assertEqual(r['originalBodyIds'], r['coldBodyIds'])
        self.assertIn('compiler', r)
        self.assertGreater(r['actualStepSeconds'], 0)
        self.assertTrue(r['checkpointContact'])
        self.assertGreater(r['checkpointSpeed'], 1e-5)
        self.assertGreater(r['checkpointBytes'], 0)
        self.assertFalse(r['physicalAccuracyQualified'])
        self.assertFalse(r['crossPlatformReplayQualified'])
        self.assertFalse(r['gameIntegrated'])

    def test_actual_saved_replay_and_cold_measurement(self):
        r = self.r
        self.assertEqual(len(r['runs']), 3)
        original, saved, cold = [row['samples'] for row in r['runs']]
        self.assertEqual(len(original), 121)
        for run in (original, saved, cold):
            self.assertEqual(len(run), 121)
            for i, sample in enumerate(run):
                self.assertEqual(sample['step'], i)
                self.assertIn('active', sample)
                self.assertEqual(sample['active'], [True, True])
                self.assertEqual(len(sample['state']), 26)
                self.assertTrue(all(math.isfinite(v) for v in sample['state']))
                self.assertGreaterEqual(sample['updateMilliseconds'], 0)
                p, q = sample['state'][:3], sample['state'][13:16]
                start = run[0]['state']
                displacement = (p[0] + q[0] - start[0] - start[13]) / 2
                self.assertAlmostEqual(sample['centerXDisplacementM'], displacement, places=12)
                self.assertAlmostEqual(sample['constraintErrorM'], abs(math.dist(p, q) - 1.2), places=12)
        self.assertEqual([s['state'] for s in original], [s['state'] for s in saved])
        self.assertEqual(original[0]['state'], cold[0]['state'])
        delta = max(abs(x - y) for a, b in zip(original, cold)
                    for x, y in zip(a['state'], b['state']))
        self.assertEqual(r['coldMaxStateComponentDifference'], delta)
        self.assertEqual(r['coldExact'], delta == 0)
        fields = [('coldMaxPositionDifferenceM', 0, 3),
                  ('coldMaxVelocityDifferenceMps', 7, 3),
                  ('coldMaxAngularVelocityDifferenceRadps', 10, 3)]
        for field, offset, width in fields:
            expected = max(
                math.dist(a['state'][body + offset:body + offset + width],
                          b['state'][body + offset:body + offset + width])
                for a, b in zip(original, cold) for body in (0, 13))
            self.assertAlmostEqual(r[field], expected, places=12)
        quaternion = max(abs(a['state'][body + i] - b['state'][body + i])
                         for a, b in zip(original, cold)
                         for body in (0, 13) for i in range(3, 7))
        self.assertEqual(r['coldMaxQuaternionComponentDifference'], quaternion)


if __name__ == '__main__':
    unittest.main()
