"""One explicit successor after a proven unused diagnostic refresh failure. Synthetic only."""
import copy
import errno
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import uuid
import completion_register as m
import test_sentence_diagnostic as prior
import test_expired_interpreter_successor as historical

APPROVAL=m.digest({'synthetic':'new explicit ten minute approval'})
CONSENT=m.digest({'synthetic':'new separate private response consent'})
GATE=m.digest({'synthetic':'renewal gate'})


class UnusedDiagnosticRenewalTests(unittest.TestCase):
    def test_retired_run_rejects_before_new_claim_and_replay(self):
        self.prepare(); self.activate()
        with self.old._open() as db:
            before = self.old._read(db)
        self.assertTrue(before['retired'])
        with self.assertRaisesRegex(m.Rejected, '^RunRetired$'):
            self.old.claim_successor(self.new, self.scope, self.h, m.digest(self.h))
        with self.old._open() as db:
            state = self.old._read(db)
            self.assertEqual(before['head'], state['head'])
            self.assertIsNone(state['pending'])
            event = dict(type='retire-claim', utc=self.clock.now, mono=state['lastMono'],
                observedMono=self.clock.now, id=self.h['TransitionId'], identity=m.digest(self.h),
                handoff=self.h, clockCondition='expired', clockDriftMicros=0)
            self.old._append(db, event, state['head']); db.commit(); self.old._seal(db)
            with self.assertRaisesRegex(m.Rejected, '^RunRetired$'):
                self.old._read(db)

    def setUp(self):
        self.base=prior.DiagnosticRegisterTests();self.base.setUp();self.addCleanup(self.base.doCleanups)
        self.base.activate();self.old=self.base.new;self.clock=self.base.clock

    def prepare(self, kind='refresh', complete=True, failed=True):
        operation=self.old.claim(kind,prior.H,str(uuid.uuid4()))
        if complete:
            self.old.complete(operation['id'],dict(id=operation['id'],identitySha256=prior.H,
                evidenceSha256=prior.G,outcome='Failed' if failed else 'Succeeded'))
        with self.old._open() as db:self.before=self.old._read(db)
        self.clock.now=m.utc(self.before['scope']['endUtc'])+1_000_000
        now=self.clock.now
        stamp=lambda value:datetime.fromtimestamp(value/1e6,timezone.utc).isoformat().replace('+00:00','Z')
        epoch=dict(ObservedUtcMicros=now,ObservedMonoMicros=now,TrustedUtcLowerMicros=now-100_000,
            TrustedUtcUpperMicros=now+100_000,PreviousLastGoodUtcMicros=self.before['lastGoodUtc'],
            WindowsBootUtc=self.before['scope']['startUtc'],BootReadbackSha256=CONSENT)
        path=self.old.path.parent/'unused-successor.db'
        self.scope=dict(self.before['scope'],approvalSha256=APPROVAL,privateCaptureConsentSha256=CONSENT,
            buildSha256=GATE,plansSha256=GATE,diagnosticWindowMode='fresh-10-minute',startUtc=stamp(now),
            endUtc=stamp(now+600_000_000),predecessorRegisterPath=str(self.old.path),
            predecessorScopeSha256=self.old.scope_sha,registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        self.h=dict(self.base.h,SchemaVersion=7,TransitionKind='expired-unused-diagnostic-renewal',
            TransitionId=self.scope['transitionId'],SourceApprovalSha256=APPROVAL,WindowApprovalSha256=APPROVAL,
            PreviousScopeSha256=self.old.scope_sha,PreviousGateSha256=self.before['currentGate'],
            PreviousConfigPath=str(path.parent/'old-diagnostic-gate.json'),ExpectedPreviousHeadSha256=self.before['head'],
            NewConfigPath=str(path.parent/'new-diagnostic-gate.json'),NewScopePath=str(path.parent/'new-scope.json'),
            NewGateSha256=GATE,NewScopeSha256=self.new.scope_sha,OriginalManifestSha256=self.before['scope']['plansSha256'],
            InheritedClockEpochEvidence=self.before['clockEpoch'],ClockEpochEvidence=epoch,
            UsedRefreshes=self.before['refresh'],UsedAdmissions=self.before['admissions'],UsedResumeAdmissions=self.before['resume-admission'])

    def activate(self):
        self.old.inspect_successor(self.new,self.scope,self.h,APPROVAL)
        self.old.claim_successor(self.new,self.scope,self.h,APPROVAL)
        self.old.check_successor(self.new,self.scope,self.h,APPROVAL)
        self.old.complete_successor(self.new,self.scope,self.h,APPROVAL,GATE)
        self.new.require_predecessor_retired(GATE)

    def test_explicit_successor_preserves_history_counters_and_requires_fresh_admission(self):
        self.prepare();self.activate()
        restarted=m.Register(self.new.path,self.new.scope_sha,lambda:self.clock.now)
        restarted.require_predecessor_retired(GATE)
        with restarted._open() as db:state=restarted._read(db)
        self.assertEqual(self.before['refresh'],state['refresh'])
        self.assertEqual(self.before['admissions'],state['admissions'])
        self.assertEqual(self.before['resume-admission'],state['resume-admission'])
        self.assertIsNone(state['diagnosticPolicySha256']);self.assertIsNone(state['diagnosticDispatch'])
        self.assertEqual(0,state['diagnostic-admission'])
        with self.old._open() as db:retired=self.old._read(db)
        self.assertTrue(retired['retired']);self.assertEqual('refresh',retired['lastFailedKind'])
        self.assertFalse(m.unused_diagnostic_predecessor(state)) # No second H7.
        with self.assertRaises(m.Rejected):self.old.claim('refresh',prior.H,str(uuid.uuid4()))
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):restarted.status()

    def test_absent_or_reused_approval_changed_consent_and_counter_reset_reject(self):
        self.prepare()
        for key,value in [('WindowApprovalSha256',prior.H),('ExpectedPreviousHeadSha256',prior.H),
                          ('UsedRefreshes',0),('InheritedClockEpochEvidence',{}),('SchemaVersion',6)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),APPROVAL)
        for key,value in [('approvalSha256',self.before['scope']['approvalSha256']),
                          ('privateCaptureConsentSha256',self.before['scope']['privateCaptureConsentSha256'])]:
            changed=dict(self.scope,**{key:value});target=m.Register(self.new.path,m.digest(changed),lambda:self.clock.now)
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                self.old.inspect_successor(target,changed,dict(self.h,NewScopeSha256=target.scope_sha),APPROVAL)
        self.assertFalse(self.new.path.exists())

    def test_pending_or_failed_admission_or_invalid_success_receipt_cannot_renew(self):
        for kind,complete,failed in [('refresh',False,True),('diagnostic-admission',True,True),('refresh',True,False)]:
            with self.subTest(kind=kind,complete=complete,failed=failed):
                other=UnusedDiagnosticRenewalTests();other.setUp()
                try:
                    other.prepare(kind,complete,failed)
                    with self.assertRaises(m.Rejected):other.old.inspect_successor(other.new,other.scope,other.h,APPROVAL)
                    self.assertFalse(other.new.path.exists())
                finally:other.doCleanups()

    def test_provider_started_proof_and_second_renewal_are_never_unused(self):
        self.prepare()
        for field,value in [('diagnosticDispatch',{'ordinal':1}),('lastFailedKind','diagnostic-admission'),
                            ('stopReason','ReceiptValidation'),('activated',False)]:
            state=copy.deepcopy(self.before);state[field]=value
            with self.subTest(field=field):self.assertFalse(m.unused_diagnostic_predecessor(state))
        state=copy.deepcopy(self.before);state['activation']['sentenceHandoff']['SchemaVersion']=7
        self.assertFalse(m.unused_diagnostic_predecessor(state))

    def test_expired_status_cannot_overwrite_original_failed_refresh_provenance(self):
        self.prepare()
        with self.assertRaisesRegex(m.Rejected,'RunStopped'):self.old.status()
        with self.old._open() as db:unchanged=self.old._read(db)
        self.assertEqual(self.before['head'],unchanged['head'])
        self.assertEqual('ObservedFailure',unchanged['stopReason'])
        self.assertEqual('refresh',unchanged['lastFailedKind'])
        self.activate()

    def test_partial_handoff_restart_requires_exact_packet_and_does_not_extend_cutoff(self):
        self.prepare();self.old.claim_successor(self.new,self.scope,self.h,APPROVAL)
        with self.assertRaises(m.Rejected):self.old.claim_successor(self.new,self.scope,self.h,APPROVAL)
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,APPROVAL,GATE)
        with self.new._open() as db:self.assertFalse(self.new._read(db)['activated'])

    def test_global_limit_survives_successor_and_old_anchor_tamper_fails(self):
        self.prepare();self.activate()
        saved=self.old.anchor.read_bytes();self.old.anchor.write_bytes(b'synthetic tamper')
        with self.assertRaises(m.Rejected):self.new.require_predecessor_retired(GATE)
        self.old.anchor.write_bytes(saved);self.new.require_predecessor_retired(GATE)
        with self.new._open() as db:state=self.new._read(db)
        for _ in range(24-state['refresh']):
            operation=self.new.claim('refresh',APPROVAL,str(uuid.uuid4()))
            receipt=dict(id=operation['id'],identitySha256=APPROVAL,evidenceSha256=GATE,outcome='Succeeded',
                catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True)
            self.assertEqual('Succeeded',self.new.complete(operation['id'],receipt))
        self.assertEqual(24,self.new.status()['refreshes'])
        with self.assertRaises(m.Rejected):self.new.claim('refresh',APPROVAL,str(uuid.uuid4()))

    def test_concurrent_claim_has_one_owner_and_same_packet_can_complete(self):
        self.prepare()
        def claim(_):
            try:self.old.claim_successor(self.new,self.scope,self.h,APPROVAL);return True
            except m.Rejected:return False
            except OSError as error:
                if error.errno in (errno.EACCES,errno.EAGAIN):return False
                raise
        with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(claim,range(2)))
        self.assertEqual(1,sum(results))
        self.old.complete_successor(self.new,self.scope,self.h,APPROVAL,GATE)
        self.new.require_predecessor_retired(GATE)

    def test_full_eight_node_chain_with_historical_interpreter_pair_is_validated(self):
        historical_pair=historical.ExpiredInterpreterSuccessorTests();historical_pair.setUp()
        self.addCleanup(historical_pair.doCleanups);historical_pair.activate()
        handoff={k:v for k,v in historical_pair.h.items() if k not in m.INTERPRETER_HANDOFF_KEYS-m.CONTINUATION_HANDOFF_KEYS}
        adapter=SimpleNamespace(new=historical_pair.new,clock=historical_pair.clock,scope=historical_pair.scope,
            h=handoff,setUp=lambda:None,activate=lambda:None,doCleanups=lambda:None)
        other=UnusedDiagnosticRenewalTests()
        with patch.object(prior.previous.previous,'WholeArticleTransitionTests',return_value=adapter):other.setUp()
        self.addCleanup(other.doCleanups);other.prepare();other.activate()
        current=other.new;count=0
        while True:
            with current._open() as db:state=current._read(db)
            count+=1
            if state['scope']['schema']==1:break
            current=m.Register(state['scope']['predecessorRegisterPath'],state['scope']['predecessorScopeSha256'])
        self.assertEqual(8,count)
        original=current.anchor.read_bytes();current.anchor.write_bytes(b'synthetic root tamper')
        with self.assertRaises(m.Rejected):other.new.require_predecessor_retired(GATE)
        current.anchor.write_bytes(original);other.new.require_predecessor_retired(GATE)


if __name__=='__main__':unittest.main()
