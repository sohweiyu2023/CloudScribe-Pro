"""Synthetic pre-effect repair only; no installed app, vault or provider calls."""
import concurrent.futures
from pathlib import Path
import unittest
import uuid

import completion_register as m
import test_whole_article_continuation as whole

H=m.digest({'synthetic':'repair evidence'})
NEW=m.digest({'synthetic':'new gate'})


class InterpreterRegisterTests(unittest.TestCase):
    def setUp(self):
        self.f=whole.WholeArticleTransitionTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.f.activate();self.r=self.f.new;state=self.state()
        self.t=dict(SchemaVersion=1,TransitionKind='interpreter-binding',TransitionId=str(uuid.uuid4()),
            SourceApprovalSha256=state['scope']['approvalSha256'],RepairApprovalSha256=H,
            ScopeSha256=self.r.scope_sha,ExpectedHeadSha256=state['head'],
            PreviousConfigPath=str(self.r.path.parent/'old4.json'),PreviousGateSha256=state['currentGate'],NewGateSha256=NEW,
            PreviousBuildSha256=state['currentBuild'],NewBuildSha256=state['currentBuild'],
            PreviousPythonSha256=m.digest({'old':'python'}),NewPythonSha256=m.digest({'new':'python'}),
            InterpreterEvidencePath=str(self.r.path.parent/'evidence.json'),InterpreterEvidenceSha256=H)

    def state(self):
        with self.r._open() as connection:return self.r._read(connection)

    def claim(self):return self.r.claim_build(self.t,H,self.t['TransitionId'])
    def complete(self):return self.r.complete_build(self.t,H,self.t['TransitionId'],H)

    def test_exact_pre_effect_repair_preserves_scope_deadline_counters_and_ancestors(self):
        before=self.state()
        ancestor=self.f.old.path.read_bytes(),self.f.old.anchor.read_bytes()
        self.r.inspect_build(self.t,H,self.t['TransitionId']);self.claim()
        self.r.check_build(self.t,H,self.t['TransitionId']);self.complete()
        after=self.state()
        for key in ('scope','startUtc','startMono','refresh','admissions','resume-admission','completion-admission','currentBuild','buildTransitions'):
            self.assertEqual(before[key],after[key],key)
        self.assertEqual(1,after['helperSourceRepairs']);self.assertEqual(NEW,after['currentGate'])
        self.assertEqual(ancestor,(self.f.old.path.read_bytes(),self.f.old.anchor.read_bytes()))
        self.r.require_predecessor_retired(NEW)
        stale=m.Register(self.r.path,self.r.scope_sha,lambda:self.f.clock.now,expected_build=before['currentBuild'],expected_gate=self.t['PreviousGateSha256'])
        with self.assertRaises(m.Rejected):stale.status()

    def test_refresh_or_admission_or_pending_blocks_repair(self):
        claim=self.r.claim('refresh',H,str(uuid.uuid4()))
        with self.assertRaises(m.Rejected):self.claim()
        self.r.complete(claim['id'],dict(id=claim['id'],identitySha256=H,outcome='Succeeded',evidenceSha256=H,
            catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True))
        self.t['ExpectedHeadSha256']=self.state()['head']
        with self.assertRaises(m.Rejected):self.claim()

    def test_restart_only_exact_pending_identity_and_one_repair(self):
        self.claim();restarted=m.Register(self.r.path,self.r.scope_sha,lambda:self.f.clock.now)
        wrong=dict(self.t,RepairApprovalSha256=m.digest({'other':'approval'}))
        with self.assertRaises(m.Rejected):restarted.check_build(wrong,H,self.t['TransitionId'])
        restarted.check_build(self.t,H,self.t['TransitionId']);self.complete()
        with self.assertRaises(m.Rejected):self.complete()
        self.t.update(TransitionId=str(uuid.uuid4()),ExpectedHeadSha256=self.state()['head'],PreviousGateSha256=NEW,NewGateSha256=H)
        with self.assertRaises(m.Rejected):self.claim()

    def test_new_build_reused_approval_and_changed_scope_rejected(self):
        for field,value in [('NewBuildSha256',H),('RepairApprovalSha256',self.t['SourceApprovalSha256']),('ScopeSha256',H),('TransitionKind','helper-source')]:
            with self.subTest(field=field),self.assertRaises(m.Rejected):
                bad=dict(self.t,**{field:value});self.r.claim_build(bad,H,bad['TransitionId'])

    def test_pending_expiry_and_clock_rollback_never_renew(self):
        self.claim();self.f.clock.now-=1
        with self.assertRaises(m.Rejected):self.r.check_build(self.t,H,self.t['TransitionId'])
        self.f.clock.now=m.utc(self.f.scope['endUtc'])+1
        with self.assertRaises(m.Rejected):self.complete()
        state=self.state();self.assertIsNotNone(state['pending']);self.assertTrue(state['stopped'])

    def test_concurrency_exactly_one_claim(self):
        def attempt():
            try:self.claim();return True
            except (m.Rejected,OSError):return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1,sum(pool.map(lambda _:attempt(),range(2))))


if __name__=='__main__':unittest.main()
