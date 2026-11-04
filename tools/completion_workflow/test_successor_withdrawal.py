"""Administrative withdrawal uses fake clocks, inert local journals, no provider."""
import copy
from datetime import datetime,timezone
from concurrent.futures import ThreadPoolExecutor
import errno
import unittest
import uuid
import completion_register as m
import test_unused_diagnostic_renewal as fixture

APPROVAL=m.digest({'synthetic':'separate one-time blocked predecessor restoration'})


class SuccessorWithdrawalTests(unittest.TestCase):
    def setUp(self):
        self.f=fixture.UnusedDiagnosticRenewalTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.f.prepare();self.old=self.f.old;self.new=self.f.new;self.clock=self.f.clock
        self.identity=m.digest(self.f.h)
        self.old.claim_successor(self.new,self.f.scope,self.f.h,self.identity)
        # Match the actual installer: two inactive pre-install checks precede replacement.
        for _ in range(2):self.old.check_successor(self.new,self.f.scope,self.f.h,self.identity)
        with self.old._open() as db:self.before=self.old._read(db)
        with self.new._open() as db:self.child=self.new._read(db)
        self.clock.now=m.utc(self.f.scope['endUtc'])+1_000_000
        self.record=dict(SchemaVersion=1,WithdrawalId=str(uuid.uuid4()),WithdrawalApprovalSha256=APPROVAL,
            RepairTransitionId=self.f.h['TransitionId'],RepairIdentitySha256=self.identity,
            ScopeSha256=self.old.scope_sha,ExpectedHeadSha256=self.before['head'],
            PreviousGateSha256=self.f.h['NewGateSha256'],RestoredGateSha256=self.f.h['PreviousGateSha256'],
            BackupPath=str(self.old.path.parent/'synthetic-backup'),PreviousInstallSha256=fixture.GATE,
            PreviousRequiredSha256=fixture.GATE,NewScopeSha256=self.new.scope_sha,NewHeadSha256=self.child['head'],
            AdministrativeOnly=True)
        self.pin=m.digest(self.record)

    def action(self,phase,record=None,pin=None):
        return self.old.successor_withdrawal(self.new,record or self.record,pin or self.pin,phase,fixture.GATE)

    def test_expired_install_withdrawal_preserves_history_holds_counters_and_inactive_child(self):
        before=self.old.path.read_bytes();self.assertFalse(self.action('inspect')['authority'])
        self.assertEqual(before,self.old.path.read_bytes())
        self.action('claim')
        with self.assertRaisesRegex(m.Rejected,'SuccessorWithdrawalState'):
            self.old.complete_successor(self.new,self.f.scope,self.f.h,self.identity,fixture.GATE)
        restarted=m.Register(self.old.path,self.old.scope_sha,lambda:self.clock.now)
        self.assertTrue(restarted.successor_withdrawal(self.new,self.record,self.pin,'check')['valid'])
        self.assertFalse(restarted.successor_withdrawal(self.new,self.record,self.pin,'complete',fixture.GATE)['authority'])
        with restarted._open() as db:s=restarted._read(db)
        self.assertIsNone(s['retirement']);self.assertIsNone(s['pending']);self.assertFalse(s['retired'])
        self.assertEqual('ObservedFailure',s['stopReason']);self.assertEqual('refresh',s['lastFailedKind'])
        for key in ('refresh','admissions','resume-admission','scope','lastGoodUtc'):
            self.assertEqual(self.before[key],s[key])
        self.assertIn(self.f.h['TransitionId'],s['ids'])
        with self.new._open() as db:n=self.new._read(db)
        self.assertEqual(self.child,n)
        for phase in ('claim','complete'):
            with self.assertRaises(m.Rejected):self.action(phase)
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.f.scope,self.f.h,self.identity,fixture.GATE)
        with self.assertRaises(m.Rejected):self.old.claim('refresh',APPROVAL,str(uuid.uuid4()))

    def test_no_record_or_reused_authority_wrong_identity_or_pending_head_can_withdraw(self):
        for key,value in [('AdministrativeOnly',False),('AdministrativeOnly',1),('ExpectedHeadSha256','a'*64),
                          ('RepairIdentitySha256','b'*64),('NewHeadSha256','c'*64),('WithdrawalId',str(uuid.UUID(int=0))),
                          ('WithdrawalApprovalSha256',self.f.h['SourceApprovalSha256']),('ScopeSha256','d'*64)]:
            with self.subTest(key=key):
                r=dict(self.record);r[key]=value
                with self.assertRaises((m.Rejected,ValueError)):self.action('claim',r,m.digest(r))
        with self.old._open() as db:self.assertEqual(self.before['head'],self.old._read(db)['head'])

    def test_touched_or_activated_child_rejects_before_withdrawal(self):
        with self.new._open() as db:
            s=self.new._read(db);self.new._append(db,self.new._stamp({'type':'stop','utc':self.clock.now,'reason':'WindowExpired'}),s['head'])
            db.commit();self.new._seal(db)
        with self.assertRaises(m.Rejected):self.action('claim')
        with self.new._open() as db:self.record['NewHeadSha256']=self.new._read(db)['head']
        with self.assertRaises(m.Rejected):self.action('claim',self.record,m.digest(self.record))

    def test_concurrent_claim_has_one_owner_and_stale_or_foreign_completion_rejects(self):
        def claim(_):
            try:self.action('claim');return True
            except m.Rejected:return False
            except OSError as e:
                if e.errno in (errno.EACCES,errno.EAGAIN):return False
                raise
        with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(claim,range(2)))
        self.assertEqual(1,sum(results))
        bad=dict(self.record,WithdrawalId=str(uuid.uuid4()))
        with self.assertRaises(m.Rejected):self.action('complete',bad,m.digest(bad))
        self.action('complete')

    def test_expired_original_completion_cannot_activate_and_reports_true_expiry(self):
        with self.assertRaisesRegex(m.Rejected,'WindowExpired'):
            self.old.complete_successor(self.new,self.f.scope,self.f.h,self.identity,fixture.GATE)
        with self.assertRaises(m.Rejected):self.action('claim') # The recorded head is now stale.
        with self.old._open() as db:self.record['ExpectedHeadSha256']=self.old._read(db)['head']
        self.pin=m.digest(self.record)
        with self.assertRaises(m.Rejected):self.action('claim') # Changed stop evidence needs separate reconciliation.
        with self.old._open() as db:s=self.old._read(db)
        self.assertEqual('WindowExpired',s['stopReason']);self.assertFalse(s['retired'])

    def test_withdrawn_and_administrative_approval_cannot_authorize_a_fresh_diagnostic(self):
        self.action('claim');self.action('complete')
        with self.old._open() as db:s=self.old._read(db)
        stamp=lambda t:datetime.fromtimestamp(t/1e6,timezone.utc).isoformat().replace('+00:00','Z')
        for approval in (self.f.h['SourceApprovalSha256'],APPROVAL,m.digest({'synthetic':'genuinely fresh narration approval'})):
            path=self.old.path.parent/('fresh-'+approval+'.db')
            scope=dict(self.f.scope,approvalSha256=approval,privateCaptureConsentSha256=m.digest({'consent':approval}),
                transitionId=str(uuid.uuid4()),registerPathSha256=m.path_digest(path),
                startUtc=stamp(self.clock.now),endUtc=stamp(self.clock.now+600_000_000))
            target=m.Register(path,m.digest(scope),lambda:self.clock.now)
            epoch=dict(self.f.h['ClockEpochEvidence'],ObservedUtcMicros=self.clock.now,ObservedMonoMicros=self.clock.now,
                TrustedUtcLowerMicros=self.clock.now-100_000,TrustedUtcUpperMicros=self.clock.now+100_000,
                PreviousLastGoodUtcMicros=s['lastGoodUtc'])
            h=dict(self.f.h,TransitionId=scope['transitionId'],SourceApprovalSha256=approval,WindowApprovalSha256=approval,
                ExpectedPreviousHeadSha256=s['head'],NewScopeSha256=target.scope_sha,ClockEpochEvidence=epoch)
            if approval in (self.f.h['SourceApprovalSha256'],APPROVAL):
                with self.assertRaisesRegex(m.Rejected,'SuccessorWithdrawalState'):
                    self.old.inspect_successor(target,scope,h,m.digest(h))
                self.assertFalse(path.exists())
                event=dict(type='retire-claim',utc=self.clock.now,mono=self.clock.now,observedMono=self.clock.now,
                    id=h['TransitionId'],identity=m.digest(h),handoff=h)
                condition,drift=self.old._retirement_observation(s,event['utc'],event['mono'],h)
                event.update(clockCondition=condition,clockDriftMicros=drift)
                saved_anchor=self.old.anchor.read_bytes()
                with self.old._open() as db:
                    self.old._append(db,event,s['head'])
                    # Even a rehashed synthetic journal/anchor cannot confer reused authority.
                    self.old.anchor.write_bytes(b''.join(f'{seq}:{pin}\n'.encode() for seq,pin in db.execute('SELECT seq,sha FROM events ORDER BY seq')))
                    try:
                        with self.assertRaisesRegex(m.Rejected,'SuccessorWithdrawalState'):self.old._read(db)
                    finally:
                        db.rollback();self.old.anchor.write_bytes(saved_anchor)
            else:
                self.old.claim_successor(target,scope,h,m.digest(h))
                self.old.complete_successor(target,scope,h,m.digest(h),fixture.GATE)
                with target._open() as db:fresh=target._read(db)
                self.assertEqual(s['refresh'],fresh['refresh']);self.assertEqual(s['admissions'],fresh['admissions'])
                self.assertIsNone(fresh['diagnosticDispatch']);self.assertIsNone(fresh['diagnosticPolicySha256'])

    def test_administrative_clock_rollback_is_rejected_without_journal_write(self):
        before=self.old.path.read_bytes();self.clock.now=self.before['lastUtc']-1
        with self.assertRaisesRegex(m.Rejected,'ClockRollback'):self.action('claim')
        self.assertEqual(before,self.old.path.read_bytes())

    def test_additional_valid_check_after_the_reviewed_head_is_stale(self):
        with self.new._open() as db:
            s=self.new._read(db)
            self.new._append(db,dict(type='check',utc=s['lastUtc']+1,mono=s['lastMono']+1),s['head'])
            db.commit();self.new._seal(db)
        with self.assertRaises(m.Rejected):self.action('claim')
        with self.new._open() as db:self.record['NewHeadSha256']=self.new._read(db)['head']
        self.assertFalse(self.action('inspect',self.record,m.digest(self.record))['authority'])

    def test_foreign_events_or_extra_field_check_never_become_unused_history(self):
        for kind in ('activate','claim','complete','build-claim','source-claim','retire-claim','diagnostic-dispatch','check'):
            with self.subTest(kind=kind):
                case=SuccessorWithdrawalTests();case.setUp()
                try:
                    with case.new._open() as db:
                        s=case.new._read(db)
                        event=dict(type=kind,utc=s['lastUtc']+1,mono=s['lastMono']+1,unexpected='synthetic')
                        pin=case.new._append(db,event,s['head']);db.commit();case.new._seal(db)
                    case.record['NewHeadSha256']=pin
                    with self.assertRaises(m.Rejected):case.action('claim',case.record,m.digest(case.record))
                    with case.old._open() as db:self.assertEqual(case.before['head'],case.old._read(db)['head'])
                finally:case.doCleanups()


if __name__=='__main__':unittest.main()
