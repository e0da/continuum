import importlib.util
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("orbit_fixture", ROOT / "run.py")
fixture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fixture)


class OrbitFixtureTests(unittest.TestCase):
    def test_circular_quarter_period_has_independent_closed_form_state(self):
        case = fixture.reference_case("quarter", 4, 2, 0, math.pi / 2)
        self.assertAlmostEqual(math.pi / math.sqrt(2), case["dt"])
        for expected, actual in zip([0, 2, 0, -math.sqrt(2), 0, 0], case["expected"]):
            self.assertAlmostEqual(expected, actual)

    def test_eccentric_apocentre_obeys_geometry_and_vis_viva(self):
        case = fixture.reference_case("apo", 8, 4, .5, math.pi)
        self.assertAlmostEqual(-6, case["expected"][0])
        self.assertAlmostEqual(-math.sqrt(2 / 3), case["expected"][4])
        self.assertAlmostEqual(math.pi * math.sqrt(8), case["dt"])

    def test_orbital_gate_detects_bad_state_and_nonfinite_output(self):
        case = fixture.reference_case("test", 4, 2, 0, math.pi / 2)
        good = fixture.assess_case(case, case["expected"])
        self.assertTrue(good["passed"])
        bad = list(case["expected"]); bad[0] += .01
        self.assertFalse(fixture.assess_case(case, bad)["passed"])
        bad[0] = float("nan")
        with self.assertRaises(fixture.FixtureError):
            fixture.assess_case(case, bad)

    def test_swept_events_find_two_crossings_and_tangent_missed_by_endpoints(self):
        self.assertEqual([.25, .75], fixture.sphere_events((-2, 0, 0), (4, 0, 0), 1, 1))
        self.assertFalse(fixture.endpoint_crossing((-2, 0, 0), (4, 0, 0), 1, 1))
        self.assertEqual([.5], fixture.sphere_events((-2, 1, 0), (4, 0, 0), 1, 1))
        self.assertFalse(fixture.endpoint_crossing((-2, 1, 0), (4, 0, 0), 1, 1))

    def test_near_grazing_inside_outside_and_initial_overlap(self):
        self.assertEqual([], fixture.sphere_events((-2, 1.00000001, 0), (4, 0, 0), 1, 1))
        self.assertEqual(2, len(fixture.sphere_events((-2, .99999999, 0), (4, 0, 0), 1, 1)))
        self.assertEqual([.5], fixture.sphere_events((0, 0, 0), (2, 0, 0), 1, 1))
        self.assertEqual([], fixture.sphere_events((2, 0, 0), (0, 0, 0), 1, 1))
        with self.assertRaises(fixture.FixtureError):
            fixture.sphere_events((1, 0, 0), (0, 0, 0), 1, 1)

    def test_bad_event_domain_and_nonfinite_inputs_fail_closed(self):
        for value in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(fixture.FixtureError):
                fixture.sphere_events((-2, 0, 0), (4, 0, 0), 1, value)
        with self.assertRaises(fixture.FixtureError):
            fixture.sphere_events((float("inf"), 0, 0), (4, 0, 0), 1, 1)

    def test_output_is_exclusive_and_source_validation_fails_without_donor(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            output.write_text("preserve")
            with self.assertRaises(FileExistsError):
                fixture.write_report(output, {"passed": True})
            self.assertEqual("preserve", output.read_text())
            result = subprocess.run([sys.executable, str(ROOT / "run.py"),
                "--gimbal-root", directory, "--expected-revision", "0" * 40,
                "--output", str(Path(directory) / "new.json")], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((Path(directory) / "new.json").exists())


if __name__ == "__main__":
    unittest.main()
