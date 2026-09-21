import importlib.util
import json
import math
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "modal-rocket" / "experiment.py"
SPEC = importlib.util.spec_from_file_location("modal_rocket", SCRIPT)
MODAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODAL)


class ModalRocketTests(unittest.TestCase):
    def test_exact_modes_are_orthonormal_and_diagonalize_chain(self):
        vectors, eigenvalues = MODAL.modes()
        for i, left in enumerate(vectors):
            for j, right in enumerate(vectors):
                self.assertAlmostEqual(MODAL.dot(left, right), 1.0 if i == j else 0.0, places=12)
            acceleration = MODAL.chain_acceleration(left, [0.0] * MODAL.BODY_COUNT, [0.0] * MODAL.BODY_COUNT)
            for actual, component in zip(acceleration, left):
                self.assertAlmostEqual(actual, -eigenvalues[i] * component, places=10)

    def test_predeclared_smooth_pass_and_impulse_failure_are_retained(self):
        report = MODAL.build_report()
        self.assertTrue(report["qualified"])
        self.assertFalse(report["productionPhysicsQualified"])
        self.assertFalse(report["performanceWinClaimed"])
        by_name = {row["name"]: row for row in report["workloads"]}
        self.assertTrue(by_name["smooth"]["qualified"])
        self.assertFalse(by_name["localizedImpulse"]["qualified"])
        self.assertTrue(by_name["localizedImpulse"]["expectedToQualify"] is False)
        for workload in by_name.values():
            self.assertEqual(set(workload["metrics"]) & set(workload["gates"]), set(workload["gates"]))
            self.assertTrue(all(math.isfinite(value) for value in workload["metrics"].values()))
            self.assertEqual(len(workload["samples"]), 21)

    def test_receipt_is_deterministic_and_output_is_exclusive(self):
        with tempfile.TemporaryDirectory() as folder:
            first = Path(folder) / "first.json"
            second = Path(folder) / "second.json"
            subprocess.run([sys.executable, "-B", str(SCRIPT), "--output", str(first)], check=True)
            subprocess.run([sys.executable, "-B", str(SCRIPT), "--output", str(second)], check=True)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            report = json.loads(first.read_text())
            self.assertEqual(report["schema"], MODAL.SCHEMA)
            self.assertEqual(report["bounds"]["maximumBodies"], 32)
            repeated = subprocess.run(
                [sys.executable, "-B", str(SCRIPT), "--output", str(first)],
                text=True, capture_output=True)
            self.assertEqual(repeated.returncode, 1)
            self.assertIn("File exists", repeated.stderr)

    def test_missing_output_parent_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "missing" / "receipt.json"
            result = subprocess.run(
                [sys.executable, "-B", str(SCRIPT), "--output", str(output)],
                text=True, capture_output=True)
            self.assertEqual(result.returncode, 1)
            self.assertIn("output parent does not exist", result.stderr)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
