import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "qualification_report.py"
PHASES = ("coast", "powered", "contact")


class QualificationReportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "qualification-session"
        self.source.mkdir()
        self.output = self.root / "report"
        (self.source / "scope.txt").write_text(
            "Start-context classified windows; raw source at /private/runtime.\n",
            encoding="utf-8",
        )
        (self.source / "status.txt").write_text(
            "complete\ncompletedWindows=3\n", encoding="utf-8"
        )
        for index, phase in enumerate(PHASES):
            self.write_phase(phase, index)

    def tearDown(self):
        self.temporary.cleanup()

    def marker_report(self, index):
        frames = [
            {
                "contextFrame": 10,
                "markerFrame": 10,
                "observedFrame": 11,
                "contextAligned": True,
                "wallMilliseconds": 16.0,
                "throttleCommand": 0.0 if index == 0 else 0.6,
                "packed": False,
                "body": "Minmus",
                "situation": "ORBITING" if index == 0 else "FLYING",
            },
            {
                "contextFrame": 11,
                "markerFrame": 12,
                "observedFrame": 13,
                "contextAligned": False,
                "wallMilliseconds": 20.0,
                "throttleCommand": None,
                "packed": True,
                "body": "Minmus",
                "situation": "LANDED" if index == 2 else "FLYING",
            },
        ]
        return {
            "schema": "ksp-continuum-markers/v2",
            "status": "complete",
            "requestedFrames": 2,
            "completedFrames": 2,
            "contextMisalignedFrames": 1,
            "frames": frames,
            "wallIntervals": {
                "count": 2, "minimum": 16.0, "maximum": 20.0,
                "mean": 18.0, "p50": 18.0, "p95": 19.8, "p99": 19.96,
            },
            "markers": [
                {
                    "name": "Physics.Simulate",
                    "status": "observed",
                    "recorderAvailableAtStart": True,
                    "availabilityDetail": "available",
                    "summary": {
                        "availableFrames": 2, "unavailableFrames": 0,
                        "observedFrames": 1, "zeroBlockFrames": 1,
                        "totalBlocks": 3,
                        "observedMilliseconds": {
                            "count": 1, "minimum": 1.5, "maximum": 1.5,
                            "mean": 1.5, "p50": 1.5, "p95": 1.5, "p99": 1.5,
                        },
                    },
                },
                {
                    "name": "Physics.Processing",
                    "status": "unavailable",
                    "recorderAvailableAtStart": False,
                    "availabilityDetail": "private failure /Users/example/game",
                    "summary": {
                        "availableFrames": 0, "unavailableFrames": 2,
                        "observedFrames": 0, "zeroBlockFrames": 0,
                        "totalBlocks": 0, "observedMilliseconds": None,
                    },
                },
            ],
        }

    def shadow_report(self, index):
        return {
            "schema": "ksp-continuum-flight-shadow/v1",
            "scope": "private source /Users/example/game",
            "status": "complete",
            "reason": "Bounded sample count reached.",
            "units": "position m; native Rigidbody.mass retained without unit conversion; timings ms; dt s",
            "submitted": 2,
            "accepted": 1,
            "stale": 1,
            "wallSeconds": 0.25,
            "samples": [
                {
                    "status": "accepted", "bodies": 2,
                    "captureMilliseconds": 0.2, "submitMilliseconds": 0.03,
                    "collectMilliseconds": 0.04, "collectAuditMilliseconds": 0.1,
                    "handoffWallMilliseconds": 4.0,
                },
                {
                    "status": "stale-discarded", "bodies": 2,
                    "captureMilliseconds": 0.22, "submitMilliseconds": 0.02,
                    "collectMilliseconds": 0.05, "collectAuditMilliseconds": 0.11,
                    "handoffWallMilliseconds": 5.0,
                },
            ],
            "firstAcceptedBatch": [
                {"id": 0, "mass": 1.0}, {"id": 1, "mass": 2.0}
            ],
            "firstAcceptedTick": 10,
        }

    def write_phase(self, phase, index):
        (self.source / (phase + "-start.txt")).write_text(
            "vessel=00000000-0000-0000-0000-%012d\n" % index
            + "situation=" + ("ORBITING" if phase == "coast" else "FLYING") + "\n"
            + "parts=17\nut=268881.5\nthrottleCommand=" + ("0" if phase == "coast" else "0.6") + "\n",
            encoding="utf-8",
        )
        (self.source / (phase + "-markers.json")).write_text(
            json.dumps(self.marker_report(index)), encoding="utf-8"
        )
        (self.source / (phase + "-shadow.json")).write_text(
            json.dumps(self.shadow_report(index)), encoding="utf-8"
        )

    def run_report(self):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(self.source), "--output", str(self.output)],
            cwd=ROOT, text=True, capture_output=True,
        )

    def test_builds_bounded_portable_summary_without_claiming_attribution(self):
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text(encoding="utf-8"))
        self.assertEqual("ksp-continuum-qualification-summary/v1", summary["schema"])
        self.assertEqual("qualification-session", summary["sourceSession"])
        self.assertEqual(["coast", "powered", "contact"], [p["id"] for p in summary["phases"]])

        coast = summary["phases"][0]
        self.assertEqual("ORBITING", coast["startContext"]["situation"])
        self.assertEqual({"ORBITING": 1, "FLYING": 1}, coast["profiler"]["frameContexts"]["situations"])
        self.assertEqual({"true": 1, "false": 1, "unknown": 0}, coast["profiler"]["frameContexts"]["packed"])
        self.assertEqual({"nearZero": 1, "positive": 0, "other": 0, "unknown": 1},
                         coast["profiler"]["frameContexts"]["throttleCommand"])
        self.assertEqual("unavailable", coast["profiler"]["markers"][1]["status"])
        self.assertEqual(2, coast["shadow"]["firstAcceptedBodyCount"])
        self.assertEqual(1, coast["shadow"]["accepted"])
        self.assertEqual(5.0, coast["shadow"]["timingsMilliseconds"]["handoff"]["maximum"])
        self.assertFalse(summary["claims"]["wholeFrameAttribution"])
        self.assertFalse(summary["claims"]["stockPhysicsComparison"])

        sources = {item["path"]: item for item in summary["sources"]}
        marker_path = "coast-markers.json"
        self.assertEqual(hashlib.sha256((self.source / marker_path).read_bytes()).hexdigest(),
                         sources[marker_path]["sha256"])
        serialized = json.dumps(summary)
        self.assertNotIn(str(self.root), serialized)
        self.assertNotIn("/private/runtime", serialized)
        self.assertNotIn("/Users/example", serialized)

        page = (self.output / "index.html").read_text(encoding="utf-8")
        self.assertIn("Start context", page)
        self.assertIn("Observed frame contexts", page)
        self.assertIn("Physics.Processing", page)
        self.assertIn("Unavailable", page)
        self.assertIn("Profiler status</dt><dd>Complete", page)
        self.assertIn("Completed / requested frames</dt><dd>2 / 2", page)
        self.assertIn("Zero-force transport", page)
        self.assertNotIn("physics percentage", page.lower())
        self.assertNotIn(str(self.root), page)

    def test_reports_terminal_partial_capture_without_inventing_missing_phases(self):
        (self.source / "status.txt").write_text("timeout\ncompletedWindows=1\n", encoding="utf-8")
        for phase in ("powered", "contact"):
            for suffix in ("-start.txt", "-markers.json", "-shadow.json"):
                (self.source / (phase + suffix)).unlink()

        result = self.run_report()

        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text(encoding="utf-8"))
        self.assertEqual("timeout", summary["status"])
        self.assertEqual(["coast"], [p["id"] for p in summary["phases"]])
        self.assertEqual(["powered", "contact"], summary["missingPhases"])

    def test_rejects_schema_count_and_nonfinite_mismatches(self):
        cases = (
            (lambda data: data.update(schema="wrong"), "unsupported marker schema"),
            (lambda data: data.update(completedFrames=3), "profiler frame count"),
            (lambda data: data["frames"][0].update(wallMilliseconds=float("nan")), "finite"),
        )
        path = self.source / "coast-markers.json"
        for mutate, message in cases:
            with self.subTest(message=message):
                data = self.marker_report(0)
                mutate(data)
                path.write_text(json.dumps(data), encoding="utf-8")
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
                self.assertFalse(self.output.exists())
        path.write_text(json.dumps(self.marker_report(0)), encoding="utf-8")

    def test_refuses_symbolic_sources_oversized_files_and_existing_output(self):
        path = self.source / "coast-markers.json"
        original = path.read_bytes()
        target = self.root / "outside.json"
        target.write_bytes(original)
        path.unlink()
        path.symlink_to(target)
        result = self.run_report()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("symbolic", result.stderr)
        path.unlink()
        path.write_bytes(original)

        with path.open("wb") as stream:
            stream.truncate(16 * 1024 * 1024 + 1)
        result = self.run_report()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("byte limit", result.stderr)
        path.write_bytes(original)

        self.output.mkdir()
        sentinel = self.output / "keep.txt"
        sentinel.write_text("keep", encoding="utf-8")
        result = self.run_report()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("output already exists", result.stderr)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))

    def test_rejects_duplicate_json_fields(self):
        path = self.source / "coast-shadow.json"
        raw = path.read_text(encoding="utf-8")
        path.write_text(raw[:-1] + ',"status":"complete"}', encoding="utf-8")

        result = self.run_report()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("duplicate JSON field", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_marker_status_that_contradicts_counts(self):
        path = self.source / "coast-markers.json"
        mutations = (
            ("unavailable", 2, 1, "unavailable marker has available frames"),
            ("observed", 2, 0, "observed marker has no observed frames"),
            ("available-no-samples", 2, 1, "available-no-samples marker has observations"),
        )
        for status, available, observed, message in mutations:
            with self.subTest(status=status):
                report = self.marker_report(0)
                marker = report["markers"][0]
                marker["status"] = status
                marker["summary"].update(
                    availableFrames=available,
                    unavailableFrames=2 - available,
                    observedFrames=observed,
                    zeroBlockFrames=available - observed,
                    observedMilliseconds=(
                        {"count": observed, "minimum": 1.5, "maximum": 1.5, "mean": 1.5,
                         "p50": 1.5, "p95": 1.5, "p99": 1.5} if observed else None
                    ),
                )
                path.write_text(json.dumps(report), encoding="utf-8")
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
                self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
