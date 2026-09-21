import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
PROJECT=ROOT/'tools/KspContinuum.WorldlineTubeToy'

class WorldlineTubeToyTests(unittest.TestCase):
    def test_conservative_screen_retains_oracle_contacts_and_unknowns(self):
        with tempfile.TemporaryDirectory() as temp:
            output=Path(temp)/'receipt.json'
            run=subprocess.run(['dotnet','run','--project',PROJECT,'-c','Release','--','--output',output],cwd=ROOT,capture_output=True,text=True,timeout=60)
            self.assertEqual(0,run.returncode,run.stdout+run.stderr)
            report=json.loads(output.read_text())
            self.assertEqual('ksp-continuum-worldline-tube-toy/v1',report['schema'])
            self.assertTrue(report['qualified'])
            self.assertEqual(0,report['falseNegatives'])
            self.assertGreaterEqual(report['falsePositives'],1)
            rows={row['name']:row for row in report['cases']}
            for name in ('accelerated-crossing','crossing','tangent','uncertain-burn'):
                self.assertTrue(rows[name]['oracleContact'])
                self.assertEqual('candidate',rows[name]['status'])
            self.assertEqual('clear',rows['straight-clear']['status'])
            self.assertEqual('clear',rows['near-miss']['status'])
            self.assertEqual('expired',rows['expired']['status'])
            self.assertEqual('unknown',rows['unknown-bound']['status'])
            self.assertEqual('unknown',rows['unknown-predicate']['status'])
            before=output.read_bytes()
            second=Path(temp)/'second.json'
            repeated=subprocess.run(['dotnet','run','--project',PROJECT,'-c','Release','--','--output',second],cwd=ROOT,capture_output=True,text=True,timeout=60)
            self.assertEqual(0,repeated.returncode,repeated.stdout+repeated.stderr)
            self.assertEqual(before,second.read_bytes())
            duplicate=subprocess.run(['dotnet','run','--project',PROJECT,'-c','Release','--','--output',output],cwd=ROOT,capture_output=True,text=True,timeout=60)
            self.assertNotEqual(0,duplicate.returncode)
            self.assertEqual(before,output.read_bytes())

if __name__=='__main__': unittest.main()
