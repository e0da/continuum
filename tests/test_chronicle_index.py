import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts/chronicle_index.py"


class ChronicleIndexTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "archive"
        self.archive.mkdir()
        report = self.archive / "CSP-0001-A003"
        report.mkdir()
        (report / "index.html").write_text("<h1>Flight report</h1>")
        self.manifest = report / "manifest.json"
        self.manifest.write_text(json.dumps({
            "schema": "ksp-continuum-chronicle-manifest/v1",
            "title": "Pathfinder <first>", "attemptId": "CSP-0001-A003",
            "outcome": "passed", "report": "index.html",
            "generatedUtc": "2026-09-20T20:00:00Z",
        }))

    def run_index(self, *options):
        return subprocess.run([sys.executable, str(SCRIPT), "--archive", str(self.archive),
                               "--output", "index.html", *options], capture_output=True, text=True)

    def test_refresh_selects_latest_rendering_without_changing_old_report(self):
        self.assertEqual(self.run_index().returncode, 0)
        newer = self.archive / "CSP-0001-A003-report2"
        newer.mkdir()
        (newer / "index.html").write_text("new report")
        data = json.loads(self.manifest.read_text())
        data.update(title="Corrected report", generatedUtc="2026-09-20T21:00:00Z")
        (newer / "manifest.json").write_text(json.dumps(data))
        result = self.run_index("--refresh")
        self.assertEqual(result.returncode, 0, result.stderr)
        page = (self.archive / "index.html").read_text()
        self.assertEqual(page.count("<article>"), 1)
        self.assertIn('href="CSP-0001-A003-report2/index.html">Corrected report</a>', page)
        self.assertIn("Earlier renderings", page)
        self.assertEqual((self.manifest.parent / "index.html").read_text(), "<h1>Flight report</h1>")

    def test_relocatable_links_and_escaped_titles(self):
        result = self.run_index()
        self.assertEqual(result.returncode, 0, result.stderr)
        page = (self.archive / "index.html").read_text()
        self.assertIn('href="CSP-0001-A003/index.html"', page)
        self.assertIn("Pathfinder &lt;first&gt;", page)
        self.assertNotIn(str(self.root), page)

    def test_refuses_to_replace_existing_index(self):
        output = self.archive / "index.html"
        output.write_text("preserved")
        result = self.run_index()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exists", result.stderr)
        self.assertEqual(output.read_text(), "preserved")

    def test_rejects_external_report_path(self):
        data = json.loads(self.manifest.read_text())
        data["report"] = "../../private.html"
        self.manifest.write_text(json.dumps(data))
        result = self.run_index()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("report", result.stderr)
        self.assertFalse((self.archive / "index.html").exists())
