"""Exercise the runnable layout experiment through its public CLI."""

import json
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tools" / "KspContinuum.LayoutBench"


class LayoutBenchTests(unittest.TestCase):
    def run_bench(self, *arguments):
        return subprocess.run(
            ["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--"]
            + list(arguments),
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=90,
        )

    def test_compares_sealed_layouts_on_both_kernels(self):
        run = self.run_bench("--bodies", "32", "--samples", "3", "--seed", "73")
        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        report = json.loads(run.stdout)
        self.assertEqual("ksp-continuum-layout-bench/v1", report["schema"])
        self.assertEqual([32], report["configuration"]["bodyCounts"])
        self.assertEqual(3, report["configuration"]["samples"])
        self.assertEqual(8, report["configuration"]["aosoaBlockWidth"])
        self.assertTrue(report["immutableLogicalCaptures"])
        self.assertFalse(report["poolingUsed"])
        self.assertFalse(report["stockPhysicsSpeedupMeasured"])
        self.assertEqual(64, len(report["configurationSha256"]))
        self.assertEqual(64, len(report["environmentSha256"]))

        case = report["cases"][0]
        self.assertEqual(64, len(case["fixtureSha256"]))
        expected_pairs = {
            (workload, strategy)
            for workload in ("free-body", "neighbor-chain")
            for strategy in ("object-aos", "soa", "aosoa-8")
        }
        self.assertEqual(
            expected_pairs,
            {(row["workload"], row["strategy"]) for row in case["results"]},
        )
        for order_group in case["measuredStrategyOrders"]:
            self.assertIn(order_group["workload"], {"free-body", "neighbor-chain"})
            self.assertEqual(3, len(order_group["orders"]))
            for order in order_group["orders"]:
                self.assertEqual(
                    {"object-aos", "soa", "aosoa-8"}, set(order)
                )
        hashes = {}
        for row in case["results"]:
            for field in (
                "packingMilliseconds",
                "kernelMilliseconds",
                "consumeMilliseconds",
                "endToEndMilliseconds",
                "packingAllocatedBytes",
                "kernelAllocatedBytes",
                "consumeAllocatedBytes",
                "endToEndAllocatedBytes",
            ):
                values = row[field]
                self.assertEqual(3, len(values), field)
                self.assertTrue(all(value >= 0 for value in values), field)
            self.assertLessEqual(row["maxPositionError"], 1e-12)
            self.assertLessEqual(row["maxVelocityError"], 1e-12)
            self.assertEqual(64, len(row["outputSha256"]))
            hashes.setdefault(row["workload"], set()).add(row["outputSha256"])
        self.assertEqual({"free-body": 1, "neighbor-chain": 1},
                         {name: len(values) for name, values in hashes.items()})

    def test_seed_reproduces_orders_and_evidence_hashes(self):
        arguments = ("--bodies", "32", "--samples", "2", "--seed", "101")
        first = self.run_bench(*arguments)
        second = self.run_bench(*arguments)
        self.assertEqual(0, first.returncode, first.stderr)
        self.assertEqual(0, second.returncode, second.stderr)
        left, right = json.loads(first.stdout), json.loads(second.stdout)
        self.assertEqual(left["configurationSha256"], right["configurationSha256"])
        self.assertEqual(left["cases"][0]["fixtureSha256"],
                         right["cases"][0]["fixtureSha256"])
        self.assertEqual(left["cases"][0]["measuredStrategyOrders"],
                         right["cases"][0]["measuredStrategyOrders"])
        self.assertEqual(
            [(row["workload"], row["strategy"], row["outputSha256"])
             for row in left["cases"][0]["results"]],
            [(row["workload"], row["strategy"], row["outputSha256"])
             for row in right["cases"][0]["results"]],
        )

    def test_rejects_work_outside_declared_bounds(self):
        for arguments, message in (
            (("--bodies", "33"), "bodies"),
            (("--samples", "0"), "samples"),
            (("--seed", "-1"), "seed"),
        ):
            with self.subTest(arguments=arguments):
                run = self.run_bench(*arguments)
                self.assertNotEqual(0, run.returncode)
                self.assertIn(message, run.stderr.lower())


if __name__ == "__main__":
    unittest.main()
