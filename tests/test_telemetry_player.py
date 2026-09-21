import csv
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
from chronicle import EXPECTED_TELEMETRY_HEADER


class TelemetryPlayerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / 'mission.csv'
        self.output = self.root / 'player.html'
        self.rows = [
            ['1', '100', 'Coast', 'Minmus', 'ORBITING', '200', '210', '100', '10', '0', '2', '17', 'True', ''],
            ['2', '120', 'Landing', 'Minmus', 'FLYING', '20', '210', '0', '1', '.5', '2', '17', 'False', '</script><script>alert(1)</script>'],
        ]
        self.write()

    def tearDown(self):
        self.temp.cleanup()

    def write(self):
        with self.source.open('w', newline='') as f:
            writer = csv.writer(f)
            writer.writerow(EXPECTED_TELEMETRY_HEADER)
            writer.writerows(self.rows)

    def run_player(self):
        return subprocess.run([sys.executable, str(ROOT / 'scripts/telemetry_player.py'),
                               str(self.source), '--title', 'Test mission', '--output', str(self.output)],
                              capture_output=True, text=True)

    def test_embeds_exact_observations_and_source_hash_without_executable_data(self):
        result = self.run_player()
        self.assertEqual(result.returncode, 0, result.stderr)
        text = self.output.read_text()
        match = re.search(r'<script id="recording" type="application/json">(.*?)</script>', text, re.S)
        data = json.loads(match.group(1))
        self.assertEqual(data['sourceSha256'], hashlib.sha256(self.source.read_bytes()).hexdigest())
        self.assertEqual(data['rows'][1]['ut'], 120)
        self.assertEqual(data['rows'][1]['altitude'], 20)
        self.assertEqual(data['rows'][1]['autopilot'], self.rows[1][-1])
        self.assertNotIn('</script><script>alert', text)
        self.assertNotIn(str(self.root), text)
        self.assertIn('Recorded observations', text)

    def test_rejects_time_reversal_nonfinite_and_out_of_range_controls(self):
        for column, value in [(0, '0.5'), (1, '99'), (5, 'NaN'), (9, '2')]:
            with self.subTest(column=column):
                original = self.rows[1][column]
                self.rows[1][column] = value
                self.write()
                self.assertNotEqual(self.run_player().returncode, 0)
                self.assertFalse(self.output.exists())
                self.rows[1][column] = original

    def test_existing_output_is_preserved(self):
        self.output.write_text('keep')
        self.assertNotEqual(self.run_player().returncode, 0)
        self.assertEqual(self.output.read_text(), 'keep')


if __name__ == '__main__':
    unittest.main()
