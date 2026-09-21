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
        self.write_report("render-latest", "2026-09-20T21:00:00Z", "NEW REPORT", playback=True)
        self.catalog = self.archive / "catalog.json"
        self.write_catalog()

    def write_report(self, folder_name, generated, body, attempt_id="CSP-0002-A001", lineage=None,
                     playback=False):
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
        manifest = {
            "schema": "ksp-continuum-chronicle-manifest/v1",
            "generatedUtc": generated,
            "missionId": "CSP-0002",
            "attemptId": attempt_id,
            "vehicleDesignId": "CV-0001-R01",
            "siteId": "SITE-MIN-001",
            "title": "Minmus Survey 1",
            "outcome": "failed",
            "report": "index.html",
            "media": [{
                "filename": "landing.png", "status": "confirmed", "width": 1920, "height": 1080,
            }],
        }
        if lineage is not None:
            manifest.update(lineage)
        if playback:
            player = folder / "telemetry.html"
            player.write_text(
                '<!doctype html><html><head><title>Playback</title></head><body>'
                '<a href="index.html">Mission report</a><p>SELECTED TELEMETRY</p></body></html>',
                encoding="utf-8",
            )
            source_sha = "a" * 64
            manifest["sources"] = [{
                "path": "mission/mission.csv", "bytes": 123, "sha256": source_sha,
            }]
            manifest["telemetryPlayback"] = {
                "schema": "ksp-continuum-telemetry-playback/v1",
                "report": "telemetry.html",
                "rows": 42,
                "sourceSha256": source_sha,
                "sha256": hashlib.sha256(player.read_bytes()).hexdigest(),
            }
        (folder / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

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

    def write_qualification(self, folder="qualification-reports/CSP-0002-A007"):
        destination = self.archive / folder
        destination.mkdir(parents=True)
        (destination / "scope.txt").write_text(
            "Start-context windows; private raw source /private/runtime.\n", encoding="utf-8")
        (destination / "status.txt").write_text("timeout\ncompletedWindows=1\n", encoding="utf-8")
        (destination / "coast-start.txt").write_text(
            "vessel=00000000-0000-0000-0000-000000000007\n"
            "situation=ORBITING\nparts=17\nut=268882.4\nthrottleCommand=0\n", encoding="utf-8")
        marker = {
            "schema": "ksp-continuum-markers/v2", "status": "cleanup-error",
            "requestedFrames": 2, "completedFrames": 2, "contextMisalignedFrames": 0,
            "frames": [
                {"contextAligned": True, "wallMilliseconds": 4.0, "throttleCommand": 0.0,
                 "packed": False, "body": "Minmus", "situation": "ORBITING"},
                {"contextAligned": True, "wallMilliseconds": 6.0, "throttleCommand": 0.0,
                 "packed": False, "body": "Minmus", "situation": "ORBITING"},
            ],
            "wallIntervals": {"count": 2, "minimum": 4.0, "maximum": 6.0, "mean": 5.0,
                              "p50": 5.0, "p95": 5.9, "p99": 5.98},
            "markers": [{
                "name": "Physics.Simulate", "status": "available-no-samples",
                "recorderAvailableAtStart": True, "availabilityDetail": "valid",
                "summary": {"availableFrames": 2, "unavailableFrames": 0,
                            "observedFrames": 0, "zeroBlockFrames": 2, "totalBlocks": 0,
                            "observedMilliseconds": None},
            }],
        }
        shadow = {
            "schema": "ksp-continuum-flight-shadow/v1", "status": "complete",
            "submitted": 1, "accepted": 1, "stale": 0, "wallSeconds": 0.25,
            "samples": [{"status": "accepted", "bodies": 10, "captureMilliseconds": 0.2,
                         "submitMilliseconds": 0.03, "collectMilliseconds": 0.04,
                         "collectAuditMilliseconds": 0.1, "handoffWallMilliseconds": 4.0}],
            "firstAcceptedBatch": [{"id": index, "mass": 1.0} for index in range(10)],
            "firstAcceptedTick": 1,
        }
        marker_path = destination / "coast-markers.json"
        marker_path.write_text(json.dumps(marker), encoding="utf-8")
        (destination / "coast-shadow.json").write_text(json.dumps(shadow), encoding="utf-8")
        return folder, destination, marker_path

    def test_builds_connected_site_and_enhanced_latest_attempt(self):
        old_hash = hashlib.sha256((self.archive / "render-latest" / "index.html").read_bytes()).hexdigest()
        old_player_hash = hashlib.sha256((self.archive / "render-latest" / "telemetry.html").read_bytes()).hexdigest()
        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        site = self.archive / "site"

        html_files = sorted(site.glob("**/*.html"))
        self.assertEqual(12, len(html_files))
        for path in html_files:
            self.assertIn('class="site-nav"', path.read_text(encoding="utf-8"), str(path))

        home = (site / "index.html").read_text(encoding="utf-8")
        self.assertIn("Continuum &lt;Space&gt; Program", home)
        self.assertIn('href="missions/CSP-0002.html"', home)
        self.assertIn('href="attempts/CSP-0002-A001/index.html"', home)
        self.assertIn('aria-label="Minmus Survey 1 (CSP-0002-A001)"', home)
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
        self.assertIn('href="telemetry.html"', attempt)
        self.assertIn('alt="Landing screenshot"', attempt)
        self.assertIn("landing.png · confirmed · 1920 × 1080", attempt)
        self.assertTrue((site / "attempts" / "CSP-0002-A001" / "media" / "landing.png").is_file())
        player = site / "attempts" / "CSP-0002-A001" / "telemetry.html"
        self.assertIn("SELECTED TELEMETRY", player.read_text(encoding="utf-8"))
        self.assertIn('href="index.html"', player.read_text(encoding="utf-8"))
        self.assertIn('class="site-nav"', player.read_text(encoding="utf-8"))
        self.assertEqual(
            old_player_hash,
            hashlib.sha256((self.archive / "render-latest" / "telemetry.html").read_bytes()).hexdigest(),
        )
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
        self.assertEqual(
            {
                "report": "attempts/CSP-0002-A001/telemetry.html",
                "sourceSha256": old_player_hash,
                "generatedSha256": hashlib.sha256(player.read_bytes()).hexdigest(),
            },
            marker["reports"][0]["telemetryPlayback"],
        )
        self.assertEqual(64, len(marker["generatorSha256"]))
        self.assertEqual(64, len(marker["templateSha256"]))
        self.assertEqual(64, len(marker["catalogSha256"]))

    def test_legacy_reports_without_playback_remain_valid(self):
        latest = self.archive / "render-latest"
        data = json.loads((latest / "manifest.json").read_text())
        data.pop("telemetryPlayback")
        (latest / "manifest.json").write_text(json.dumps(data))
        (latest / "telemetry.html").unlink()

        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        attempt = self.archive / "site" / "attempts" / "CSP-0002-A001"
        self.assertFalse((attempt / "telemetry.html").exists())
        self.assertNotIn('href="telemetry.html"', (attempt / "index.html").read_text())

    def test_links_validated_qualification_summary_from_experiment_both_ways(self):
        folder, source_directory, _marker_path = self.write_qualification()
        self.write_catalog(lambda data: data["experiments"][0].update(
            qualification_report=folder, name="Final descent \\1 profile"))

        result = self.run_generator()

        self.assertEqual(0, result.returncode, result.stderr)
        site = self.archive / "site"
        experiment = (site / "experiments" / "EXP-CSP-0002-DESCENT.html").read_text()
        self.assertIn('href="EXP-CSP-0002-DESCENT-qualification.html"', experiment)
        qualification = (site / "experiments" / "EXP-CSP-0002-DESCENT-qualification.html").read_text()
        self.assertIn('class="site-nav"', qualification)
        self.assertIn('href="EXP-CSP-0002-DESCENT.html"', qualification)
        self.assertIn("Final descent \\1 profile", qualification)
        self.assertIn("Physics.Simulate", qualification)
        self.assertIn("Available No Samples", qualification)
        self.assertIn("1 / 1 / 0 / 0", qualification)
        self.assertIn("First accepted body count", qualification)
        self.assertIn("No versioned physical input recorded", qualification)
        self.assertIn("Profiler status</dt><dd>Cleanup Error", qualification)
        self.assertNotIn("/private/runtime", qualification)

        source_items = []
        for path in sorted(source_directory.iterdir()):
            source_items.append({"path": path.name, "bytes": len(path.read_bytes()),
                                 "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        source_manifest_sha = hashlib.sha256(json.dumps(
            source_items, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()

        marker = json.loads((site / "site-manifest.json").read_text())
        self.assertEqual([{
            "experimentId": "EXP-CSP-0002-DESCENT",
            "report": "experiments/EXP-CSP-0002-DESCENT-qualification.html",
            "source": folder,
            "sourceManifestSha256": source_manifest_sha,
            "generatedSha256": hashlib.sha256(
                (site / "experiments" / "EXP-CSP-0002-DESCENT-qualification.html").read_bytes()
            ).hexdigest(),
        }], marker["qualificationReports"])

    def test_rejects_unsafe_or_untrusted_qualification_report(self):
        folder, _source_directory, marker_path = self.write_qualification()
        cases = (
            ("../private", None, "unsafe qualification report path"),
            (folder, lambda data: data.update(schema="wrong"), "unsupported marker schema"),
        )
        original = json.loads(marker_path.read_text())
        for index, (reference, mutate, message) in enumerate(cases):
            with self.subTest(message=message):
                marker_path.write_text(json.dumps(original))
                if mutate:
                    data = json.loads(marker_path.read_text())
                    mutate(data)
                    marker_path.write_text(json.dumps(data))
                self.write_catalog(lambda data, value=reference:
                                   data["experiments"][0].update(qualification_report=value))
                result = self.run_generator(self.archive / ("site-q-%d" % index))
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
                self.assertFalse((self.archive / ("site-q-%d" % index)).exists())

        marker_path.write_text(json.dumps(original))
        linked = self.archive / "qualification-link"
        linked.symlink_to(self.archive / "qualification-reports", target_is_directory=True)
        self.write_catalog(lambda data: data["experiments"][0].update(
            qualification_report="qualification-link/CSP-0002-A007"))
        result = self.run_generator(self.archive / "site-q-link")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("symbolic", result.stderr)

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

    def test_links_checkpoint_child_to_parent_and_parent_to_descendant(self):
        digest = "6993575ee97eb6f9529d6f91187e63909dc2aad424e477905a4a86a063445240"
        self.write_report(
            "child-render", "2026-09-21T01:00:00Z", "CHILD REPORT",
            attempt_id="CSP-0002-A002",
            lineage={
                "parentAttemptId": "CSP-0002-A001",
                "parentCheckpoint": "minmus-orbit-e05676be2e38432caf5eac0b1baf79b3",
                "parentCheckpointSha256": digest,
            },
        )
        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        site = self.archive / "site" / "attempts"
        child = (site / "CSP-0002-A002" / "index.html").read_text(encoding="utf-8")
        parent = (site / "CSP-0002-A001" / "index.html").read_text(encoding="utf-8")
        self.assertIn('href="../CSP-0002-A001/index.html"', child)
        self.assertIn("Started from", child)
        self.assertIn("minmus-orbit-e05676be2e38432caf5eac0b1baf79b3", child)
        self.assertIn(digest, child)
        self.assertIn("does not establish deterministic replay", child)
        self.assertIn('href="../CSP-0002-A002/index.html"', parent)
        self.assertIn("Checkpoint descendants", parent)
        self.assertIn(digest, parent)
        marker = json.loads((self.archive / "site" / "site-manifest.json").read_text())
        child_record = next(item for item in marker["reports"] if item["attemptId"] == "CSP-0002-A002")
        self.assertEqual("CSP-0002-A001", child_record["parentAttemptId"])

    def test_preserves_legacy_parent_checkpoint_without_lineage_link(self):
        for folder in ("render-old", "render-latest"):
            path = self.archive / folder / "manifest.json"
            data = json.loads(path.read_text())
            data["parentCheckpoint"] = "legacy-free-label"
            path.write_text(json.dumps(data))
        result = self.run_generator()
        self.assertEqual(0, result.returncode, result.stderr)
        attempt = (self.archive / "site" / "attempts" / "CSP-0002-A001" / "index.html").read_text()
        self.assertNotIn("Started from", attempt)
        self.assertNotIn("Checkpoint descendants", attempt)

    def test_rejects_incomplete_or_missing_checkpoint_lineage(self):
        digest = "c" * 64
        cases = (
            ({"parentAttemptId": "CSP-0002-A999"}, "incomplete checkpoint lineage"),
            ({
                "parentAttemptId": "CSP-0002-A999", "parentCheckpoint": "minmus-orbit",
                "parentCheckpointSha256": digest,
            }, "references missing parent attempt"),
            ({
                "parentAttemptId": "CSP-0002-A001", "parentCheckpoint": "minmus-orbit",
                "parentCheckpointSha256": digest,
            }, "may not parent itself"),
        )
        latest_path = self.archive / "render-latest" / "manifest.json"
        old_path = self.archive / "render-old" / "manifest.json"
        original_latest = json.loads(latest_path.read_text())
        original_old = json.loads(old_path.read_text())
        for index, (lineage, message) in enumerate(cases):
            with self.subTest(message=message):
                latest_path.write_text(json.dumps(original_latest))
                old_path.write_text(json.dumps(original_old))
                data = dict(original_latest)
                data.update(lineage)
                latest_path.write_text(json.dumps(data))
                if message == "references missing parent attempt":
                    old_data = dict(original_old)
                    old_data.update(lineage)
                    old_path.write_text(json.dumps(old_data))
                result = self.run_generator(self.archive / ("site-%d" % index))
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)

    def test_rejects_lineage_disagreement_between_renderings(self):
        digest = "d" * 64
        path = self.archive / "render-old" / "manifest.json"
        data = json.loads(path.read_text())
        data.update({
            "parentAttemptId": "CSP-0002-A000",
            "parentCheckpoint": "minmus-orbit",
            "parentCheckpointSha256": digest,
        })
        path.write_text(json.dumps(data))
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("renderings disagree on lineage", result.stderr)

    def test_rejects_checkpoint_lineage_cycle(self):
        digest = "e" * 64
        for folder in ("render-old", "render-latest"):
            path = self.archive / folder / "manifest.json"
            data = json.loads(path.read_text())
            data.update({
                "parentAttemptId": "CSP-0002-A002",
                "parentCheckpoint": "minmus-orbit",
                "parentCheckpointSha256": digest,
            })
            path.write_text(json.dumps(data))
        self.write_report(
            "child-render", "2026-09-21T01:00:00Z", "CHILD REPORT",
            attempt_id="CSP-0002-A002",
            lineage={
                "parentAttemptId": "CSP-0002-A001",
                "parentCheckpoint": "minmus-orbit",
                "parentCheckpointSha256": digest,
            },
        )
        result = self.run_generator()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checkpoint lineage cycle", result.stderr)


if __name__ == "__main__":
    unittest.main()
