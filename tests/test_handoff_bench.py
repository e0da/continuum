"""Exercise the real portable handoff through its executable consumer."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'tools/KspContinuum.HandoffBench'


class HandoffBenchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release'],
                               cwd=ROOT, capture_output=True, text=True, timeout=60)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.binary = PROJECT / 'bin/Release/net8.0/KspContinuum.HandoffBench.dll'

    def run_bench(self, *args):
        return subprocess.run(['dotnet', str(self.binary), *map(str, args)], cwd=ROOT,
                              capture_output=True, text=True, timeout=30)

    def test_actual_contact_commit_frame_invariance_and_replay(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'receipt.json'
            run = self.run_bench('--output', output)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            r = json.loads(output.read_text())
            self.assertEqual('ksp-continuum-handoff-bench/v1', r['schema'])
            self.assertTrue(r['qualified'])
            self.assertFalse(r['gameIntegrated'])
            self.assertFalse(r['asynchronousIslandsQualified'])
            self.assertTrue(all(r['checks'].values()))
            self.assertEqual(3, len(r['runs']))
            for row in r['runs']:
                self.assertEqual('Committed', row['status'])
                self.assertAlmostEqual(9, row['contactSeconds'])
                self.assertEqual(9, row['after']['offset'])
                self.assertEqual(3, len(row['after']['bodies']))
                self.assertEqual(2, row['solverBodies'])
                self.assertTrue(row['replayEquivalent'])
                self.assertIn('resumedOffset', row)
                self.assertEqual(11, row['resumedOffset'])
                self.assertEqual([-5, 0, 0], row['resumedNormalizedPositions'][0])
                self.assertEqual([1, 0, 0], row['resumedNormalizedPositions'][1])
                self.assertEqual([-2, 0, 0], row['normalizedVelocities'][0])
                self.assertEqual([0, 0, 0], row['normalizedVelocities'][1])
            original = output.read_bytes()
            repeat = self.run_bench('--output', output)
            self.assertNotEqual(0, repeat.returncode)
            self.assertEqual(original, output.read_bytes())

    def test_bad_arguments_do_not_create_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'receipt.json'
            for args in ((), ('--bogus', output), ('--output',)):
                run = self.run_bench(*args)
                self.assertNotEqual(0, run.returncode)
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
