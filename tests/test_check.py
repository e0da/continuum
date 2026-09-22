import os
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
CHECK = ROOT / "scripts" / "check"


class CheckContractTests(unittest.TestCase):
    def run_lane(self, lane):
        environment = dict(os.environ, KSP_CONTINUUM_CHECK_DRY_RUN="1")
        return subprocess.run(
            [str(CHECK), lane],
            cwd="/",
            env=environment,
            capture_output=True,
            text=True,
        )

    def test_portable_lane_preserves_the_current_commands(self):
        result = self.run_lane("portable")
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = result.stdout.splitlines()
        self.assertEqual(len([line for line in commands if line.startswith("dotnet run")]), 18)
        self.assertIn(
            "dotnet run --project tests/KspContinuum.StructuralResponse.Tests -c Release",
            commands,
        )
        self.assertIn(
            "dotnet run --project tests/KspContinuum.RigidCluster.Tests -c Release",
            commands,
        )
        self.assertIn(
            "dotnet run --project tests/KspContinuum.RigidCluster6Dof.Tests -c Release",
            commands,
        )
        self.assertIn(
            "python3 -m unittest discover -s tests -p test_*.py -v", commands
        )
        self.assertTrue(
            any(line.startswith("python3 tools/curve-bounds/experiment.py --output ") for line in commands)
        )
        self.assertIn(
            "python3 -m unittest discover -s tools/orbit-fixture -p test_*.py -v",
            commands,
        )

    def test_field_and_structural_lanes_preserve_the_current_commands(self):
        field = self.run_lane("field")
        self.assertEqual(field.returncode, 0, field.stderr)
        self.assertEqual(
            field.stdout.splitlines(),
            [
                "python3 -m pip install -r tools/field-gravity/requirements.txt",
                "python3 -m unittest discover -s tests -p test_field*.py -v",
            ],
        )

        structural = self.run_lane("structural")
        self.assertEqual(structural.returncode, 0, structural.stderr)
        self.assertEqual(
            structural.stdout.splitlines(),
            [
                "cmake -S tools/structural-bench -B artifacts/structural-ci -DCMAKE_BUILD_TYPE=Release",
                "cmake --build artifacts/structural-ci --parallel 2",
                "ctest --test-dir artifacts/structural-ci --output-on-failure",
            ],
        )

    def test_all_runs_each_lane_and_unknown_lane_fails(self):
        all_lanes = self.run_lane("all")
        self.assertEqual(all_lanes.returncode, 0, all_lanes.stderr)
        self.assertIn("python3 -m pip install -r tools/field-gravity/requirements.txt", all_lanes.stdout)
        self.assertIn("ctest --test-dir artifacts/structural-ci --output-on-failure", all_lanes.stdout)

        unknown = self.run_lane("unknown")
        self.assertEqual(unknown.returncode, 64)
        self.assertIn("{portable|field|structural|all}", unknown.stderr)


if __name__ == "__main__":
    unittest.main()
