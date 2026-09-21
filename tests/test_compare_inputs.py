import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "compare_inputs.py"


class CompareInputsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def timeline(self, name, duration, tracks):
        path = self.root / name
        lines = [
            "schema,ksp-continuum-input-timeline/v1",
            "duration," + str(duration),
            "track,name,min,max",
        ]
        for track_name, minimum, maximum, _keys in tracks:
            lines.append("track,%s,%s,%s" % (track_name, minimum, maximum))
        lines.append("key,track,time,value,mode,control1,control2")
        for track_name, _minimum, _maximum, keys in tracks:
            for key in keys:
                lines.append("key,%s,%s,%s,%s,%s,%s" % ((track_name,) + key))
        lines.append("event,time,name,value")
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def run_compare(self, left, right, *extra):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(left), str(right),
             "--start", "0", "--end", "1", "--samples", "3",
             "--tolerance", "0.1"] + list(extra),
            cwd=ROOT, text=True, capture_output=True,
        )

    def test_evaluates_step_linear_and_cubic_bezier_tracks(self):
        tracks_left = [
            ("step", -1, 1, [(0, 0, "step", 0, 0), (0.5, 1, "step", 1, 1)]),
            ("linear", -1, 1, [(0, -1, "linear", -1, -1), (1, 1, "step", 1, 1)]),
            ("curve", 0, 1, [(0, 0, "cubic-bezier", 0, 1), (1, 0, "step", 0, 0)]),
        ]
        tracks_right = [
            (name, minimum, maximum, [(0, 0, "step", 0, 0)])
            for name, minimum, maximum, _keys in tracks_left
        ]
        left = self.timeline("left.csv", 1, tracks_left)
        right = self.timeline("right.csv", 1, tracks_right)
        output = self.root / "comparison.json"
        result = self.run_compare(left, right, "--output", str(output))
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(output.read_text(encoding="utf-8"))
        channels = {item["name"]: item for item in report["channels"]}
        self.assertEqual(1.0, channels["step"]["maxAbsDeviation"])
        self.assertEqual(0.5, channels["curve"]["firstDivergenceTime"])
        self.assertAlmostEqual(0.375, channels["curve"]["maxAbsDeviation"])
        self.assertEqual(0.0, channels["linear"]["firstDivergenceTime"])
        self.assertTrue(report["sampledNotContinuous"])
        self.assertFalse(report["eventsCompared"])

    def test_adds_all_key_breakpoints_to_the_declared_grid(self):
        pulse = [(0, 0, "step", 0, 0), (0.25, 1, "step", 1, 1),
                 (0.26, 0, "step", 0, 0)]
        left = self.timeline("left.csv", 1, [("x", 0, 1, pulse)])
        right = self.timeline("right.csv", 1, [("x", 0, 1, [(0, 0, "step", 0, 0)])])
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(left), str(right),
             "--start", "0", "--end", "1", "--samples", "2", "--tolerance", "0.5"],
            cwd=ROOT, text=True, capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(4, report["evaluatedSamples"])
        self.assertEqual(2, report["breakpointSamplesAdded"])
        self.assertEqual(1.0, report["channels"][0]["maxAbsDeviation"])
        self.assertEqual(0.25, report["channels"][0]["firstDivergenceTime"])

    def test_rejects_missing_channels_range_and_duration_mismatches(self):
        base = self.timeline("base.csv", 1, [("x", 0, 1, [(0, 0, "step", 0, 0)])])
        cases = (
            (self.timeline("missing.csv", 1, [("y", 0, 1, [(0, 0, "step", 0, 0)])]),
             "channel sets differ"),
            (self.timeline("range.csv", 1, [("x", -1, 1, [(0, 0, "step", 0, 0)])]),
             "range differs for channel x"),
            (self.timeline("duration.csv", 2, [("x", 0, 1, [(0, 0, "step", 0, 0)])]),
             "timeline durations differ"),
        )
        for other, message in cases:
            with self.subTest(message=message):
                result = self.run_compare(base, other)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)

    def test_rejects_domain_outside_timeline_and_invalid_mode(self):
        valid = self.timeline("valid.csv", 1, [("x", 0, 1, [(0, 0, "step", 0, 0)])])
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(valid), str(valid),
             "--start", "0", "--end", "1.1", "--samples", "3", "--tolerance", "0"],
            cwd=ROOT, text=True, capture_output=True,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("aligned domain must be within timeline duration", result.stderr)

        invalid = self.timeline("invalid.csv", 1, [("x", 0, 1, [(0, 0, "mystery", 0, 0)])])
        result = self.run_compare(valid, invalid)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unknown interpolation mode", result.stderr)

        invalid_number = self.timeline(
            "invalid-number.csv", "1_0", [("x", 0, 1, [(0, 0, "step", 0, 0)])]
        )
        result = self.run_compare(valid, invalid_number)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("invalid duration", result.stderr)

    def test_keeps_extreme_finite_cubic_evaluation_bounded(self):
        maximum = "1.7976931348623157E+308"
        left = self.timeline("left.csv", 1, [
            ("constant", 0, maximum, [(0, maximum, "cubic-bezier", maximum, maximum),
                                      (1, maximum, "step", maximum, maximum)]),
            ("cross", "-" + maximum, maximum,
             [(0, "-" + maximum, "cubic-bezier", maximum, "-" + maximum),
              (1, maximum, "step", maximum, maximum)]),
        ])
        right = self.timeline("right.csv", 1, [
            ("constant", 0, maximum, [(0, maximum, "step", maximum, maximum)]),
            ("cross", "-" + maximum, maximum, [(0, 0, "step", 0, 0)]),
        ])
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(left), str(right),
             "--start", "0.5", "--end", "0.75", "--samples", "3", "--tolerance", "0"],
            cwd=ROOT, text=True, capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        channels = {item["name"]: item for item in report["channels"]}
        self.assertEqual(0, channels["constant"]["maxAbsDeviation"])
        self.assertLessEqual(channels["cross"]["maxAbsDeviation"], float(maximum))

    def test_builds_large_finite_grid_without_overflow(self):
        maximum = "1.7976931348623157E+308"
        left = self.timeline("left.csv", maximum, [
            ("x", 0, 1, [(0, 0, "linear", 0, 0), (maximum, 1, "step", 1, 1)])
        ])
        right = self.timeline("right.csv", maximum, [
            ("x", 0, 1, [(0, 0, "step", 0, 0)])
        ])
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(left), str(right),
             "--start", "0", "--end", maximum, "--samples", "4", "--tolerance", "0"],
            cwd=ROOT, text=True, capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        channel = json.loads(result.stdout)["channels"][0]
        self.assertAlmostEqual((14 / 36) ** 0.5, channel["rmsDeviation"], places=12)

    def test_refuses_to_overwrite_output(self):
        timeline = self.timeline("timeline.csv", 1, [("x", 0, 1, [(0, 0, "step", 0, 0)])])
        output = self.root / "comparison.json"
        output.write_text("keep", encoding="utf-8")
        result = self.run_compare(timeline, timeline, "--output", str(output))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("output already exists", result.stderr)
        self.assertEqual("keep", output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
