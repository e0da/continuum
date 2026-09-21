import hashlib
import json
import subprocess
import struct
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "chronicle.py"


class ChronicleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.mission = self.root / "mission-session-original"
        self.inputs = self.root / "inputs-session-original"
        self.mission.mkdir()
        self.inputs.mkdir()
        self.write_fixture()

    def tearDown(self):
        self.temporary.cleanup()

    def write_fixture(self):
        (self.mission / "mission.txt").write_text(
            "status=running\n"
            "save=private-save-name\n"
            "craft=Ships/VAB/Kerbal X.craft\n"
            "mechjebAssemblyVersion=2.15.0.0\n"
            "mechjebFileVersion=2.15.3.0\n"
            "inputDirectory=/private/example/inputs-session-original\n"
            "status=passed\n"
            "reason=Landed safely.\n",
            encoding="utf-8",
        )
        (self.mission / "events.txt").write_text(
            "100 SpaceCenter\n105 Ascent\n205 Done\n", encoding="utf-8"
        )
        (self.mission / "mission.csv").write_text(
            "wall_s,ut_s,phase,body,situation,altitude_m,apoapsis_m,periapsis_m,"
            "surface_speed_mps,throttle,stage,parts,packed,autopilot\n"
            "0,100,SpaceCenter,,,,,,,,,,,\"\"\n"
            "5,105,Ascent,Kerbin,FLYING,1000,2000,-500000,250,1,5,30,False,\"ASCENT\"\n"
            "15,205,Done,Minmus,LANDED,4359,4360,-59800,0.01,0,2,17,False,\"IDLE\"\n",
            encoding="utf-8",
        )
        (self.mission / "screenshots.csv").write_text(
            "105,requested,launch.png\n105.1,png-written,launch.png\n"
            "200,requested,survey.png\n200.1,png-below-required-resolution,survey.png,1280,720\n",
            encoding="utf-8",
        )
        png = (
            b"\x89PNG\r\n\x1a\n"
            + struct.pack(">I", 13)
            + b"IHDR"
            + struct.pack(">II", 1280, 720)
            + b"\x08\x02\x00\x00\x00"
            + b"\x00\x00\x00\x00"
            + b"\x00\x00\x00\x00IEND\xaeB\x60\x82"
        )
        (self.mission / "launch.png").write_bytes(png)
        (self.mission / "survey.png").write_bytes(png)
        (self.mission / "survey.csv").write_text("wall_s,site\n1,candidate\n", encoding="utf-8")
        (self.mission / "terrain.csv").write_text("latitude_deg,longitude_deg,height_m\n0,0,1\n", encoding="utf-8")
        (self.inputs / "session-end.txt").write_text(
            "status=Recording stopped; files saved in PluginData.\nsegments=2\n",
            encoding="utf-8",
        )
        for index, samples in enumerate((1000, 25)):
            stem = "segment-%05d" % index
            (self.inputs / (stem + ".txt")).write_text(
                "startUt=%d\nsamples=%d\n" % (100 + index, samples), encoding="utf-8"
            )
            (self.inputs / (stem + ".csv")).write_text(
                "schema,ksp-continuum-input-timeline/v1\n"
                "duration,1\n"
                "track,name,min,max\n"
                "track,mainThrottle,0,1\n"
                "key,track,time,value,mode,control1,control2\n"
                "key,mainThrottle,0,0,step,0,0\n"
                "event,time,name,value\n",
                encoding="utf-8",
            )

    def metadata(self, **changes):
        data = {
            "schema": "ksp-continuum-chronicle-metadata/v1",
            "mission_id": "CSP-0001",
            "name": "Minmus <Pathfinder>",
            "attempt_id": "CSP-0001-A003",
            "vehicle_design_id": "CV-0001-R01",
            "objective": "Land <script>alert(1)</script> on Minmus.",
            "configuration": "Stock craft under MechJeb control.",
            "anomalies": ["Touchdown attitude was observed sideways."],
            "next_experiment": "Target a named site and measure attitude.",
        }
        data.update(changes)
        path = self.root / "metadata.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def run_generator(self, output, metadata=None, include_inputs=True):
        command = [
            sys.executable,
            str(SCRIPT),
            "--mission",
            str(self.mission),
            "--output",
            str(output),
            "--metadata",
            str(metadata or self.metadata()),
        ]
        if include_inputs:
            command.extend(["--inputs", str(self.inputs)])
        return subprocess.run(command, cwd=ROOT, text=True, capture_output=True)

    def test_generates_escaped_relocatable_report_and_manifest(self):
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)

        page = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("CSP-0001-A003", page)
        self.assertIn("Minmus &lt;Pathfinder&gt;", page)
        self.assertIn("&lt;script&gt;alert(1)&lt;/script&gt;", page)
        self.assertNotIn("<script>alert(1)</script>", page)
        self.assertNotIn("private-save-name", page)
        self.assertNotIn("/private/example", page)
        for section in (
            "objective",
            "configuration",
            "timeline",
            "media",
            "measurements",
            "outcome",
            "anomalies",
            "next-experiment",
        ):
            self.assertIn('id="%s"' % section, page)
        self.assertIn("10.00 s", page)
        self.assertIn("100.00 s", page)
        self.assertIn('src="media/launch.png"', page)
        self.assertIn('href="media/launch.png"', page)
        self.assertIn('aria-label="Open full-size Launch screenshot"', page)
        self.assertIn('href="media/survey.png"', page)
        self.assertIn("Below required 1920 × 1080 capture resolution · 1280 × 720", page)
        self.assertIn('class="grid media-grid"', page)
        self.assertIn(".media figcaption { padding-top: 10px; overflow-wrap: anywhere; }", page)
        self.assertIn("1280 × 720", page)
        self.assertEqual((self.mission / "launch.png").read_bytes(), (output / "media" / "launch.png").read_bytes())

        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual("mission-v1", manifest["templateVersion"])
        self.assertEqual("CSP-0001-A003", manifest["attemptId"])
        self.assertEqual("Minmus <Pathfinder>", manifest["title"])
        self.assertEqual("passed", manifest["outcome"])
        self.assertEqual("index.html", manifest["report"])
        self.assertEqual(64, len(manifest["templateSha256"]))
        self.assertEqual(64, len(manifest["generatorSha256"]))
        self.assertEqual("CSP-0001-A003", manifest["attemptAlias"])
        self.assertEqual("mission-session-original", manifest["sourceSessions"]["mission"])
        self.assertEqual("inputs-session-original", manifest["sourceSessions"]["inputs"])
        self.assertEqual(
            {"status": "included", "session": "inputs-session-original"},
            manifest["inputEvidence"],
        )
        self.assertNotIn(str(self.root), json.dumps(manifest))
        self.assertTrue(all(len(item["sha256"]) == 64 for item in manifest["sources"]))
        self.assertIn("mission/survey.csv", {item["path"] for item in manifest["sources"]})
        self.assertIn("mission/terrain.csv", {item["path"] for item in manifest["sources"]})
        self.assertEqual(
            {"filename": "launch.png", "status": "confirmed", "width": 1280, "height": 720},
            manifest["media"][0],
        )
        self.assertEqual(
            {"filename": "survey.png", "status": "below-required-resolution", "width": 1280, "height": 720},
            manifest["media"][1],
        )

    def test_hashes_optional_checkpoint_and_mechjeb_evidence_without_copying_it(self):
        evidence = {
            "checkpoint-load-resources.csv": b"resource,amount\nElectricCharge,98\n",
            "checkpoint-load-resources.txt": b"PRIVATE_RESOURCE_VALUE load receipt\n",
            "checkpoint-acquisition-resources.csv": b"resource,amount\nLiquidFuel,12\n",
            "checkpoint-acquisition-resources.txt": b"acquisition receipt\n",
            "mechjeb-settings.csv": b"setting,value\nlandingTolerance,1\n",
        }
        for name, content in evidence.items():
            (self.mission / name).write_bytes(content)

        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        sources = {item["path"]: item for item in manifest["sources"]}
        for name, content in evidence.items():
            item = sources["mission/" + name]
            self.assertEqual(len(content), item["bytes"])
            self.assertEqual(hashlib.sha256(content).hexdigest(), item["sha256"])
            self.assertFalse((output / name).exists())
        self.assertNotIn("PRIVATE_RESOURCE_VALUE", (output / "index.html").read_text(encoding="utf-8"))
        self.assertNotIn("PRIVATE_RESOURCE_VALUE", json.dumps(manifest))

    def test_rejects_oversized_optional_checkpoint_evidence(self):
        with (self.mission / "checkpoint-load-resources.csv").open("wb") as stream:
            stream.truncate(16 * 1024 * 1024 + 1)
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("file exceeds size limit", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_unsafe_media_path_without_creating_output(self):
        (self.mission / "screenshots.csv").write_text(
            "105,requested,../escape.png\n105.1,png-written,../escape.png\n",
            encoding="utf-8",
        )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unsafe screenshot filename", result.stderr)
        self.assertFalse(output.exists())

    def test_reports_missing_and_unconfirmed_captures_honestly(self):
        (self.mission / "screenshots.csv").write_text(
            "105,requested,missing.png\n105.1,png-written,missing.png\n"
            "205,requested,orbit.png\n235,unconfirmed,orbit.png\n",
            encoding="utf-8",
        )
        (self.mission / "launch.png").unlink()
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)
        page = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("Confirmed by receipt; source PNG missing", page)
        self.assertIn("Capture remained unconfirmed", page)
        self.assertFalse((output / "media").exists())

    def test_refuses_to_overwrite_existing_output(self):
        output = self.root / "chronicle"
        output.mkdir()
        sentinel = output / "keep.txt"
        sentinel.write_text("keep", encoding="utf-8")
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("already exists", result.stderr)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))

    def test_rejects_malformed_telemetry(self):
        (self.mission / "mission.csv").write_text("wrong,header\n", encoding="utf-8")
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("mission.csv header", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_attempt_from_another_mission(self):
        output = self.root / "chronicle"
        result = self.run_generator(
            output, self.metadata(attempt_id="CSP-0002-A003")
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("attempt_id must belong to mission_id", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_reported_png_dimensions_that_do_not_match_file(self):
        (self.mission / "screenshots.csv").write_text(
            "105,requested,launch.png\n105.1,png-written,launch.png,1920,1080\n",
            encoding="utf-8",
        )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("reported dimensions do not match", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_symbolic_linked_source_file(self):
        outside = self.root / "outside-survey.csv"
        outside.write_text("private source", encoding="utf-8")
        (self.mission / "survey.csv").unlink()
        (self.mission / "survey.csv").symlink_to(outside)
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("source file may not be a symbolic link", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_metadata_that_disagrees_with_native_receipt_ids(self):
        original = (self.mission / "mission.txt").read_text(encoding="utf-8")
        correct = {
            "missionId": "CSP-0001",
            "attemptId": "CSP-0001-A003",
            "vehicleDesignId": "CV-0001-R01",
            "siteId": "SITE-MIN-001",
        }
        mismatches = {
            "missionId": "CSP-9999",
            "attemptId": "CSP-0001-A999",
            "vehicleDesignId": "CV-9999-R01",
            "siteId": "SITE-MIN-999",
        }
        for index, (receipt_key, mismatch) in enumerate(mismatches.items()):
            with self.subTest(receipt_key=receipt_key):
                values = dict(correct)
                values[receipt_key] = mismatch
                receipt = "".join("%s=%s\n" % item for item in values.items())
                (self.mission / "mission.txt").write_text(original + receipt, encoding="utf-8")
                output = self.root / ("mismatch-%d" % index)
                result = self.run_generator(output, self.metadata(site_id="SITE-MIN-001"))
                self.assertNotEqual(0, result.returncode)
                self.assertIn("does not match mission receipt", result.stderr)
                self.assertFalse(output.exists())

    def test_requires_metadata_site_when_native_receipt_names_one(self):
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write(
                "missionId=CSP-0001\nattemptId=CSP-0001-A003\n"
                "vehicleDesignId=CV-0001-R01\nsiteId=SITE-MIN-001\n"
            )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("site_id does not match mission receipt", result.stderr)
        self.assertFalse(output.exists())

    def test_accepts_matching_native_receipt_ids(self):
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write(
                "missionId=CSP-0001\nattemptId=CSP-0001-A003\n"
                "vehicleDesignId=CV-0001-R01\nsiteId=SITE-MIN-001\n"
            )
        output = self.root / "chronicle"
        result = self.run_generator(output, self.metadata(site_id="SITE-MIN-001"))
        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_unassociated_input_directory(self):
        receipt = (self.mission / "mission.txt").read_text(encoding="utf-8")
        (self.mission / "mission.txt").write_text(
            receipt.replace("inputs-session-original", "inputs-other-session"), encoding="utf-8"
        )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("input directory does not match mission receipt", result.stderr)
        self.assertFalse(output.exists())

    def test_rejects_inputs_when_mission_has_no_input_receipt(self):
        receipt = (self.mission / "mission.txt").read_text(encoding="utf-8")
        receipt = "\n".join(
            line for line in receipt.splitlines() if not line.startswith("inputDirectory=")
        ) + "\n"
        (self.mission / "mission.txt").write_text(receipt, encoding="utf-8")
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not declare inputDirectory", result.stderr)
        self.assertFalse(output.exists())

    def test_associates_relocated_inputs_from_windows_receipt_basename(self):
        receipt = (self.mission / "mission.txt").read_text(encoding="utf-8")
        (self.mission / "mission.txt").write_text(
            receipt.replace(
                "/private/example/inputs-session-original",
                "C:\\KSP\\PluginData\\inputs-session-original",
            ),
            encoding="utf-8",
        )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_reports_omitted_input_evidence_explicitly(self):
        output = self.root / "chronicle"
        result = self.run_generator(output, include_inputs=False)
        self.assertEqual(0, result.returncode, result.stderr)
        page = (output / "index.html").read_text(encoding="utf-8")
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertIn("Input evidence was not supplied for this report", page)
        self.assertNotIn('<div class="label">Input segments</div>', page)
        self.assertEqual({"status": "not-supplied"}, manifest["inputEvidence"])

    def test_redacts_absolute_paths_from_receipt_reason(self):
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write(
                "reason=IOException at '/Users/example/private-KSP/checkpoint.sfs' "
                "and 'C:\\Private\\KSP\\checkpoint.sfs'.\n"
            )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)
        page = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("redacted local path", page)
        self.assertNotIn("/Users/example", page)
        self.assertNotIn("C:\\Private", page)

    def test_does_not_display_unrecognized_craft_receipt_path(self):
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write("craft=..\\private\\named-save.craft\n")
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)
        page = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("Unrecognized craft receipt value", page)
        self.assertNotIn("named-save", page)

    def test_rejects_unsafe_source_session_basename(self):
        unsafe_mission = self.root / "mission-private session"
        self.mission.rename(unsafe_mission)
        self.mission = unsafe_mission
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("invalid mission session directory name", result.stderr)
        self.assertFalse(output.exists())

    def test_imports_complete_native_checkpoint_lineage(self):
        digest = "A1" * 32
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write(
                "parentAttemptId=CSP-0001-A002\n"
                "parentCheckpoint=minmus-orbit\n"
                "parentCheckpointSha256=%s\n" % digest
            )
        output = self.root / "chronicle"
        result = self.run_generator(output)
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual("CSP-0001-A002", manifest["parentAttemptId"])
        self.assertEqual("minmus-orbit", manifest["parentCheckpoint"])
        self.assertEqual(digest.lower(), manifest["parentCheckpointSha256"])
        page = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("Parent attempt", page)
        self.assertIn("CSP-0001-A002", page)
        self.assertIn("minmus-orbit", page)
        self.assertIn("Checkpoint SHA-256", page)
        self.assertIn(digest.lower(), page)
        self.assertEqual(1, page.count("does not establish deterministic replay"))

    def test_rejects_incomplete_or_invalid_native_checkpoint_lineage(self):
        original = (self.mission / "mission.txt").read_text(encoding="utf-8")
        cases = (
            ("parentAttemptId=CSP-0001-A002\n", "incomplete checkpoint lineage"),
            ("parentAttemptId=CSP-0001-A003\nparentCheckpoint=minmus-orbit\nparentCheckpointSha256=%s\n" % ("a" * 64),
             "may not parent itself"),
            ("parentAttemptId=CSP-0001-A002\nparentCheckpoint=../orbit\nparentCheckpointSha256=%s\n" % ("a" * 64),
             "invalid parentCheckpoint"),
            ("parentAttemptId=CSP-0001-A002\nparentCheckpoint=minmus-orbit\nparentCheckpointSha256=not-a-digest\n",
             "invalid parentCheckpointSha256"),
        )
        for index, (receipt, message) in enumerate(cases):
            with self.subTest(message=message):
                (self.mission / "mission.txt").write_text(original + receipt, encoding="utf-8")
                output = self.root / ("lineage-invalid-%d" % index)
                result = self.run_generator(output)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
                self.assertFalse(output.exists())

    def test_rejects_native_checkpoint_label_that_disagrees_with_metadata(self):
        with (self.mission / "mission.txt").open("a", encoding="utf-8") as stream:
            stream.write(
                "parentAttemptId=CSP-0001-A002\n"
                "parentCheckpoint=minmus-orbit\n"
                "parentCheckpointSha256=%s\n" % ("b" * 64)
            )
        output = self.root / "chronicle"
        result = self.run_generator(
            output, self.metadata(parent_checkpoint="different-checkpoint")
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("parent_checkpoint does not match mission receipt", result.stderr)
        self.assertFalse(output.exists())

    def test_preserves_legacy_free_checkpoint_without_claiming_structured_lineage(self):
        output = self.root / "chronicle"
        result = self.run_generator(
            output, self.metadata(parent_checkpoint="legacy-checkpoint.v1")
        )
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual("legacy-checkpoint.v1", manifest["parentCheckpoint"])
        self.assertIsNone(manifest["parentAttemptId"])
        self.assertIsNone(manifest["parentCheckpointSha256"])


if __name__ == "__main__":
    unittest.main()
