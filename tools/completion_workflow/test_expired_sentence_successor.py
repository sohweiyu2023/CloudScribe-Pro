"""Explicit synthetic new-window retirement after nonzero-effect scope expiry."""
import copy
from datetime import datetime,timedelta,timezone
import unittest
from unittest.mock import patch
import uuid
import completion_register as m
import test_sentence_replan as ordinary
from test_sentence_replan import H,G

class ExpiredSentenceTests(unittest.TestCase):
    setUp=ordinary.SentenceRegisterTests.setUp
    activate=ordinary.SentenceRegisterTests.activate

    def prepare_expired(self,reboot=False,stopped=False):
        self.clock.now=m.utc(self.before['scope']['endUtc'])+1_000_000
        self.mono=5_000_000 if reboot else self.clock.now
        self.old.monotonic=lambda:self.mono;self.new.monotonic=lambda:self.mono
        if stopped:
            with self.assertRaises(m.Rejected):self.old.claim('refresh',H,str(uuid.uuid4()))
        with self.old._open() as db:self.before=self.old._read(db)
        start=datetime.fromtimestamp(self.clock.now/1e6,timezone.utc)
        self.scope.update(startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
        self.new=m.Register(self.new.path,m.digest(self.scope),lambda:self.clock.now,lambda:self.mono)
        evidence=dict(ObservedUtcMicros=self.clock.now,ObservedMonoMicros=self.mono,TrustedUtcLowerMicros=self.clock.now-100_000,
            TrustedUtcUpperMicros=self.clock.now+100_000,PreviousLastGoodUtcMicros=self.before['lastGoodUtc'],
            WindowsBootUtc=datetime.fromtimestamp((self.before['lastGoodUtc']-1_000_000)/1e6,timezone.utc).isoformat().replace('+00:00','Z'),BootReadbackSha256=H)
        self.h.update(TransitionKind='expired-terminal-sentence-replan',ExpectedPreviousHeadSha256=self.before['head'],
            NewScopeSha256=self.new.scope_sha,WindowApprovalSha256=self.scope['approvalSha256'],ClockEpochEvidence=evidence)

    def test_nonzero_effect_old_scope_retired_with_all_counters_and_no_reset(self):
        self.prepare_expired();self.activate()
        self.new.require_predecessor_retired(G)
        result=self.new.status();self.assertEqual((2,2,1),(result['refreshes'],result['totalAdmissions'],result['resumeAdmissions']))
        with self.old._open() as db:old=self.old._read(db)
        for key in ('scope','refresh','admissions','resume-admission','lastGoodUtc'):
            self.assertEqual(self.before[key],old[key],key)
        self.assertEqual('ApprovedExpiredTerminalWindow',old['retirement']['clockCondition'])
        self.assertLess(m.utc(result['deadlineUtc']),m.utc(self.scope['endUtc']))
        with self.assertRaises(m.Rejected):self.old.claim('refresh',H,str(uuid.uuid4()))
        # Existing used slots remain consumed in the new bounded window.
        for _ in range(22):
            op=self.new.claim('refresh',H,str(uuid.uuid4()));r=ordinary.receipt(op)
            r.update(catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True)
            self.new.complete(op['id'],r)
        with self.assertRaisesRegex(m.Rejected,'OperationLimit'):self.new.claim('refresh',H,str(uuid.uuid4()))

    def test_reboot_and_prior_expiry_stop_do_not_repair_old_authority(self):
        self.prepare_expired(True,True);self.activate();self.new.require_predecessor_retired(G)
        with self.old._open() as db:old=self.old._read(db)
        self.assertEqual(self.before['lastMono'],old['lastMono'])
        self.assertTrue(old['stopped']);self.assertTrue(old['retired'])

    def test_persisted_expiry_stop_without_reboot_is_retired(self):
        self.prepare_expired(False,True)
        self.assertEqual('WindowExpired',self.before['stopReason'])
        self.activate();self.new.require_predecessor_retired(G)
        self.assertEqual(2,self.new.status()['totalAdmissions'])

    def test_nonclock_failure_stop_cannot_be_renewed(self):
        op=self.old.claim('refresh',H,str(uuid.uuid4()))
        failed=ordinary.receipt(op);failed['outcome']='Stopped';self.old.complete(op['id'],failed)
        self.prepare_expired();self.h['UsedRefreshes']=3
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.assertFalse(self.new.path.exists())

    def test_missing_or_stale_approval_rejected_before_new_register(self):
        self.prepare_expired()
        for change in ('missing','wrong','old'):
            h=copy.deepcopy(self.h)
            if change=='missing':h.pop('WindowApprovalSha256')
            elif change=='wrong':h['WindowApprovalSha256']=G
            else:h['SourceApprovalSha256']=h['WindowApprovalSha256']=self.before['scope']['approvalSha256']
            with self.subTest(change=change),self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,h,H)
        self.assertFalse(self.new.path.exists())

    def test_active_or_changed_window_and_expired_old_handoff_are_not_renewal(self):
        self.prepare_expired()
        h=copy.deepcopy(self.h);h['ClockEpochEvidence']['TrustedUtcLowerMicros']=m.utc(self.before['scope']['endUtc'])-1
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,h,H)
        h=copy.deepcopy(self.h);h.pop('WindowApprovalSha256');h['TransitionKind']='same-window-sentence-replan-after-reboot'
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,h,H)
        changed=dict(self.scope,endUtc=(datetime.fromisoformat(self.scope['endUtc'].replace('Z','+00:00'))+timedelta(hours=1)).isoformat().replace('+00:00','Z'))
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,changed,self.h,H)

    def test_pending_or_unknown_old_operation_blocks_new_window(self):
        op=self.old.claim('refresh',H,str(uuid.uuid4()))
        self.prepare_expired()
        self.h['UsedRefreshes']=3
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.assertFalse(self.new.path.exists());self.assertIsNotNone(op)

    def test_interrupted_activation_and_replay_remain_single_use(self):
        self.prepare_expired(True);self.old.claim_successor(self.new,self.scope,self.h,H)
        original=self.new._append
        def fail(db,event,head):
            if event['type']=='activate':raise OSError('synthetic interruption')
            return original(db,event,head)
        with patch.object(self.new,'_append',side_effect=fail),self.assertRaises(OSError):
            self.old.complete_successor(self.new,self.scope,self.h,H,G)
        self.old.complete_successor(self.new,self.scope,self.h,H,G);self.new.require_predecessor_retired(G)
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,H,G)

    def test_pending_retirement_cannot_finish_after_new_deadline(self):
        self.prepare_expired(True);self.old.claim_successor(self.new,self.scope,self.h,H)
        delta=6*3600*1_000_000;self.clock.now+=delta;self.mono+=delta
        with self.assertRaisesRegex(m.Rejected,'WindowExpired'):
            self.old.check_successor(self.new,self.scope,self.h,H)
        with self.new._open() as db:self.assertFalse(self.new._read(db)['activated'])

    def test_actual_second_clock_reset_is_rejected(self):
        self.prepare_expired(True);self.activate();self.mono=0
        with self.assertRaisesRegex(m.Rejected,'ClockRollback'):self.new.claim('refresh',H,str(uuid.uuid4()))

    def test_new_deadline_and_second_reset_still_fail_closed(self):
        self.prepare_expired();self.activate()
        delta=m.utc(self.scope['endUtc'])-self.clock.now
        self.clock.now+=delta;self.mono+=delta
        with self.assertRaisesRegex(m.Rejected,'WindowExpired'):self.new.claim('refresh',H,str(uuid.uuid4()))

if __name__=='__main__':unittest.main()
