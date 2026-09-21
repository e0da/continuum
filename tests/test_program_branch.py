import json
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tools" / "KspContinuum.ProgramBranchToy"


class ProgramBranchTests(unittest.TestCase):
    def run_toy(self, seed="20260921"):
        result = subprocess.run(
            ["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--", seed],
            cwd=ROOT, text=True, capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)

    def test_receipt_is_reproducible_and_schedule_independent(self):
        first = self.run_toy()
        second = self.run_toy()
        self.assertEqual(first, second)
        self.assertEqual("ksp-continuum-program-branch-receipt/v1", first["schema"])
        self.assertEqual(256, first["samples"])
        self.assertTrue(first["serialParallelEqual"])
        self.assertTrue(first["staleRejected"])
        self.assertEqual(list(range(256)), [item["sample"] for item in first["scenarios"]])
        self.assertEqual(256, len({item["hash"] for item in first["scenarios"]}))

    def test_seed_changes_scenarios_without_changing_root(self):
        first = self.run_toy("1")
        second = self.run_toy("2")
        self.assertEqual(first["rootHash"], second["rootHash"])
        self.assertNotEqual(first["scenarios"], second["scenarios"])


if __name__ == "__main__":
    unittest.main()
