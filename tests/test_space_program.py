import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "space_program.py"


class SpaceProgramTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "space-program"
        self.archive.mkdir()
        (self.archive / "experiments").mkdir()
        (self.archive / "experiments" / "descent.png").write_bytes(b"chart")
        self.write_report("render-old", "2026-09-20T20:00:00Z", "OLD REPORT")
        self.write_report("render-latest", "2026-09-20T21:00:00Z", "NEW REPORT")
        self.catalog = self.archive / "catalog.json"
        self.write_catalog()

    def write_report(self, folder_name, generated, body):
        folder = self.archive / folder_name
        folder.mkdir()
        (folder / "media").mkdir()
        (folder / "media" / "landing.png").write_bytes(b"png bytes")
        (folder / "index.html").write_text(
            '<!doctype html><html><head><title>Report</title></head><body>'
            '<main><h1>%s</h1><img src="media/landing.png" alt="Landed vessel"></main>'
            '</body></html>' % body,
            encoding="utf-8",
        )
        (folder / "manifest.json").write_text(json.dumps({
            "schema": "ksp-continuum-chronicle-manifest/v1",
            "generatedUtc": generated,
            "missionId": "CSP-0002",
            "attemptId": "CSP-0002-A001",
            "vehicleDesignId": "CV-0001-R01",
            "siteId": "SITE-MIN-001",
            "title": "Minmus Survey 1",
            "outcome": "failed",
            "report": "index.html",
            "media": [{
                "filename": "landing.png", "status": "confirmed", "width": 1920, "height": 1080,
            }],
        }), encoding="utf-8")

    def catalog_data(self):
        return {
            "schema": "ksp-continuum-space-program/v1",
            "program": {
                "name": "Continuum <Space> Program",
                "tagline": "Flights, discoveries, experiments",
                "summary": "A connected record of missions and engineering work.",
            },
            "missions": [{
                "id": "CSP-0002", "name": "Minmus Survey 1", "status": "active",
                "summary": "Survey a candidate site.",
                "facts": [{"label": "Objective", "value": "Measure the landing site."}],
                "media": [],
            }],
            "vehicles": [{
                "id": "CV-0001-R01", "name": "Kerbal X / stock", "status": "flown",
                "summary": "The current qualification vehicle.", "facts": [], "media": [],
            }],
            "sites": [{
                "id": "SITE-MIN-001", "name": "Greater Flats Candidate 1", "status": "candidate",
                "summary": "A measured Minmus candidate.", "facts": [], "media": [],
            }],
            "experiments": [{
                "id": "EXP-CSP-0002-DESCENT", "name": "Final descent profile", "status": "observed",
                "summary": "Compare commanded and observed final descent behavior.",
                "attempt_ids": ["CSP-0002-A001"],
                "facts": [{"label": "Result", "value": "Observed, not qualified."}],
                "media": [{
                    "path": "experiments/descent.png", "caption": "Final descent measurements",
                    "alt": "Chart of the final descent measurements",
                }],
            }],
        }

    def write_catalog(self, transform=None):
        data = self.catalog_data()
        if transform:
            transform(data)
        self.catalog.write_text(json.dumps(data), encoding="utf-8")

    def run_generator(self, output=None):
        return subprocess.run([
            sys.executable, str(SCRIPT), "--archive", str(self.archive),
            "--catalog", str(self.catalog), "--output", str(output or self.archive / "site"),
        ], cwd=ROOT, text=True, capture_output=True)

    def test_builds_connected_site_and_enhanced_latest_attempt(self):
        old_hash = hashlib.sha256((self.archive / "render-latest" / "index.html").read_bytes()).hexdigest()
        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        site = self.archive / "site"

        html_files = sorted(site.glob("**/*.html"))
        self.assertEqual(11, len(html_files))
        for path in html_files:
            self.assertIn('class="site-nav"', path.read_text(encoding="utf-8"), str(path))

        home = (site / "index.html").read_text(encoding="utf-8")
        self.assertIn("Continuum &lt;Space&gt; Program", home)
        self.assertIn('href="missions/CSP-0002.html"', home)
        self.assertIn('href="attempts/CSP-0002-A001/index.html"', home)
        self.assertNotIn(str(self.root), home)

        mission = (site / "missions" / "CSP-0002.html").read_text(encoding="utf-8")
        self.assertIn('href="../attempts/CSP-0002-A001/index.html"', mission)
        self.assertIn('href="../vehicles/CV-0001-R01.html"', mission)
        self.assertIn('href="../sites/SITE-MIN-001.html"', mission)
        self.assertIn('href="../experiments/EXP-CSP-0002-DESCENT.html"', mission)

        attempt = (site / "attempts" / "CSP-0002-A001" / "index.html").read_text(encoding="utf-8")
        self.assertIn("NEW REPORT", attempt)
        self.assertNotIn("OLD REPORT", attempt)
        self.assertIn('href="../../missions/CSP-0002.html"', attempt)
        self.assertIn('href="../../vehicles/CV-0001-R01.html"', attempt)
        self.assertIn('href="../../sites/SITE-MIN-001.html"', attempt)
        self.assertIn('href="../../experiments/EXP-CSP-0002-DESCENT.html"', attempt)
        self.assertIn('href="../../../render-latest/index.html"', attempt)
        self.assertIn('href="../../../render-old/index.html"', attempt)
        self.assertIn("Recorded outcome: <strong>failed</strong>", attempt)
        self.assertIn("Minmus Survey 1 (CSP-0002)", attempt)
        self.assertIn("Kerbal X / stock (CV-0001-R01)", attempt)
        self.assertIn(">Original report</a>", attempt)
        self.assertIn('alt="Landing screenshot"', attempt)
        self.assertIn("landing.png · confirmed · 1920 × 1080", attempt)
        self.assertTrue((site / "attempts" / "CSP-0002-A001" / "media" / "landing.png").is_file())
        self.assertEqual(
            old_hash,
            hashlib.sha256((self.archive / "render-latest" / "index.html").read_bytes()).hexdigest(),
        )

        experiment = (site / "experiments" / "EXP-CSP-0002-DESCENT.html").read_text(encoding="utf-8")
        self.assertIn('src="../assets/catalog/experiments/descent.png"', experiment)
        self.assertIn('href="../assets/catalog/experiments/descent.png"', experiment)
        self.assertIn('alt="Chart of the final descent measurements"', experiment)
        self.assertIn("Final descent measurements", experiment)
        self.assertTrue((site / "assets" / "catalog" / "experiments" / "descent.png").is_file())

        marker = json.loads((site / "site-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual("ksp-continuum-space-program-site/v1", marker["schema"])
        self.assertEqual(1, marker["attempts"])
        self.assertEqual("render-latest", marker["reports"][0]["sourceFolder"])
        self.assertEqual(64, len(marker["generatorSha256"]))
        self.assertEqual(64, len(marker["templateSha256"]))
        self.assertEqual(64, len(marker["catalogSha256"]))

    def test_rebuild_updates_only_marked_derived_site(self):
        self.assertEqual(0, self.run_generator().returncode)
        self.write_catalog(lambda data: data["program"].update(tagline="Updated program"))
        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Updated program", (self.archive / "site" / "index.html").read_text())
        self.assertIn("NEW REPORT", (self.archive / "render-latest" / "index.html").read_text())

        unmarked = self.archive / "unmarked"
        unmarked.mkdir()
        (unmarked / "keep.txt").write_text("keep")
        result = self.run_generator(unmarked)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("marker", result.stderr)
        self.assertEqual("keep", (unmarked / "keep.txt").read_text())

    def test_rejects_catalog_media_path_traversal_without_output(self):
        self.write_catalog(lambda data: data["experiments"][0]["media"][0].update(path="../private.png"))
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unsafe media path", result.stderr)
        self.assertFalse((self.archive / "site").exists())

    def test_rejects_manifest_reference_to_missing_catalog_entity(self):
        self.write_catalog(lambda data: data.update(vehicles=[]))
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("missing vehicle CV-0001-R01", result.stderr)
        self.assertFalse((self.archive / "site").exists())

    def test_rejects_private_path_in_report_manifest(self):
        manifest = self.archive / "render-latest" / "manifest.json"
        data = json.loads(manifest.read_text())
        data["debugPath"] = "/private/var/folders/session/save.sfs"
        manifest.write_text(json.dumps(data))
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("manifest contains a private absolute path", result.stderr)
        self.assertFalse((self.archive / "site").exists())

    def test_rejects_output_outside_archive(self):
        outside = self.root / "site"
        result = self.run_generator(outside)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("direct child of the archive", result.stderr)
        self.assertFalse(outside.exists())

    def test_rejects_private_path_in_catalog_narrative(self):
        for value in (
            "Local evidence lives at /private/var/folders/session/save.sfs.",
            r"Local evidence lives at \\private-server\user\save.sfs.",
        ):
            with self.subTest(value=value):
                self.write_catalog(lambda data: data["program"].update(summary=value))
                result = self.run_generator()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("catalog contains a private absolute path", result.stderr)
                self.assertFalse((self.archive / "site").exists())

    def test_rejects_private_unc_path_in_report_html(self):
        report = self.archive / "render-latest" / "index.html"
        report.write_text(
            "<!doctype html><html><head><title>Report</title></head><body>"
            r"Evidence at \\private-server\user\save.sfs"
            "</body></html>",
            encoding="utf-8",
        )
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("report page contains a private absolute path", result.stderr)
        self.assertFalse((self.archive / "site").exists())


if __name__ == "__main__":
    unittest.main()
