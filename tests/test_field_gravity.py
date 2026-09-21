"""Optional scientific fixture; execute with tools/field-gravity/requirements.txt installed."""
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'tools/field-gravity/run.py'
HAS_NUMPY = importlib.util.find_spec('numpy') is not None


@unittest.skipUnless(HAS_NUMPY, 'field-gravity requires its isolated pinned NumPy environment')
class FieldGravityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('field_gravity', SCRIPT)
        cls.model = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.model)

    def test_two_body_analytic_force_and_isolated_boundary(self):
        positions, masses, epsilon = [[0, 4, 4], [8, 4, 4]], [2, 5], .6
        expected = 5 * 8 / (64 + epsilon**2)**1.5
        direct, _ = self.model.direct(positions, masses, epsilon)
        field, _ = self.model.field(positions, masses, 17, epsilon)
        self.assertAlmostEqual(direct[0][0], expected, places=13)
        self.assertAlmostEqual(field[0][0], expected, places=13)
        self.assertAlmostEqual(field[1][0], -2/5*expected, places=13)
        self.assertLess(max(abs(field[i][j]) for i in range(2) for j in (1, 2)), 1e-13)

    def test_offgrid_isolated_self_force(self):
        force, _ = self.model.field([[3.173, 2.291, 4.867]], [3.0], 17, .6)
        self.assertLess(math.sqrt(sum(x*x for x in force[0])), 1e-10*3/.6**2)

    def test_offgrid_pair_refines_with_fixed_softening(self):
        p, m, epsilon = [[3.173, 3.619, 3.883], [4.431, 4.107, 4.557]], [2, 5], .6
        reference, _ = self.model.direct(p, m, epsilon)
        separation = math.dist(p[0], p[1])
        for axis in range(3):
            analytic = m[1]*(p[1][axis]-p[0][axis])/(separation**2+epsilon**2)**1.5
            self.assertAlmostEqual(reference[0][axis], analytic, places=13)
        errors = []
        for n in (17, 33):
            actual, _ = self.model.field(p, m, n, epsilon)
            errors.append(self.model.assess(actual, reference, m, epsilon)['normalizedRmsError'])
        self.assertLess(errors[1], errors[0])

    def test_assessment_retains_inaccurate_and_nonfinite_results(self):
        bad = self.model.assess([[0, 0, 0], [0, 0, 0]], [[1, 0, 0], [-1, 0, 0]], [1, 1], .6)
        self.assertFalse(bad['qualified'])
        self.assertEqual(bad['normalizedRmsError'], 1)
        with self.assertRaises(ValueError):
            self.model.assess([[float('nan'), 0, 0]], [[0, 0, 0]], [1], .6)

    def test_capture_limits_and_inputs_unchanged(self):
        p, m = [[3.1, 4, 4]], [2]
        self.model.field(p, m, 17, .6)
        self.assertEqual(p, [[3.1, 4, 4]])
        self.assertEqual(m, [2])
        for positions, masses, nodes, epsilon in (
            (p, m, 129, .6), (p, m, 16, .6), (p, m, 17, 0),
            ([[float('nan'), 0, 0]], m, 17, .6), ([[8.1, 0, 0]], m, 17, .6),
            (p, [-1], 17, .6), (p, [], 17, .6), ([[1, 1, 1]]*129, [1]*129, 17, .6),
        ):
            with self.assertRaises(ValueError):
                self.model.field(positions, masses, nodes, epsilon)

    def test_cli_report_keeps_failure_rows_and_exclusive_output(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'report.json'
            command = [sys.executable, str(SCRIPT), '--grids', '17', '--samples', '1', '--output', str(path)]
            result = subprocess.run(command, capture_output=True, text=True, timeout=90)
            self.assertIn(result.returncode, (0, 2), result.stderr)
            report = json.loads(path.read_text())
            self.assertEqual(report['schema'], 'ksp-continuum-field-gravity/v1')
            self.assertFalse(report['trajectoryQualification'])
            self.assertEqual(report['model']['softening'], .6)
            self.assertTrue(any(not row['qualified'] for row in report['rows']))
            self.assertEqual(result.returncode, 0 if report['finestGridQualified'] else 2)
            for row in report['rows']:
                self.assertEqual(len(row['samples']), 1)
                self.assertIn('positions', row)
                for strategy in ('direct', 'field'):
                    self.assertGreater(row['samples'][0][strategy]['totalSeconds'], 0)
            original = path.read_bytes()
            again = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertEqual(again.returncode, 1)
            self.assertEqual(path.read_bytes(), original)


if __name__ == '__main__':
    unittest.main()
