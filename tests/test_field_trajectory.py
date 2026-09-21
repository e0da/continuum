"""Stdlib trajectory and potential-split fixtures; no optional scientific dependency."""
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'tools/field-gravity/trajectory.py'


class FieldTrajectoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('field_trajectory', SCRIPT)
        cls.model = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.model)

    def test_verlet_one_step_matches_independent_kick_drift_kick(self):
        p, v, m, dt = [[-1, 0, 0], [1, 0, 0]], [[0, 0, 0], [0, 0, 0]], [1, 1], .01
        a = 2/(4+.36)**1.5
        expected_x = -1+.5*a*dt*dt
        separation = -2*expected_x
        next_a = separation/(separation**2+.36)**1.5
        expected_v = .5*(a+next_a)*dt
        actual = self.model.integrate(p, v, m, dt, 1)
        self.assertAlmostEqual(actual['positions'][0][0], expected_x, places=14)
        self.assertAlmostEqual(actual['velocities'][0][0], expected_v, places=14)

    def test_split_force_is_gradient_of_each_potential(self):
        r, h = 1.8, 1e-6
        split = self.model.split_pair(r)
        for part in ('near', 'far'):
            derivative = (self.model.split_pair(r+h)[part+'Potential']-self.model.split_pair(r-h)[part+'Potential'])/(2*h)
            self.assertAlmostEqual(split[part+'RadialDerivative'], derivative, delta=1e-7)

    def test_circular_oracle_and_reversal(self):
        case = self.model.case('circular')
        run = self.model.integrate(case['positions'], case['velocities'], case['masses'], case['period']/512, 512)
        exact = self.model.circular_exact(case, case['period'])
        error = self.model.state_error(run, exact, case)
        self.assertLess(error['position'], .001)
        self.assertLess(error['velocity'], .001)
        back = self.model.integrate(run['positions'], run['velocities'], case['masses'], -case['period']/512, 512)
        self.assertLess(self.model.state_error(back, case, case)['position'], 1e-10)

    def test_omission_adversary_cannot_hide_behind_reconstruction(self):
        s = self.model.split_pair(1.8)
        self.assertAlmostEqual(s['nearRadialDerivative']+s['farRadialDerivative'], s['radialDerivative'], places=14)
        self.assertAlmostEqual(s['naiveNear']+s['naiveFar'], s['radialDerivative'], places=14)
        self.assertGreater(abs(s['naiveNear']-s['nearRadialDerivative']), .01)

    def test_limits_and_initial_state_ownership(self):
        p, v, m = [[0., 0., 0.]], [[1., 0., 0.]], [1.]
        self.model.integrate(p, v, m, .1, 2)
        self.assertEqual(p, [[0., 0., 0.]])
        for dt, steps in ((float('nan'), 1), (0, 1), (.1, 0), (.1, 131073), (.1, 1.5)):
            with self.assertRaises(ValueError):
                self.model.integrate(p, v, m, dt, steps)
        with self.assertRaises(ValueError):
            self.model.split_pair(float('nan'))
        with self.assertRaises(ValueError):
            self.model.integrate(p, v, [0], .1, 2)
        with self.assertRaises(ValueError):
            self.model.integrate(p, v, m, .1, 2, mode='unknown')

    def test_report_and_exclusive_output(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'trajectory.json'
            cmd = [sys.executable, str(SCRIPT), '--output', str(path)]
            run = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
            self.assertIn(run.returncode, (0, 2), run.stderr)
            report = json.loads(path.read_text())
            self.assertEqual(report['schema'], 'ksp-continuum-field-trajectory/v1')
            self.assertFalse(report['fftTrajectoryQualified'])
            self.assertFalse(report['speedClaim'])
            self.assertEqual(len(report['trajectories']), 2)
            self.assertEqual(len(report['trajectories'][0]['refinements']), 4)
            self.assertEqual(report['schedule']['stepsPerPeriod'], [128, 256, 512, 1024])
            self.assertTrue(report['split']['omissionAdversaryDetected'])
            self.assertEqual(run.returncode, 0 if report['qualified'] else 2)
            saved = path.read_bytes()
            again = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
            self.assertEqual(again.returncode, 1)
            self.assertEqual(path.read_bytes(), saved)

    def test_one_bounded_refinement_keeps_gates_and_exposes_schedule(self):
        report = self.model.experiment(1)
        self.assertEqual(report['schedule']['stepsPerPeriod'], [256, 512, 1024, 2048])
        self.assertEqual(report['schedule']['noncircularReferenceStepsPerPeriod'], [16384, 32768])
        self.assertEqual(report['gates']['energy'], 1e-4)
        self.assertEqual(report['gates']['referenceAgreement'], 1e-6)
        self.assertFalse(report['fftTrajectoryQualified'])
        with self.assertRaises(ValueError):
            self.model.experiment(2)


if __name__ == '__main__':
    unittest.main()
