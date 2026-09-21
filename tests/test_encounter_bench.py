"""Exercise the real portable planner through its executable consumer."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'tools/KspContinuum.EncounterBench'


class EncounterBenchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release'],
                               cwd=ROOT, capture_output=True, text=True, timeout=60)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.binary = PROJECT / 'bin/Release/net8.0/KspContinuum.EncounterBench.dll'

    def run_bench(self, *args):
        return subprocess.run(['dotnet', str(self.binary), *map(str, args)], cwd=ROOT,
                              capture_output=True, text=True, timeout=30)

    def test_stops_crossing_while_quiet_objects_keep_their_horizon(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'receipt.json'
            run = self.run_bench('--quiet', 32, '--samples', 2, '--output', output)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            report = json.loads(output.read_text())
            self.assertEqual('ksp-continuum-encounter-bench/v1', report['schema'])
            self.assertTrue(report['qualified'])
            cases = {row['name']: row for row in report['cases']}
            self.assertIn('mixed-fast-crossing', cases)
            self.assertIn('curved-interior-crossing', cases)
            curved = cases['curved-interior-crossing']
            self.assertEqual('complete', curved['status'])
            self.assertEqual(32, curved['fullHorizonBodies'])
            self.assertEqual(2, curved['refinedBodies'])
            self.assertLessEqual(curved['minimumAdvanceSeconds'], 10 - 2 ** .5)
            self.assertGreater(curved['minimumAdvanceSeconds'], 8.58)
            self.assertEqual([0, 2, 0], curved['inputs'][0]['nominalAcceleration'])
            self.assertEqual(20, cases['curved-clear']['minimumAdvanceSeconds'])
            cross_axis = cases["sparse-cross-axis"]
            self.assertEqual(32, cross_axis["fullHorizonBodies"])
            self.assertLess(cross_axis["plan"]["work"]["pairTests"], 32 * 4)
            mixed = cases['mixed-fast-crossing']
            self.assertEqual(34, mixed['bodyCount'])
            self.assertEqual(32, mixed['fullHorizonBodies'])
            self.assertEqual(2, mixed['refinedBodies'])
            self.assertLessEqual(mixed['minimumAdvanceSeconds'], 9.998)
            self.assertGreaterEqual(mixed['minimumAdvanceSeconds'], 9.9979)
            self.assertEqual(20, mixed['maximumAdvanceSeconds'])
            self.assertTrue(report['permutationStable'])
            self.assertTrue(report['stalePlanRejected'])
            self.assertTrue(report['foreignPlanRejected'])
            self.assertFalse(report['physicsIntegrated'])
            self.assertFalse(report['parallelExecutionQualified'])
            dense = cases['dense-budget-exhaustion']
            self.assertEqual('budget-exhausted', dense['status'])
            self.assertEqual(0, dense['maximumAdvanceSeconds'])
            for name in ('tangent', 'near-miss', 'acceleration-uncertainty', 'zero-lookahead'):
                self.assertIn(name, cases)
            self.assertEqual(0, cases['zero-lookahead']['maximumAdvanceSeconds'])
            for row in report['measurements']:
                self.assertEqual(2, len(row['milliseconds']))
                self.assertTrue(all(x >= 0 for x in row['milliseconds']))
            original = output.read_bytes()
            duplicate = self.run_bench('--quiet', 32, '--samples', 2, '--output', output)
            self.assertNotEqual(0, duplicate.returncode)
            self.assertEqual(original, output.read_bytes())

    def test_rejects_unbounded_or_invalid_workload(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'receipt.json'
            for args in (('--quiet', '1000000000'), ('--samples', '0'), ('--bogus', '1')):
                run = self.run_bench(*args, '--output', output)
                self.assertNotEqual(0, run.returncode)
                self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
