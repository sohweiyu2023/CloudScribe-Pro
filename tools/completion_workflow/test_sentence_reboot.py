"""Explicit synthetic reboot retirement; never repairs an active old clock guard."""
import copy
from datetime import datetime, timezone
import unittest
from unittest.mock import patch
import uuid
import completion_register as m
import test_sentence_replan as ordinary
from test_sentence_replan import H, G

class RebootRetirementTests(unittest.TestCase):
    setUp=ordinary.SentenceRegisterTests.setUp
    activate=ordinary.SentenceRegisterTests.activate
    # Reuse fixture, not the ordinary test cases.
    def reboot(self, stopped=False):
        self.clock.now += 60_000_000
        self.mono = 5_000_000
        self.old.monotonic=lambda:self.mono
        self.new.monotonic=lambda:self.mono
        if stopped:
            with self.assertRaises(m.Rejected):self.old.claim('refresh',H,str(uuid.uuid4()))
        with self.old._open() as db:self.before=self.old._read(db)
        self.h.update(TransitionKind='same-window-sentence-replan-after-reboot',ExpectedPreviousHeadSha256=self.before['head'])
        self.h['ClockEpochEvidence']={
            'ObservedUtcMicros':self.clock.now,'ObservedMonoMicros':self.mono,
            'TrustedUtcLowerMicros':self.clock.now-100_000,'TrustedUtcUpperMicros':self.clock.now+100_000,
            'PreviousLastGoodUtcMicros':self.before['lastGoodUtc'],
            'WindowsBootUtc':datetime.fromtimestamp((self.clock.now-1_000_000)/1e6,timezone.utc).isoformat().replace('+00:00','Z'),
            'BootReadbackSha256':H}

    def test_explicit_reboot_carries_counters_and_shortens_native_deadline(self):
        self.reboot();self.activate()
        with self.new._open() as db:state=self.new._read(db)
        self.assertEqual(self.before['scope']['endUtc'],state['scope']['endUtc'])
        self.assertEqual((2,2,1),(state['refresh'],state['admissions'],state['resume-admission']))
        cutoff=m.epoch_deadline(state['scope'],self.h['ClockEpochEvidence'])
        self.assertLess(cutoff,m.utc(state['scope']['endUtc']))
        m.require_epoch_policy_deadline(state,datetime.fromtimestamp(cutoff/1e6,timezone.utc).isoformat())
        with self.assertRaisesRegex(m.Rejected,'ClockEpochPolicyDeadline'):
            m.require_epoch_policy_deadline(state,state['scope']['endUtc'])
        self.new.require_predecessor_retired(G)
        permit=self.new.claim('refresh',H,str(uuid.uuid4()))
        self.assertEqual(self.new.status()['deadlineUtc'],permit['deadlineUtc'])
        r=ordinary.receipt(permit);r.update(catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True)
        self.new.complete(permit['id'],r)
        with self.old._open() as db:
            old=self.old._read(db)
            self.assertEqual(self.before['lastMono'],old['lastMono'])
            self.assertEqual(self.before['lastGoodUtc'],old['lastGoodUtc'])
        self.mono+=1_000_000;self.clock.now+=1_000_000
        self.assertFalse(self.new.status()['requiresReconciliation'])
        self.mono=0
        with self.assertRaises(m.Rejected):self.new.claim('refresh',H,str(uuid.uuid4()))

    def test_preexisting_clock_stop_can_only_be_retired_by_explicit_evidence(self):
        self.reboot(True);self.activate();self.new.require_predecessor_retired(G)

    def test_invalid_evidence_rejected_without_successor(self):
        self.reboot()
        for key,value in [('ObservedMonoMicros',True),('TrustedUtcUpperMicros',self.clock.now+6_000_000),
                          ('PreviousLastGoodUtcMicros',0),('BootReadbackSha256','0'*64),
                          ('WindowsBootUtc',self.scope['startUtc'])]:
            h=copy.deepcopy(self.h);h['ClockEpochEvidence'][key]=value
            with self.subTest(key=key),self.assertRaises((m.Rejected,ValueError)):
                self.old.inspect_successor(self.new,self.scope,h,H)
        self.assertFalse(self.new.path.exists())

    def test_pending_work_and_stale_evidence_are_not_renewed(self):
        self.reboot();self.clock.now+=601_000_000;self.mono+=601_000_000
        with self.assertRaisesRegex(m.Rejected,'ClockEpochEvidence'):
            self.old.inspect_successor(self.new,self.scope,self.h,H)

    def test_interrupt_after_old_retirement_recovers_same_handoff_only(self):
        self.reboot();self.old.claim_successor(self.new,self.scope,self.h,H)
        original=self.new._append
        def fail(db,event,head):
            if event['type']=='activate':raise OSError('synthetic interruption')
            return original(db,event,head)
        with patch.object(self.new,'_append',side_effect=fail),self.assertRaises(OSError):
            self.old.complete_successor(self.new,self.scope,self.h,H,G)
        self.old.complete_successor(self.new,self.scope,self.h,H,G);self.new.require_predecessor_retired(G)
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,H,G)

    def test_six_node_typed_interpreter_ancestry_is_verified(self):
        import test_expired_interpreter_successor as typed
        f=typed.ExpiredInterpreterSuccessorTests();f.setUp();self.addCleanup(f.doCleanups);f.activate()
        self.old=f.new;self.clock=f.clock
        with self.old._open() as db:self.before=self.old._read(db)
        path=self.old.path.parent/'sentence-after-typed4.db'
        self.scope=dict(f.scope,schema=5,approvalSha256=H,maximumNewAttempts=114,plansSha256=G,
            acceptedEvidenceSha256=H,wholeArticleGrantSha256=G,predecessorRegisterPath=str(self.old.path),
            predecessorScopeSha256=self.old.scope_sha,registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        self.h.update(TransitionId=self.scope['transitionId'],SourceApprovalSha256=H,
            PreviousGateSha256=self.before['currentGate'],PreviousScopeSha256=self.old.scope_sha,
            ExpectedPreviousHeadSha256=self.before['head'],NewScopeSha256=self.new.scope_sha,
            OriginalManifestSha256=self.before['scope']['plansSha256'],UsedRefreshes=0,UsedAdmissions=0,UsedResumeAdmissions=0)
        self.activate();self.new.require_predecessor_retired(G)

    def test_deadline_and_utc_uncertainty_never_extend_authority(self):
        self.reboot();self.activate()
        delta=m.utc(self.scope['endUtc'])-self.clock.now-50_000
        self.clock.now+=delta;self.mono+=delta
        with self.assertRaisesRegex(m.Rejected,'WindowExpired'):self.new.claim('refresh',H,str(uuid.uuid4()))

if __name__=='__main__':unittest.main()
