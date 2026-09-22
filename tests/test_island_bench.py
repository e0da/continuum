"""Exercise deterministic island scheduling and its failure boundary."""
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]


class IslandBenchTests(unittest.TestCase):
    def test_structural_workload_matrix(self):
        run = subprocess.run(
            ['dotnet', 'run', '--project', 'tools/KspContinuum.IslandBench', '-c', 'Release', '--',
             '--repetitions', '2', '--parallelism', '4'], cwd=ROOT, capture_output=True, text=True, timeout=60)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        report = json.loads(run.stdout)
        self.assertEqual(report['schema'], 'ksp-continuum-island-bench/v1')
        rows = {row['workload']: row for row in report['workloads']}
        self.assertEqual(set(rows), {'many-tiny-islands', 'long-chain', 'mixed-sizes'})
        self.assertEqual(rows['many-tiny-islands']['islands'], 4096)
        self.assertEqual(rows['long-chain']['islands'], 1)
        self.assertGreater(rows['mixed-sizes']['islands'], 2)
        self.assertTrue(all(row['exactOutputMatch'] for row in rows.values()))
        self.assertTrue(report['safety']['cancellationObserved'])
        self.assertTrue(report['safety']['failureObserved'])
        self.assertFalse(report['safety']['cancellationPublishedOutput'])
        self.assertFalse(report['safety']['failurePublishedOutput'])


if __name__ == '__main__':
    unittest.main()
