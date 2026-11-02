"""Safety-only reverse journal tests. Restoring old pins does not restore paid authority."""
import concurrent.futures
from pathlib import Path
import unittest
import uuid
import completion_register as m
import test_interpreter_binding as fixture

class InterpreterWithdrawalTests(unittest.TestCase):
    def setUp(self):
        self.f=fixture.InterpreterRegisterTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.r=self.f.r
    def prepare(self,complete=True):
        self.f.claim()
        if complete:self.f.complete()
        state=self.f.state()
        self.record=dict(SchemaVersion=1,WithdrawalId=str(uuid.uuid4()),WithdrawalApprovalSha256=m.digest({'distinct':'withdrawal approval'}),RepairTransitionId=self.f.t['TransitionId'],RepairIdentitySha256=fixture.H,ScopeSha256=self.r.scope_sha,ExpectedHeadSha256=state['head'],PreviousGateSha256=self.f.t['NewGateSha256'],RestoredGateSha256=self.f.t['PreviousGateSha256'],BackupPath=str(self.r.path.parent/'private-original-backup'),PreviousInstallSha256=fixture.H,PreviousRequiredSha256=fixture.H)
        return state
    def claim(self):return self.r.claim_interpreter_withdrawal(self.record,fixture.H,self.record['WithdrawalId'])
    def check(self):return self.r.check_interpreter_withdrawal(self.record,fixture.H,self.record['WithdrawalId'])
    def complete(self):return self.r.complete_interpreter_withdrawal(self.record,fixture.H,self.record['WithdrawalId'],fixture.H)
    def test_completed_repair_reverse_is_terminal_and_preserves_every_limit(self):
        before=self.prepare();self.claim();self.check();self.complete();after=self.f.state()
        for key in ('scope','refresh','admissions','helperSourceRepairs','buildTransitions','startUtc','startMono','currentBuild'):
            self.assertEqual(before[key],after[key],key)
        self.assertTrue(after['stopped']);self.assertEqual('InterpreterBindingWithdrawn',after['stopReason'])
        self.assertEqual(self.f.t['PreviousGateSha256'],after['currentGate']);self.assertIsNone(after['pending'])
        with self.assertRaises(m.Rejected):self.r.claim('refresh',fixture.H,str(uuid.uuid4()))
        with self.assertRaises(m.Rejected):self.claim()
        with self.assertRaises(m.Rejected):self.complete()
    def test_pending_repair_withdraws_without_completing_or_erasing_it(self):
        self.prepare(False);self.claim();self.complete()
        self.assertEqual(self.f.t['TransitionId'],self.f.state()['interpreterRepair']['id'])
        with self.assertRaises(m.Rejected):self.f.complete()
    def test_restart_keeps_terminal_pending_and_only_exact_withdrawal_may_finish(self):
        self.prepare();self.claim()
        reopened=m.Register(self.r.path,self.r.scope_sha,lambda:self.f.f.clock.now)
        reopened.check_interpreter_withdrawal(self.record,fixture.H,self.record['WithdrawalId'])
        wrong=dict(self.record,WithdrawalApprovalSha256=m.digest({'unreviewed':'other'}))
        with self.assertRaises(m.Rejected):reopened.check_interpreter_withdrawal(wrong,fixture.H,self.record['WithdrawalId'])
        self.complete()
    def test_identity_approval_old_gate_and_backup_schema_mismatches_rejected(self):
        self.prepare()
        for key,value in [('RepairIdentitySha256',fixture.NEW),('RestoredGateSha256',fixture.NEW),('PreviousGateSha256',fixture.H),('WithdrawalApprovalSha256',self.f.t['RepairApprovalSha256']),('ExpectedHeadSha256',fixture.H),('BackupPath','relative')]:
            original=self.record
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                self.record=dict(original,**{key:value});self.claim()
            self.record=original
    def test_actual_refresh_prevents_withdrawal(self):
        self.prepare();c=self.r.claim('refresh',fixture.H,str(uuid.uuid4()))
        self.record['ExpectedHeadSha256']=self.f.state()['head']
        with self.assertRaises(m.Rejected):self.claim()
    def test_expiry_can_only_withdraw_not_renew(self):
        self.prepare();state=self.f.state();self.f.f.clock.now=m.utc(state['scope']['endUtc'])+1
        self.r.monotonic=lambda:state['startMono']+self.f.f.clock.now-state['startUtc']
        self.claim();self.complete();self.assertEqual(state['scope'],self.f.state()['scope'])
    def test_clock_rollback_never_restores_pins(self):
        self.prepare();self.f.f.clock.now-=1
        with self.assertRaises(m.Rejected):self.claim()
        self.assertIsNone(self.f.state()['interpreterWithdrawal'])
    def test_concurrent_withdrawal_has_one_owner(self):
        self.prepare()
        def claim():
            try:self.claim();return True
            except (m.Rejected,OSError):return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:self.assertEqual(1,sum(pool.map(lambda _:claim(),range(2))))

if __name__=='__main__':unittest.main()
