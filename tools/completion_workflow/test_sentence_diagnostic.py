"""Inert same-window evidence purpose; no real register/profile/provider data."""
import copy
import unittest
import uuid
from unittest.mock import patch
import completion_register as m
import test_sentence_replan as previous
import test_sentence_policy as policy_fixture

H=m.digest({'synthetic':'diagnostic approval'})
G=m.digest({'synthetic':'diagnostic gate'})
C=m.digest({'synthetic':'separate private consent'})


class DiagnosticRegisterTests(unittest.TestCase):
    def setUp(self):
        self.prepare()
    def prepare(self, epoch=False):
        self.f=previous.SentenceRegisterTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        if epoch:
            now=self.f.clock.now
            from datetime import datetime,timezone
            e=dict(ObservedUtcMicros=now,ObservedMonoMicros=now,TrustedUtcLowerMicros=now-100_000,TrustedUtcUpperMicros=now+100_000,
                PreviousLastGoodUtcMicros=self.f.before['lastGoodUtc'],WindowsBootUtc=datetime.fromtimestamp((now+1)/1e6,timezone.utc).isoformat().replace('+00:00','Z'),BootReadbackSha256=C)
            self.f.h.update(TransitionKind='same-window-sentence-replan-after-reboot',ClockEpochEvidence=e)
        self.f.activate()
        self.old=self.f.new;self.clock=self.f.clock
        with self.old._open() as db:self.before=self.old._read(db)
        path=self.old.path.parent/'diagnostic6.db'
        self.scope=dict(self.f.scope,schema=6,approvalSha256=H,plansSha256=G,buildSha256=G,
            maximumNewAttempts=1,diagnosticPurpose='unchanged-provider-evidence',diagnosticWindowMode='inherited',privateCaptureConsentSha256=C,
            predecessorRegisterPath=str(self.old.path),predecessorScopeSha256=self.old.scope_sha,
            registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        self.scope.pop('wholeArticleGrantSha256')
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        self.h=dict(self.f.h,SchemaVersion=5,TransitionKind='sentence-evidence-diagnostic',
            InheritedClockEpochEvidence=self.before['clockEpoch'],TransitionId=self.scope['transitionId'],
            SourceApprovalSha256=H,PreviousGateSha256=self.before['currentGate'],PreviousScopeSha256=self.old.scope_sha,
            ExpectedPreviousHeadSha256=self.before['head'],NewGateSha256=G,NewScopeSha256=self.new.scope_sha,
            OriginalManifestSha256=self.before['scope']['plansSha256'],UsedRefreshes=self.before['refresh'],
            UsedAdmissions=self.before['admissions'],UsedResumeAdmissions=self.before['resume-admission'])
        self.h.pop('ClockEpochEvidence',None)
    def activate(self):
        self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.old.claim_successor(self.new,self.scope,self.h,H)
        self.old.check_successor(self.new,self.scope,self.h,H)
        self.old.complete_successor(self.new,self.scope,self.h,H,G)
        self.new.require_predecessor_retired(G)
    def test_carries_window_counters_and_single_admission(self):
        self.activate();s=self.new.status()
        self.assertEqual(self.scope['endUtc'],s['deadlineUtc'])
        self.assertEqual(self.before['admissions'],s['totalAdmissions'])
        op=self.new.claim('diagnostic-admission',H,str(uuid.uuid4()))
        self.assertEqual('Succeeded',self.new.complete(op['id'],previous.receipt(op,1)))
        with self.assertRaises(m.Rejected):self.new.claim('diagnostic-admission',G,str(uuid.uuid4()))
        self.assertEqual(self.before['admissions']+1,self.new.status()['totalAdmissions'])
    def test_other_financial_purposes_are_rejected(self):
        self.activate()
        for kind in ('pilot-admission','completion-admission','resume-admission'):
            with self.subTest(kind=kind),self.assertRaises(m.Rejected):self.new.claim(kind,H,str(uuid.uuid4()))
    def test_changed_window_epoch_counts_and_head_rejected(self):
        for key,value in [('UsedAdmissions',0),('UsedRefreshes',0),('InheritedClockEpochEvidence',{}),('ExpectedPreviousHeadSha256',G)]:
            with self.subTest(key=key),self.assertRaises((m.Rejected,ValueError)):self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),H)
        scope=dict(self.scope,startUtc='2031-01-01T00:00:00Z',endUtc='2031-01-01T06:00:00Z')
        target=m.Register(self.new.path,m.digest(scope),lambda:self.clock.now)
        with self.assertRaises(m.Rejected):self.old.inspect_successor(target,scope,dict(self.h,NewScopeSha256=target.scope_sha),H)
        self.assertFalse(self.new.path.exists())
    def test_expired_and_backward_clock_never_renew(self):
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.assertFalse(self.new.path.exists())
    def test_partial_handoff_requires_same_readback_and_replay_rejects(self):
        self.old.claim_successor(self.new,self.scope,self.h,H)
        with self.assertRaises(m.Rejected):self.old.claim_successor(self.new,self.scope,self.h,H)
        self.old.complete_successor(self.new,self.scope,self.h,H,G)
        self.new.require_predecessor_retired(G)
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,H,G)
    def test_inherited_epoch_preserves_earlier_cutoff_after_restart(self):
        self.prepare(epoch=True);self.activate()
        with self.new._open() as db:state=self.new._read(db)
        self.assertEqual(self.before['clockEpoch'],state['clockEpoch'])
        self.assertEqual(m.effective_deadline(self.scope,self.before['clockEpoch']),self.new.status()['deadlineUtc'])
        self.clock.now=m.utc(self.scope['endUtc'])-50_000
        with self.assertRaises(m.Rejected):self.new.status()
    def test_dispatch_permit_replays_neither_new_id_nor_third_check(self):
        self.activate();op=self.new.claim('diagnostic-admission',H,str(uuid.uuid4()));self.new.complete(op['id'],previous.receipt(op,1))
        correlation=str(uuid.uuid4());auth=uuid.uuid4().hex;attempt=uuid.uuid4().hex
        self.new.diagnostic_dispatch(correlation,H,G,auth,attempt)
        self.new.status()
        with self.assertRaises(m.Rejected):self.new.diagnostic_dispatch(str(uuid.uuid4()),H,G,auth,attempt)
        self.new.diagnostic_dispatch(correlation,H,G,auth,attempt)
        with self.assertRaises(m.Rejected):self.new.diagnostic_dispatch(correlation,H,G,auth,attempt)
        with self.assertRaises(m.Rejected):self.new.claim('refresh',G,str(uuid.uuid4()))
    def test_concurrent_correlations_have_one_owner_and_storage_gap_fails_closed(self):
        self.activate();op=self.new.claim('diagnostic-admission',H,str(uuid.uuid4()));self.new.complete(op['id'],previous.receipt(op,1))
        from concurrent.futures import ThreadPoolExecutor
        import threading
        barrier=threading.Barrier(2);auth=uuid.uuid4().hex;attempt=uuid.uuid4().hex
        def claim(_):
            barrier.wait()
            try:self.new.diagnostic_dispatch(str(uuid.uuid4()),H,G,auth,attempt);return True
            except (m.Rejected,OSError):return False
        with ThreadPoolExecutor(2) as pool:self.assertEqual(1,sum(pool.map(claim,range(2))))
        with self.new._open() as db:owned=self.new._read(db)['diagnosticDispatch']
        with patch.object(self.new,'_seal',side_effect=OSError('synthetic storage failure')):
            with self.assertRaises(OSError):self.new.diagnostic_dispatch(owned['id'],H,G,auth,attempt)
        reopened=m.Register(self.new.path,self.new.scope_sha,lambda:self.clock.now)
        with self.assertRaisesRegex(m.Rejected,'AnchorDatabaseMismatch'):reopened.status()


def diagnostic_policy_fixture():
    manifest,old,policy,_=policy_fixture.fixture();part=manifest['parts'][0]
    d=dict(proposalSha256=H,successorGrantSha256=G,privateCaptureConsentSha256=C,failedAuthorizationId=str(uuid.uuid4()),
        failedLocalAttemptId=uuid.uuid4().hex,planSha256=part['planSha256'],coverageOrdinal=part['coverageOrdinal'],
        payloadSha256=part['payloadSha256'],payloadBytes=part['payloadBytes'],financialRowsSha256=H,
        projectCommittedMinorUnits=174,allocationCommittedMinorUnits=44)
    manifest['diagnostic']=d
    scope=dict(schema=6,approvalSha256=H,buildSha256=H,profileSha256=H,accountSha256=H,projectSha256=H,
        plansSha256=m.digest(manifest),registerPathSha256=H,startUtc=old['startUtc'],endUtc=old['endUtc'],maxRefreshes=24,
        maxResumeAdmissions=24,maxAdmissions=24,predecessorRegisterPath=str(__import__('pathlib').Path.cwd()/'inert5.db'),
        predecessorScopeSha256=G,transitionId=str(uuid.uuid4()),acceptedEvidenceSha256=old['acceptedEvidenceSha256'],
        maximumNewAttempts=1,diagnosticPurpose='unchanged-provider-evidence',diagnosticWindowMode='inherited',privateCaptureConsentSha256=C)
    policy.update(SchemaVersion=4,SourceApprovalSha256=H,MaximumNewAttempts=1,MaximumAdditionalReservationMinorUnits=1,
        AppSha256=H,InfrastructureSha256=G,ProfileRootSha256=H,
        DiagnosticEvidence=dict(Version=1,Purpose='unchanged-provider-evidence',ProposalSha256=H,SuccessorGrantSha256=G,
            PrivateCaptureConsentSha256=C,RequiredCaptureMode=1),
        SuccessorGrant=dict(ParentAuthorizationId=d['failedAuthorizationId'],ParentLocalAttemptId=d['failedLocalAttemptId'],
            PlanSha256=d['planSha256'],CoverageOrdinal=d['coverageOrdinal'],MaximumNewAttempts=1,MaximumAdditionalReservationMinorUnits=1))
    policy['Budget'].update(ProjectCommittedMinorUnits=174,AllocationCommittedMinorUnits=44,ProjectCeilingMinorUnits=175,AllocationCeilingMinorUnits=45)
    policy['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=1,MaximumMinorUnits=1)]
    scope['buildSha256']=m.hashlib.sha256((H+'|'+G).encode()).hexdigest()
    return manifest,scope,policy


class DiagnosticPolicyTests(unittest.TestCase):
    def test_exact_one_cent_and_separate_consent(self):
        manifest,scope,policy=diagnostic_policy_fixture();m.verify_plan_policy(manifest,scope,policy,'diagnostic-admission')
        for key,value in [('MaximumNewAttempts',2),('MaximumAdditionalReservationMinorUnits',0),('SchemaVersion',2),('SourceApprovalSha256',G)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):m.verify_plan_policy(manifest,scope,dict(policy,**{key:value}),'diagnostic-admission')
        for kind in ('resume-admission','pilot-admission','completion-admission'):
            with self.assertRaises(m.Rejected):m.verify_plan_policy(manifest,scope,policy,kind)
    def test_whole_grant_expiry_and_payload_changes_rejected(self):
        manifest,scope,policy=diagnostic_policy_fixture()
        with self.assertRaises(m.Rejected):m.verify_plan_policy(manifest,scope,dict(policy,WholeArticleGeneration={}), 'diagnostic-admission')
        with self.assertRaises(m.Rejected):m.verify_plan_policy(manifest,scope,dict(policy,ExpiresAtUtc='2020-01-01T00:00:00Z'),'diagnostic-admission')
        changed=copy.deepcopy(manifest);changed['diagnostic']['payloadSha256']=G
        with self.assertRaises(m.Rejected):m.verify_plan_policy(changed,dict(scope,plansSha256=m.digest(changed)),policy,'diagnostic-admission')


class FreshDiagnosticWindowTests(unittest.TestCase):
    setUp=DiagnosticRegisterTests.setUp
    prepare=DiagnosticRegisterTests.prepare
    activate=DiagnosticRegisterTests.activate
    def fresh(self, persisted=False, reset_mono=False):
        from datetime import datetime,timezone
        self.clock.now=m.utc(self.before['scope']['endUtc'])+1_000_000
        if persisted:
            with self.assertRaises(m.Rejected):self.old.status()
        with self.old._open() as db:self.before=self.old._read(db)
        now=self.clock.now;mono=1_000_000 if reset_mono else now
        self.old.monotonic=lambda:mono+(self.clock.now-now)
        e=dict(ObservedUtcMicros=now,ObservedMonoMicros=mono,TrustedUtcLowerMicros=now-100_000,TrustedUtcUpperMicros=now+100_000,
            PreviousLastGoodUtcMicros=self.before['lastGoodUtc'],WindowsBootUtc=self.before['scope']['startUtc'],BootReadbackSha256=C)
        stamp=lambda value:datetime.fromtimestamp(value/1e6,timezone.utc).isoformat().replace('+00:00','Z')
        self.scope.update(diagnosticWindowMode='fresh-10-minute',startUtc=stamp(now),endUtc=stamp(now+600_000_000))
        self.new=m.Register(self.new.path,m.digest(self.scope),lambda:self.clock.now,lambda:mono+(self.clock.now-now))
        self.h.update(SchemaVersion=6,TransitionKind='expired-sentence-evidence-diagnostic',ClockEpochEvidence=e,WindowApprovalSha256=H,
            NewScopeSha256=self.new.scope_sha,ExpectedPreviousHeadSha256=self.before['head'],UsedRefreshes=self.before['refresh'],
            UsedAdmissions=self.before['admissions'],UsedResumeAdmissions=self.before['resume-admission'])
    def test_expired_window_is_ten_minutes_no_counter_reset_and_no_old_permission(self):
        self.fresh();self.activate()
        self.assertEqual(self.before['admissions'],self.new.status()['totalAdmissions'])
        self.assertEqual(600_000_000,m.utc(self.scope['endUtc'])-m.utc(self.scope['startUtc']))
        with self.assertRaises(m.Rejected):self.old.claim('refresh',H,str(uuid.uuid4()))
        self.clock.now=m.utc(self.scope['endUtc'])-50_000
        with self.assertRaises(m.Rejected):self.new.status()
    def test_persisted_expiry_and_actual_new_monotonic_epoch_remain_auditable(self):
        self.fresh(persisted=True,reset_mono=True);self.activate()
        with self.new._open() as db:state=self.new._read(db)
        self.assertEqual(self.h['ClockEpochEvidence'],state['clockEpoch'])
        with self.old._open() as db:
            old=self.old._read(db);self.assertTrue(old['retired']);self.assertGreater(old['lastMono'],self.h['ClockEpochEvidence']['ObservedMonoMicros'])
    def test_window_approval_duration_mode_and_active_predecessor_fail_closed(self):
        self.fresh()
        for key,value in [('WindowApprovalSha256',C),('SourceApprovalSha256',C),('InheritedClockEpochEvidence',{})]:
            with self.subTest(key=key),self.assertRaises((m.Rejected,ValueError)):self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),H)
        for change in ({'diagnosticWindowMode':'inherited'},{'endUtc':self.before['scope']['endUtc']}):
            scope=dict(self.scope,**change);target=m.Register(self.new.path,m.digest(scope),lambda:self.clock.now,self.old.monotonic)
            with self.assertRaises(m.Rejected):self.old.inspect_successor(target,scope,dict(self.h,NewScopeSha256=target.scope_sha),H)
    def test_pending_expired_new_window_never_completes(self):
        self.fresh();self.old.claim_successor(self.new,self.scope,self.h,H)
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,H,G)
        with self.new._open() as db:self.assertFalse(self.new._read(db)['activated'])


if __name__=='__main__':unittest.main()
