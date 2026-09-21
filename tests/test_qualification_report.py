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

    @staticmethod
    def force_report():
        vec = lambda x=0: {"X": x, "Y": 0, "Z": 0}
        session = "11111111-1111-1111-1111-111111111111"
        return {
            "schema": "ksp-continuum-part-force-observation/v1",
            "provider": "continuum-part-census", "providerVersion": "1",
            "sessionId": session, "nativeAssemblyMvid": session,
            "status": "interrupted", "cleanupStatus": "removed-owned-callbacks",
            "timingStage": "TimingManager.FashionablyLate", "partCensusStatus": "captured-component-only",
            **{key: "unavailable-not-observed" for key in ("stockAerodynamics", "gravity", "contactsAndConstraints", "directRigidbodyWrites")},
            "maxBatches": 16, "maxPartsPerBatch": 512, "maxHoldersPerPart": 64,
            "maxRetainedParts": 2048, "maxRetainedHolders": 4096,
            "completedBatches": 1, "retainedParts": 1, "retainedHolders": 1,
            "skippedCallbacks": 0, "physicsEpochs": 1, "originEvents": 0,
            "batches": [{"context": {
                "providerSession": session, "vesselId": session, "scene": "FLIGHT",
                "referenceFrame": "unity-world-at-FashionablyLate", "frameKey": "FLIGHT:1:0",
                "unityFrame": 10, "mainBodyInstanceId": -42, "physicsEpoch": 1,
                "topologyGeneration": 1, "frameGeneration": 1, "floatingOriginEvents": 0,
                "universalTime": 123, "fixedTimeSeconds": 1.0, "stepSeconds": 0.02,
                "rawKrakensbaneFrameVelocity": vec(),
            }, "parts": [{"flightId": 7, "parentFlightId": 0, "nativePartInstanceId": -8,
                "nativeRigidbodyInstanceId": -9, "rigidBodyPartFlightId": 7,
                "force": vec(1), "torque": vec(2), "worldCenterOfMass": vec(3),
                "forces": [{"force": vec(4), "worldPosition": vec(5), "worldLeverArm": vec(2)}]}]}],
        }

    def test_component_force_coverage_is_distinct_from_total_force(self):
        path = self.source / "coast-markers.json"
        report = json.loads(path.read_text())
        report["partForces"] = self.force_report()
        path.write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        capture = summary["phases"][0]["profiler"].get("partForces")
        self.assertIsNotNone(capture, "component observations must reach the consumer")
        self.assertEqual(1, capture["capturedBatches"])
        self.assertEqual(1, capture["capturedParts"])
        self.assertEqual(1, capture["capturedPositionForces"])
        page = (self.output / "index.html").read_text()
        self.assertIn("Part force observations", page)
        self.assertIn("not total force", page)
        self.assertIn("No part-force observation recorded", page)

    def test_empty_force_capture_does_not_claim_usable_observations(self):
        path = self.source / "coast-markers.json"
        report = self.marker_report(0)
        capture = self.force_report()
        capture.update(status="bounded", partCensusStatus="not-observed", batches=[],
                       completedBatches=0, retainedParts=0, retainedHolders=0)
        report["partForces"] = capture
        path.write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        self.assertFalse(summary["phases"][0]["profiler"]["partForces"]["usableComponentCapture"])
        self.assertIn("diagnostic only", (self.output / "index.html").read_text())

    def test_rejects_malformed_force_coverage_and_cleanup_contradictions(self):
        import copy
        mutations = [
            lambda r: r.update(status="complete"),
            lambda r: r["batches"][0]["parts"][0].update(parentFlightId=999),
            lambda r: r["batches"][0]["parts"][0].update(rigidBodyPartFlightId=999),
            lambda r: r.update(cleanupStatus="registered"),
            lambda r: r.update(gravity="captured"),
            lambda r: r.update(retainedParts=2),
            lambda r: r.update(cleanupStatus="cleanup-error"),
            lambda r: r["batches"][0]["context"].update(providerSession="wrong"),
            lambda r: r["batches"][0]["context"].update(stepSeconds=0),
            lambda r: r["batches"][0]["parts"][0]["force"].update(X=float("nan")),
            lambda r: r["batches"][0]["parts"].append(copy.deepcopy(r["batches"][0]["parts"][0])),
            lambda r: r["batches"][0]["parts"][0].update(worldCenterOfMass=None),
            lambda r: r["batches"][0]["parts"][0]["forces"][0].update(worldLeverArm=None),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                self.output = self.root / ("force-invalid-output-%d" % index)
                path = self.source / "coast-markers.json"
                report = self.marker_report(0)
                report["partForces"] = self.force_report()
                mutate(report["partForces"])
                path.write_text(json.dumps(report))
                result = self.run_report()
                self.assertNotEqual(0, result.returncode, "invalid force contract accepted")

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

    def loop_report(self):
        return {
            "schema": "ksp-continuum-playerloop/v1", "status": "observed",
            "integrityStatus": "verified-at-boundaries", "cleanupStatus": "removed-owned-hooks",
            "clockFrequency": 1000000, "detail": "/private/runtime/not-exported",
            "measurementScope": "untrusted raw prose",
            "scopes": [{"name": "UnityEngine.PlayerLoop.FixedUpdate+" + name,
                        "status": "observed", "droppedSamples": 0, "sequenceErrors": 0,
                        "samples": [{"frame": 1, "fixedTimeSeconds": .02, "fixedDeltaSeconds": .02,
                                     "elapsedTicks": 1000},
                                    {"frame": 1, "fixedTimeSeconds": .04, "fixedDeltaSeconds": .02,
                                     "elapsedTicks": 3000}],
                        "milliseconds": None}
                       for name in ("PhysicsFixedUpdate", "ScriptRunBehaviourFixedUpdate")],
        }

    def loop_report_v2(self):
        report = self.loop_report()
        report["schema"] = "ksp-continuum-playerloop/v2"
        report["scopes"] = []
        definitions = (
            ("UnityEngine.PlayerLoop.FixedUpdate", "fixed", "contains-fixed-children"),
            ("UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate", "fixed", "contained-by-fixed"),
            ("UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate", "fixed", "contained-by-fixed"),
            ("UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate", "frame", "separate-frame-phase"),
            ("UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate", "frame", "separate-frame-phase"),
        )
        for name, domain, overlap in definitions:
            report["scopes"].append({
                "name": name, "timeDomain": domain, "overlap": overlap,
                "status": "observed", "droppedSamples": 0, "sequenceErrors": 0,
                "samples": [{"frame": 1, "fixedTimeSeconds": .02,
                             "fixedDeltaSeconds": .02, "elapsedTicks": 1000}],
                "milliseconds": None,
            })
        return report

    def test_playerloop_samples_are_separate_from_frame_intervals(self):
        report = self.marker_report(0)
        report["playerLoop"] = self.loop_report()
        (self.source / "coast-markers.json").write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        loop = summary["phases"][0]["profiler"]["playerLoop"]
        self.assertEqual(2.0, loop["scopes"][0]["milliseconds"]["mean"])
        self.assertEqual(1, loop["scopes"][0]["sampledFrames"])
        self.assertIsNone(summary["phases"][1]["profiler"]["playerLoop"])
        page = (self.output / "index.html").read_text()
        self.assertIn("Fixed-step loop brackets", page)
        self.assertIn("Elapsed wall time, not exclusive CPU time", page)
        self.assertNotIn("untrusted raw prose", page)
        self.assertNotIn("/private/runtime", page)

    def test_playerloop_v2_requires_named_hierarchy_and_warns_about_overlap(self):
        report = self.marker_report(0)
        report["playerLoop"] = self.loop_report_v2()
        (self.source / "coast-markers.json").write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        loop = json.loads((self.output / "summary.json").read_text())["phases"][0]["profiler"]["playerLoop"]
        self.assertEqual("ksp-continuum-playerloop/v2", loop["schema"])
        self.assertEqual(5, len(loop["scopes"]))
        self.assertTrue(loop["hasOverlappingScopes"])
        self.assertIn("Overlapping scopes", (self.output / "index.html").read_text())

    def test_playerloop_v2_rejects_wrong_domain_overlap_or_scope_set(self):
        mutations = (
            lambda loop: loop["scopes"][0].update(timeDomain="frame"),
            lambda loop: loop["scopes"][1].update(overlap="separate-frame-phase"),
            lambda loop: loop["scopes"][4].update(name="UnityEngine.PlayerLoop.Update"),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                self.output = self.root / ("bad-v2-loop-%d" % index)
                report = self.marker_report(0)
                report["playerLoop"] = self.loop_report_v2()
                mutate(report["playerLoop"])
                (self.source / "coast-markers.json").write_text(json.dumps(report))
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())

    def test_structural_counts_summarize_observed_ranges_and_unavailable_frames(self):
        report = self.marker_report(0)
        report["frames"][0].update(rigidbodies=12, joints=11, colliders=30, loadedVessels=3)
        report["frames"][1].update(rigidbodies=-1, joints=-1, colliders=32, loadedVessels=4)
        (self.source / "coast-markers.json").write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        counts = json.loads((self.output / "summary.json").read_text())["phases"][0]["profiler"]["frameContexts"]["structuralCounts"]
        self.assertEqual({"observedFrames": 1, "unavailableFrames": 1, "minimum": 12, "maximum": 12},
                         counts["rigidbodies"])
        self.assertEqual(32, counts["colliders"]["maximum"])
        page = (self.output / "index.html").read_text()
        self.assertIn("Structural count ranges", page)
        self.assertIn("rigidbodies: 12-12 (1 observed, 1 unavailable)", page)

    def test_structural_counts_require_complete_bounded_set(self):
        mutations = (
            lambda frame: frame.update(rigidbodies=1),
            lambda frame: frame.update(rigidbodies=-2, joints=1, colliders=1, loadedVessels=1),
            lambda frame: frame.update(rigidbodies=1.0, joints=1, colliders=1, loadedVessels=1),
            lambda frame: frame.update(rigidbodies=1, joints=1, colliders=1, loadedVessels=1),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                self.output = self.root / ("bad-structure-%d" % index)
                report = self.marker_report(0)
                mutate(report["frames"][0])
                (self.source / "coast-markers.json").write_text(json.dumps(report))
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())

    def test_invalidated_loop_never_exposes_usable_timing_summary(self):
        report = self.marker_report(0)
        report["playerLoop"] = self.loop_report()
        report["playerLoop"].update(status="invalid", integrityStatus="invalidated")
        for scope in report["playerLoop"]["scopes"]:
            scope["status"] = "invalid"
        (self.source / "coast-markers.json").write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        scope = summary["phases"][0]["profiler"]["playerLoop"]["scopes"][0]
        self.assertEqual(2, scope["sampleCount"])
        self.assertIsNone(scope["milliseconds"])
        self.assertIn("Timing summaries withheld", (self.output / "index.html").read_text())

    def test_loop_cleanup_failure_requires_outer_cleanup_error(self):
        report = self.marker_report(0)
        loop = report["playerLoop"] = self.loop_report()
        loop.update(status="invalid", integrityStatus="invalidated", cleanupStatus="cleanup-error")
        for scope in loop["scopes"]:
            scope["status"] = "invalid"
        path = self.source / "coast-markers.json"
        path.write_text(json.dumps(report))
        result = self.run_report()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("cleanup", result.stderr)
        self.assertFalse(self.output.exists())
        report["status"] = "cleanup-error"
        path.write_text(json.dumps(report))
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Cleanup Error", (self.output / "index.html").read_text())

    def test_loop_unavailable_empty_and_partial_sequence_failure_states(self):
        for state in ("unavailable", "no-samples", "invalid-sequence", "truncated"):
            with self.subTest(state=state):
                self.output = self.root / ("loop-state-" + state)
                report = self.marker_report(0)
                loop = report["playerLoop"] = self.loop_report()
                if state in ("unavailable", "no-samples"):
                    loop["status"] = state
                    for scope in loop["scopes"]:
                        scope.update(samples=[], status="invalid" if state == "unavailable" else "no-samples")
                    if state == "unavailable":
                        loop.update(integrityStatus="not-installed", cleanupStatus="not-installed")
                elif state == "invalid-sequence":
                    loop.update(status="invalid", integrityStatus="invalidated")
                    loop["scopes"][0].update(status="invalid", sequenceErrors=1)
                    loop["scopes"][1]["status"] = "invalid"
                else:
                    loop["scopes"][0]["droppedSamples"] = 4
                (self.source / "coast-markers.json").write_text(json.dumps(report))
                result = self.run_report()
                self.assertEqual(0, result.returncode, result.stderr)
                summary = json.loads((self.output / "summary.json").read_text())
                output = summary["phases"][0]["profiler"]["playerLoop"]
                if state == "truncated":
                    self.assertEqual(4, output["scopes"][0]["droppedSamples"])
                    self.assertIsNotNone(output["scopes"][0]["milliseconds"])
                else:
                    self.assertTrue(all(scope["milliseconds"] is None for scope in output["scopes"]))

    def test_rejects_contradictory_or_unbounded_loop_contract(self):
        mutations = [
            lambda r: r.update(schema="unknown"),
            lambda r: r.update(clockFrequency=0),
            lambda r: r.update(status="invalid"),
            lambda r: r.update(status="unavailable"),
            lambda r: r.update(scopes=[]),
            lambda r: r.update(status="observed", integrityStatus="not-installed"),
            lambda r: r["scopes"][0].update(name="untrusted/name"),
            lambda r: r["scopes"][0].update(status="no-samples"),
            lambda r: r["scopes"][0].update(sequenceErrors=1),
            lambda r: r["scopes"][0]["samples"][0].update(elapsedTicks=-1),
            lambda r: r["scopes"][0]["samples"][0].update(fixedDeltaSeconds=0),
            lambda r: r["scopes"][0].update(samples=r["scopes"][0]["samples"] * 2049),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                self.output = self.root / ("bad-loop-%d" % index)
                report = self.marker_report(0)
                report["playerLoop"] = self.loop_report()
                mutate(report["playerLoop"])
                (self.source / "coast-markers.json").write_text(json.dumps(report))
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())

    def physical_shadow_report(self):
        report = self.shadow_report(0)
        report.update(
            physicalInputSchema="ksp-continuum-rigidbody-input/v1",
            referenceFrameSchema="ksp-continuum-unity-frame-context/v1",
            aggregateForceStatus="unavailable-not-captured",
        )
        for index, sample in enumerate(report["samples"]):
            sample.update(tick=10 + index, referenceFrame="unity-world-at-capture",
                          rawKrakensbaneFrameVelocity=[0, 100, -2], physicsEpoch=index,
                          floatingOriginEventCount=0)
        for body in report["firstAcceptedBatch"]:
            body.update(rotation=[0, 0, 0, 1], angularVelocity=[0, .1, 0],
                        centerOfMass=[0, 0, 0], worldCenterOfMass=[0, 1, 0],
                        position=[0, 1, 0], velocity=[0, 0, 0], inertiaTensor=[1, 2, 0],
                        predictedPosition=[0, 1, 0], predictedVelocity=[0, 0, 0],
                        inertiaTensorRotation=[0, 0, 0, 1], constraints=0, sleeping=False,
                        force=[0, 0, 0], forceSource="synthetic-zero-not-native-measurement")
        return report

    def test_physical_capture_coverage_and_legacy_absence(self):
        path = self.source / "coast-shadow.json"
        path.write_text(json.dumps(self.physical_shadow_report()), encoding="utf-8")
        before = {item.name: item.read_bytes() for item in self.source.iterdir()}
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        coverage = summary["phases"][0]["shadow"]["physicalInput"]
        self.assertEqual(2, coverage["capturedBodies"])
        self.assertEqual(2, coverage["frameSamples"])
        self.assertEqual(2, coverage["zeroInertiaBodies"])
        self.assertEqual("unavailable-not-captured", coverage["aggregateForceStatus"])
        self.assertIsNone(summary["phases"][1]["shadow"]["physicalInput"])
        page = (self.output / "index.html").read_text()
        self.assertIn("Physical input coverage", page)
        self.assertIn("No versioned physical input recorded", page)
        self.assertIn("Native aggregate force and torque are not captured", page)
        self.assertEqual(before, {item.name: item.read_bytes() for item in self.source.iterdir()})

    def test_physical_contract_without_accepted_bodies_is_not_capture_proof(self):
        report = self.physical_shadow_report()
        report.update(accepted=0, stale=2, firstAcceptedBatch=[], firstAcceptedTick=0)
        report["samples"][0]["status"] = "stale-discarded"
        (self.source / "coast-shadow.json").write_text(json.dumps(report), encoding="utf-8")
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        self.assertEqual(0, summary["phases"][0]["shadow"]["physicalInput"]["capturedBodies"])
        page = (self.output / "index.html").read_text()
        self.assertIn("no accepted body snapshot", page)
        self.assertNotIn("Pose, angular velocity, centers of mass and principal inertia are recorded", page)

    def test_rejects_malformed_physical_capture_claims(self):
        def body_change(field, value):
            return lambda report: report["firstAcceptedBatch"][0].update({field: value})
        mutations = [
            lambda report: report.pop("referenceFrameSchema"),
            lambda report: report.update(physicalInputSchema="future/v9"),
            body_change("rotation", [0, 0, 0, 2]),
            body_change("angularVelocity", [0, 1]),
            lambda report: report["firstAcceptedBatch"][0].pop("predictedPosition"),
            body_change("predictedVelocity", [0, 0, "bad"]),
            body_change("mass", 0),
            body_change("inertiaTensor", [-1, 1, 1]),
            body_change("sleeping", 1),
            body_change("force", [1, 0, 0]),
            body_change("forceSource", "native"),
            lambda report: report["firstAcceptedBatch"][1].update(id=0),
            lambda report: report["samples"][0].update(tick=9),
            lambda report: report["samples"][0].update(bodies=3),
            lambda report: report["samples"][0].update(referenceFrame="inertial"),
            lambda report: report["samples"][0].update(rawKrakensbaneFrameVelocity=[False, 0, 0]),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                self.output = self.root / ("invalid-output-%d" % index)
                report = self.physical_shadow_report()
                mutate(report)
                (self.source / "coast-shadow.json").write_text(json.dumps(report), encoding="utf-8")
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())

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

    def test_cleanup_error_is_separate_from_complete_capture(self):
        (self.source / "shutdown.txt").write_text(
            "schema=ksp-continuum-shutdown/v1\nstatus=error\nrequestedBy=qualification\n"
            "captureStatus=complete\nhandlers=2\nhandler=mission:error\n"
            "handler=recorder:inactive\nerrors=1\n")
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text())
        self.assertEqual("complete", summary["status"])
        self.assertIn("shutdown", summary)
        self.assertEqual("error", summary["shutdown"]["status"])
        self.assertEqual(1, summary["shutdown"]["errors"])
        self.assertIn("shutdown.txt", [source["path"] for source in summary["sources"]])
        page = (self.output / "index.html").read_text()
        self.assertIn("Shutdown status: <strong>Error</strong>", page)
        self.assertIn("mission: error", page)

    def test_rejects_contradictory_or_unsafe_shutdown(self):
        base = ("schema=ksp-continuum-shutdown/v1\nstatus=error\nrequestedBy=qualification\n"
                "captureStatus=complete\nhandlers=1\nhandler=mission:error\nerrors=1\n")
        for value in (base.replace("status=error", "status=complete"),
                      base.replace("handlers=1", "handlers=2"),
                      base.replace("errors=1", "errors=0"),
                      base.replace("captureStatus=complete", "captureStatus=timeout"),
                      base.replace("mission:error", "/private/runtime:error"),
                      base + "status=error\n"):
            with self.subTest(receipt=value):
                (self.source / "shutdown.txt").write_text(value)
                result = self.run_report()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())

    def test_builds_bounded_portable_summary_without_claiming_attribution(self):
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        summary = json.loads((self.output / "summary.json").read_text(encoding="utf-8"))
        self.assertEqual("ksp-continuum-qualification-summary/v1", summary["schema"])
        self.assertEqual("qualification-session", summary["sourceSession"])
        self.assertIsNone(summary["shutdown"])
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

    def test_qualification_consumer_accepts_v2_shadow_without_claiming_residuals(self):
        report = self.shadow_report(0)
        report.update({"schema": "ksp-continuum-flight-shadow/v2", "compared": 1,
                       "comparisonSkipped": 0})
        report["samples"][0].update({"comparisonStatus": "compared",
                                      "observedComparisonAvailable": True})
        report["samples"][1].update({"comparisonStatus": "not-accepted",
                                      "observedComparisonAvailable": False})
        (self.source / "coast-shadow.json").write_text(json.dumps(report), encoding="utf-8")
        result = self.run_report()
        self.assertEqual(0, result.returncode, result.stderr)
        shadow = json.loads((self.output / "summary.json").read_text())["phases"][0]["shadow"]
        self.assertEqual("ksp-continuum-flight-shadow/v2", shadow["schema"])
        self.assertEqual(1, shadow["compared"])
        self.assertNotIn("residuals", shadow)

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
