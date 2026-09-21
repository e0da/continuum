"""Outside-in checks against actual native trajectory output, using Python's analytic oracle."""
import json
import math
import subprocess
import sys
import unittest

BINARY = sys.argv.pop(1)


class StructuralReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads(subprocess.check_output([BINARY], text=True, timeout=60))

    def test_pinned_engine_and_measurement_boundary(self):
        r = self.report
        self.assertEqual(r.get("schema"), "ksp-continuum-structural/v1")
        self.assertEqual(r["joltCommit"], "e77f175595e64cb44218cc9d9d56fc365ad0e36a")
        self.assertFalse(r["stockPhysicsSpeedupMeasured"])
        self.assertEqual(r["workerThreads"], 0)
        self.assertTrue(r["doublePrecisionPositions"])

    def test_native_oscillator_matches_independent_reference(self):
        runs = self.report.get("oscillatorRuns", [])
        self.assertEqual(len(runs), 15, "Five substep configurations, three repetitions required")
        for run in runs:
            self.assertEqual(len(run["samples"]), 251)
            maximum_q = maximum_v = maximum_com = maximum_p = 0
            for index, s in enumerate(run["samples"]):
                t = index * self.report["macroStepSeconds"]
                self.assertAlmostEqual(s["timeSeconds"], t, places=10)
                # m1=2, m2=5, reduced mass=10/7, k=25, c=.5; extension .2, initially at rest.
                mu = 10 / 7
                decay = .5 / (2 * mu)
                omega = math.sqrt(25 / mu - decay * decay)
                q = .2 * math.exp(-decay * t) * (math.cos(omega * t) + decay / omega * math.sin(omega * t))
                v = -.2 * (25 / mu) / omega * math.exp(-decay * t) * math.sin(omega * t)
                maximum_q = max(maximum_q, abs(s["extensionM"] - q))
                maximum_v = max(maximum_v, abs(s["relativeVelocityMps"] - v))
                maximum_com = max(maximum_com, abs(s["centerOfMassM"]))
                maximum_p = max(maximum_p, abs(s["momentumKgMps"]))
                self.assertTrue(all(math.isfinite(x) for x in s.values()))
                self.assertGreaterEqual(s["updateMilliseconds"], 0)
            self.assertAlmostEqual(run["maxExtensionErrorM"], maximum_q, places=9)
            self.assertAlmostEqual(run["maxVelocityErrorMps"], maximum_v, places=8)
            self.assertAlmostEqual(run["maxCenterOfMassDriftM"], maximum_com, places=10)
            self.assertAlmostEqual(run["maxMomentumKgMps"], maximum_p, places=10)
            expected = maximum_q <= .005 and maximum_v <= .02 and maximum_com <= 1e-5 and maximum_p <= 1e-5
            self.assertEqual(run["qualified"], expected)

    def test_substeps_reduce_error_and_qualify(self):
        runs = self.report.get("oscillatorRuns", [])
        self.assertEqual({r["collisionSteps"] for r in runs}, {1, 2, 4, 8, 16})
        for repeat in range(3):
            indexed = {r["collisionSteps"]: r for r in runs if r["repeat"] == repeat}
            self.assertTrue(indexed[16]["qualified"], "Finest configuration must meet frozen physical-error bounds")
            self.assertLess(indexed[16]["maxExtensionErrorM"], indexed[1]["maxExtensionErrorM"])

    def test_drop_reaches_contact_and_settles(self):
        drop = self.report.get("drop", {})
        self.assertEqual(len(drop.get("samples", [])), 501)
        samples = drop["samples"]
        for s in samples:
            self.assertTrue(all(math.isfinite(x) for x in s.values()))
            self.assertGreaterEqual(s["heightM"], .225)
            if s["timeSeconds"] <= .5:
                self.assertLess(abs(s["heightM"] - (2 - .5 * 9.81 * s["timeSeconds"] ** 2)), .015)
        for s in samples[-100:]:
            self.assertLess(abs(s["heightM"] - .25), .025)
            self.assertLess(abs(s["velocityMps"]), .05)
        self.assertTrue(drop["qualified"])


if __name__ == "__main__":
    unittest.main()
