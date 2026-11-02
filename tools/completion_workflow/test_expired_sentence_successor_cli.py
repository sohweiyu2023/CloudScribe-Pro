"""Real isolated helper/installer contract for a NEW explicitly approved test window."""
import copy,json
from pathlib import Path
import unittest,uuid
import completion_register as m
import test_sentence_replan_cli as s

class ExpiredSentenceCliTests(unittest.TestCase):
    def test_expired_effectful_scope_actual_entrypoint_preserves_accounting(self):
        f,old,old_scope,events,proposal,parts,first,grant,policy,reuse,media,before,folder=s.SentenceCliTests.prepare(self,whole_end_after=45)
        db_before=f.f.db.read_bytes();cutoff=s.synthetic_expired_window(f,old)
        saved=f.h_path.read_bytes();h=copy.deepcopy(f.h);h.pop('WindowApprovalSha256');f.h_path.write_text(json.dumps(h));f.h_pin=s.sha(f.h_path.read_bytes())
        result=f.command('inspect-successor');self.assertNotEqual(0,result[0]);self.assertFalse(Path(f.config['RegisterPath']).exists())
        f.h_path.write_bytes(saved);f.h_pin=s.sha(saved)
        result=f.command('inspect-successor');self.assertEqual(0,result[0],result)
        self.assertFalse(Path(f.config['RegisterPath']).exists());f.activate()
        result=f.command('status');self.assertEqual(0,result[0],result)
        self.assertEqual(cutoff,result[1]['deadlineUtc']);self.assertEqual(before['admissions'],result[1]['totalAdmissions'])
        self.assertEqual(before['refresh'],result[1]['refreshes']);self.assertEqual(db_before,f.f.db.read_bytes())
        pilot=copy.deepcopy(policy);pilot.pop('WholeArticleGeneration');pilot.update(SchemaVersion=2,SourceApprovalSha256=f.scope['approvalSha256'],
            ExpiresAtUtc=cutoff,MaximumNewAttempts=2,MaximumAdditionalReservationMinorUnits=2,
            Articles=[dict(Proposal=proposal,ReusedAudio=reuse,AllowedSegments=[dict(CoverageOrdinal=x['coverageOrdinal'],MaximumMinorUnits=1) for x in first])])
        pilot['Budget'].update(ProjectCommittedMinorUnits=173,AllocationCommittedMinorUnits=43,ProjectCeilingMinorUnits=289,AllocationCeilingMinorUnits=159)
        path=folder/'new-window-pilot.json';path.write_text(json.dumps(pilot));op=str(uuid.uuid4())
        result=f.command('claim-completion',path,op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',path,op)[0]);f.install_policy(path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',path,op,dict(outcome='Succeeded'))[0])
        self.assertEqual(db_before,f.f.db.read_bytes())
        with old._open() as db:
            self.assertEqual(events,db.execute('SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?',(len(events),)).fetchall())
            self.assertTrue(old._read(db)['retired'])
        self.assertNotEqual(0,f.command('claim-successor')[0])

if __name__=='__main__':unittest.main()
