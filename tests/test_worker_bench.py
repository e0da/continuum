"""Exercise the runnable worker boundary, not a mocked benchmark report."""
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'tools/KspContinuum.WorkerBench'


class WorkerBenchTests(unittest.TestCase):
    def test_compares_serial_and_parallel_worker_with_analytic_motion(self):
        run = subprocess.run(
            ['dotnet', 'run', '--project', str(PROJECT), '-c', 'Release', '--',
             '--bodies', '32', '--samples', '4'], capture_output=True, text=True,
            cwd=ROOT, timeout=60)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        report = json.loads(run.stdout)
        self.assertEqual(report['schema'], 'ksp-continuum-worker-bench/v1')
        self.assertEqual(report['bodies'], 32)
        self.assertEqual(report['samples'], 4)
        self.assertFalse(report['stockPhysicsSpeedupMeasured'])
        self.assertEqual({r['strategy'] for r in report['results']},
                         {'serial-inline', 'serial-worker', 'parallel-worker'})
        for row in report['results']:
            self.assertEqual(len(row['milliseconds']), 4)
            self.assertTrue(all(t >= 0 for t in row['milliseconds']))
            self.assertLessEqual(row['maxPositionError'], 1e-10)
            self.assertLessEqual(row['maxVelocityError'], 1e-10)

    def test_rejects_unbounded_workload(self):
        run = subprocess.run(
            ['dotnet', 'run', '--project', str(PROJECT), '-c', 'Release', '--',
             '--bodies', '1000000000'], capture_output=True, text=True,
            cwd=ROOT, timeout=60)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn('bodies', run.stderr.lower())

    def test_column_handoff_has_real_worker_evidence_and_bounded_allocation(self):
        run = subprocess.run(
            ['dotnet', 'run', '--project', str(PROJECT), '-c', 'Release', '--',
             '--mode', 'handoff-layout', '--bodies', '32', '--samples', '4'],
            capture_output=True, text=True, cwd=ROOT, timeout=60)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        report = json.loads(run.stdout)
        self.assertEqual(report['schema'], 'ksp-continuum-handoff-layout/v1')
        self.assertFalse(report['stockPhysicsSpeedupMeasured'])
        self.assertEqual(len(report['orders']), 4)
        self.assertEqual({r['layout'] for r in report['results']}, {'object', 'columns'})
        for order in report['orders']:
            self.assertEqual(set(order), {'object', 'columns'})
        results = {r['layout']: r for r in report['results']}
        self.assertEqual(results['object']['outputSha256'], results['columns']['outputSha256'])
        for r in results.values():
            self.assertEqual(len(r['samples']), 4)
            for sample in r['samples']:
                self.assertGreaterEqual(sample['endToEndMilliseconds'], 0)
                self.assertGreater(sample['callerAllocatedBytes'], 0)
                self.assertGreater(sample['backendAllocatedBytes'], 0)
                self.assertEqual(sample['maxPositionError'], 0)
                self.assertEqual(sample['maxVelocityError'], 0)
        self.assertLess(results['columns']['samples'][0]['backendAllocatedBytes'],
                        results['object']['samples'][0]['backendAllocatedBytes'])


if __name__ == '__main__':
    unittest.main()
