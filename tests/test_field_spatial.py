"""Spatial smooth-split fixture; requires the pinned field-gravity NumPy environment."""
import importlib.util
import math
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HAS_NUMPY = importlib.util.find_spec('numpy') is not None


@unittest.skipUnless(HAS_NUMPY, 'requires field-gravity NumPy environment')
class FieldSpatialTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('spatial', ROOT/'tools/field-gravity/spatial.py')
        cls.model = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.model)

    def test_node_aligned_split_matches_independent_plummer_force(self):
        # This separation lies in the switching shell, exercising both product-rule terms.
        p, m = [[3, 4, 4], [4.5, 4, 4]], [2, 5]
        a = self.model.spatial_force(p, m, 17)
        expected = 5*1.5/(1.5**2+.6**2)**1.5
        self.assertAlmostEqual(a[0][0], expected, places=12)
        self.assertAlmostEqual(a[1][0], -2/5*expected, places=12)
        self.assertLess(max(abs(row[k]) for row in a for k in (1, 2)), 1e-12)

    def test_force_provider_controls_verlet_and_rejects_malformed_output(self):
        t = self.model.trajectory
        p, v, m = [[0., 0., 0.]], [[1., 0., 0.]], [2.]
        run = t.integrate(p, v, m, .1, 2, force_provider=lambda p, m: [[3., 0., 0.]])
        self.assertAlmostEqual(run['positions'][0][0], .26, places=14)
        self.assertAlmostEqual(run['velocities'][0][0], 1.6, places=14)
        for output in ([], [[float('nan'), 0, 0]], [[1, 2]]):
            with self.assertRaises(ValueError):
                t.integrate(p, v, m, .1, 2, force_provider=lambda p, m: output)

    def test_far_kernel_matches_independent_component_potential_gradient(self):
        r, h = 1.8, 1e-6
        derivative = (self.model.trajectory.split_pair(r+h)['farPotential']-
                      self.model.trajectory.split_pair(r-h)['farPotential'])/(2*h)
        self.assertAlmostEqual(float(self.model.far_coefficient(self.model.np.array(r*r)))*r,
                               derivative, delta=1e-7)

    def test_fft_matches_stencil_for_offgrid_boundary_and_asymmetric_snapshots(self):
        cases = [([[3.173, 2.291, 4.867]], [3]),
                 ([[0, 4, 4], [8, 4, 4]], [2, 5]),
                 ([[3.173, 3.619, 3.883], [4.831, 4.281, 4.159], [6.1, 2.13, 3.3]], [2, 5, 3])]
        for nodes in (17, 33, 65):
            for positions, masses in cases:
                stencil = self.model.spatial_force(positions, masses, nodes)
                fft = self.model.fft_force(positions, masses, nodes)
                self.assertLess(max(math.dist(a, b) for a, b in zip(stencil, fft)), 1e-11)
                self.assertLess(math.sqrt(sum(sum(m*a[k] for m, a in zip(masses, stencil))**2 for k in range(3))), 1e-12)
        self.assertEqual(self.model.spatial_force(cases[0][0], cases[0][1], 17), [[0., 0., 0.]])

    def test_mesh_error_is_measured_instead_of_replaced_by_direct_force(self):
        p, m = [[3.173, 3.619, 3.883], [4.831, 4.281, 4.159]], [2, 5]
        reference = self.model.trajectory.accelerations(p, m)
        errors = [math.dist(self.model.spatial_force(p, m, n)[0], reference[0]) for n in (17, 33, 65)]
        self.assertGreater(errors[0], 1e-3)
        self.assertLess(errors[-1], errors[0])
        original = [list(x) for x in p]
        self.model.spatial_force(p, m, 17)
        self.assertEqual(p, original)
        for positions, masses, nodes in (([[-1, 4, 4]], [1], 17), ([[4, 4, 4]], [1], 18)):
            with self.assertRaises(ValueError):
                self.model.spatial_force(positions, masses, nodes)

    def test_report_preserves_gates_schedules_failures_and_exclusive_output(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'spatial.json'
            command = [sys.executable, str(ROOT/'tools/field-gravity/spatial.py'), '--grids', '17', '--output', str(path)]
            run = subprocess.run(command, capture_output=True, text=True, timeout=120)
            self.assertTrue(path.exists(), run.stderr)
            report = json.loads(path.read_text())
            self.assertEqual(report['gates'], self.model.trajectory.GATES)
            self.assertEqual(report['schedule']['stepsPerPeriod'], [256, 512, 1024, 2048])
            self.assertTrue(report['directBaseline']['qualified'])
            self.assertTrue(report['fftSnapshotsQualified'])
            self.assertEqual(len(report['trajectories']), 2)
            self.assertFalse(report['fftTrajectoryQualified'])
            self.assertFalse(report['speedClaim'])
            self.assertEqual(run.returncode, 0 if report['qualified'] else 2)
            for row in report['trajectories']:
                self.assertEqual(len(row['refinements']), 4)
                self.assertIn('sameStepDirectDifference', row)
            saved = path.read_bytes()
            again = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertEqual(again.returncode, 1)
            self.assertEqual(path.read_bytes(), saved)


if __name__ == '__main__':
    unittest.main()
