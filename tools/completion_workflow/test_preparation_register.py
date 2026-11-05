"""Disposable H9 history; no real profile, credentials, network or paid authority."""
import copy
from datetime import datetime, timezone
import unittest
import uuid
import completion_register as m
from test_reserved_diagnostic_resume import ReservedDiagnosticResumeTests


class PreparationRegisterTests(unittest.TestCase):
    def setUp(self):
        self.f = ReservedDiagnosticResumeTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.f.activate(); self.r = self.f.new; self.clock = self.f.clock
        self.clock.now = m.utc(self.f.scope['endUtc']) + 1000000
        # A reboot resets the real monotonic sample. No old event or epoch is rewritten.
        self.mono = 1000000; self.r.monotonic = lambda: self.mono
        with self.assertRaisesRegex(m.Rejected, 'ClockRollback'): self.r.status()
        self.before = self.state()
        s = self.before
        self.plan = dict(SchemaVersion=1, PreparationId=str(uuid.uuid4()),
            ApprovalSha256=m.digest({'synthetic': 'catalog-only approval'}), ScopeSha256=self.r.scope_sha,
            PreviousGateSha256=s['currentGate'], NewBuildSha256=m.digest({'synthetic': 'new build'}),
            HeldAuthorizationId=str(uuid.uuid4()), HeldLedgerSha256=m.digest({'synthetic': 'unused cent'}),
            HeldQueueSha256=m.digest({'synthetic': 'not submitted'}), FinancialRowsSha256=m.digest({'synthetic': '175 cents'}),
            AcceptedEvidenceSha256=s['scope']['acceptedEvidenceSha256'], ClockEpochEvidence=dict(
                ObservedUtcMicros=self.clock.now, ObservedMonoMicros=self.mono,
                TrustedUtcLowerMicros=self.clock.now-100000, TrustedUtcUpperMicros=self.clock.now+100000,
                WindowsBootUtc=self.stamp(self.clock.now-10000000), BootReadbackSha256=m.digest({'synthetic': 'new boot'})),
            NotAfterUtc=self.stamp(self.clock.now+900000000), MaximumSeconds=900,
            AdministrativeOnly=True, EnableNarration=False)
        self.pin = m.digest(self.plan); self.operation = str(uuid.uuid4())
        self.transition = dict(SchemaVersion=1, TransitionKind='expired-reserved-diagnostic-preparation',
            TransitionId=str(uuid.uuid4()), MaintenanceApprovalSha256=m.digest({'synthetic': 'maintenance approval'}),
            SourceApprovalSha256=s['scope']['approvalSha256'], ScopeSha256=self.r.scope_sha,
            ExpectedHeadSha256=s['head'], PreviousGateSha256=s['currentGate'], NewGateSha256=m.digest({'synthetic': 'new gate'}),
            PreviousBuildSha256=s['currentBuild'], NewBuildSha256=self.plan['NewBuildSha256'],
            DatabaseSha256=m.digest({'synthetic': 'cold database'}), FinancialRowsSha256=self.plan['FinancialRowsSha256'],
            AcceptedEvidenceSha256=self.plan['AcceptedEvidenceSha256'], ProfileSha256=s['scope']['profileSha256'],
            OldInventorySha256=m.digest({'synthetic': 'old inventory'}), NewInventorySha256=m.digest({'synthetic': 'new inventory'}),
            PreviousRequiredSha256=m.digest({'synthetic': 'old required'}), NewRequiredSha256=m.digest({'synthetic': 'new required'}),
            UsedRefreshes=s['refresh'], UsedAdmissions=s['admissions'], UsedResumeAdmissions=s['resume-admission'],
            LastGoodUtcMicros=s['lastGoodUtc'], AdministrativeOnly=True, EnableNarration=False,
            PreparationSha256=self.pin, HeldAuthorizationId=self.plan['HeldAuthorizationId'],
            HeldLedgerSha256=self.plan['HeldLedgerSha256'], HeldQueueSha256=self.plan['HeldQueueSha256'])

    @staticmethod
    def stamp(micros):
        return datetime.fromtimestamp(micros/1000000, timezone.utc).isoformat().replace('+00:00', 'Z')

    def state(self):
        with self.r._open() as db: return self.r._read(db)

    def install(self):
        for phase in ('claim', 'check', 'complete'):
            self.r.maintenance(self.transition, m.digest(self.transition), phase, m.digest({'synthetic': 'verified files'}))

    def operate(self, phase, receipt=None):
        return m._preparation('operate', self.r, self.plan, self.pin, self.operation, phase, receipt)

    def receipt(self, outcome='Succeeded'):
        return dict(id=self.operation, identitySha256=self.pin, outcome=outcome,
            evidenceSha256=m.digest({'synthetic': 'catalog and empty probe'}), catalogVerified=True,
            probeInputEmpty=True, probeHttpStatus=400, probeStatus='INVALID_ARGUMENT', audioAbsent=True)

    def test_reboot_preparation_preserves_paid_stop_and_counts_one_cycle(self):
        self.install(); self.operate('claim'); self.operate('check'); self.operate('complete', self.receipt())
        self.r = m.Register(self.r.path, self.r.scope_sha, lambda:self.clock.now, lambda:self.mono)
        s = self.state()
        for key in ('scope', 'stopReason', 'clockEpoch', 'lastGoodUtc', 'admissions', 'resume-admission',
                'diagnosticPolicySha256', 'diagnosticDispatch'):
            self.assertEqual(self.before[key], s[key], key)
        self.assertEqual(self.before['refresh']+1, s['refresh']); self.assertTrue(s['stopped']); self.assertIsNone(s['pending'])
        self.assertGreater(s['lastMono'], self.mono)
        with self.assertRaisesRegex(m.Rejected, 'RunStopped'): self.r.status()
        for kind in ('refresh', 'resume-admission', 'diagnostic-admission', 'completion-admission'):
            with self.subTest(kind=kind), self.assertRaises(m.Rejected): self.r.claim(kind, self.pin, str(uuid.uuid4()))
        with self.assertRaises(m.Rejected): self.operate('claim')

    def test_interrupted_claim_cannot_be_claimed_again_or_stolen(self):
        self.install(); self.operate('claim')
        self.r = m.Register(self.r.path, self.r.scope_sha, lambda:self.clock.now, lambda:self.mono)
        with self.assertRaises(m.Rejected): self.operate('claim')
        self.operation = str(uuid.uuid4())
        with self.assertRaises(m.Rejected): self.operate('check')
        self.assertEqual('preparation-claim', self.state()['pending']['type'])

    def test_noncanonical_operation_ids_cannot_poison_the_journal(self):
        self.install(); before = self.state()['head']
        for value in (uuid.uuid4().hex, str(uuid.uuid4()).upper()):
            self.operation = value
            with self.subTest(value=value), self.assertRaises(m.Rejected): self.operate('claim')
            self.assertEqual(before, self.state()['head'])
            self.assertIsNone(self.state()['pending'])

    def test_trusted_clock_precision_is_separate_from_monotonic_drift(self):
        p=copy.deepcopy(self.plan);e=p['ClockEpochEvidence']
        e.update(TrustedUtcLowerMicros=self.clock.now-900000,TrustedUtcUpperMicros=self.clock.now+900000)
        m._preparation('validate',p,m.digest(p))
        m._preparation('clock',p,self.clock.now,self.mono)
        with self.assertRaisesRegex(m.Rejected,'PreparationClock'):
            m._preparation('clock',p,self.clock.now+250001,self.mono)
        e.update(TrustedUtcLowerMicros=self.clock.now-2500001,TrustedUtcUpperMicros=self.clock.now+2500000)
        with self.assertRaisesRegex(m.Rejected,'PreparationClock'):m._preparation('validate',p,m.digest(p))

    def test_maintenance_failure_retains_safe_preparation_code_without_prose(self):
        import maintenance_operator
        packet=maintenance_operator.safe_failure(m.Rejected('PreparationClock'))
        self.assertEqual('PreparationClock',packet['code']);self.assertTrue(packet['detailsAvailable'])
        packet=maintenance_operator.safe_failure(ValueError('private-text-and-token-canary'))
        self.assertNotIn('private-text-and-token-canary',m.canonical(packet))

    def test_clock_rollback_deadline_and_duration_fail_closed(self):
        for change in ('reboot', 'wall', 'deadline', 'duration'):
            case = PreparationRegisterTests(); case.setUp()
            try:
                case.install(); case.operate('claim')
                if change == 'reboot': case.mono = 1
                if change == 'wall': case.clock.now += 1000000
                if change in ('deadline', 'duration'):
                    case.mono += 900000000; case.clock.now += 900000000
                with self.subTest(change=change), self.assertRaises(m.Rejected): case.operate('check')
                self.assertIsNotNone(case.state()['pending'])
            finally: case.doCleanups()

    def test_maintenance_rejects_policy_dispatch_pending_and_scope_changes(self):
        for key, value in (('diagnosticPolicySha256', self.pin), ('diagnosticDispatch', {}),
                ('pending', {'type':'claim'}), ('retired', True), ('stopped', False)):
            s = copy.deepcopy(self.before); s[key] = value
            with self.subTest(key=key), self.assertRaises(m.Rejected):
                m._maintenance('ready', s, self.transition, m.digest(self.transition), self.clock.now)
        self.install()
        for field in ('HeldAuthorizationId', 'HeldLedgerSha256', 'HeldQueueSha256', 'FinancialRowsSha256'):
            p = dict(self.plan); p[field] = str(uuid.uuid4()) if field.endswith('Id') else m.digest({'changed':field})
            with self.subTest(field=field), self.assertRaises(m.Rejected):
                m._preparation('ready', self.state(), p, m.digest(p), self.clock.now, self.mono)

    def test_manual_release_and_success_fabrication_are_rejected(self):
        self.install(); s = self.state()
        # Preparation alone is not eligible: a completed checked cycle and a
        # separate retirement instruction are required.
        release = dict(SchemaVersion=1, ReleaseId=str(uuid.uuid4()), ReleaseApprovalSha256=self.pin,
            MaintenanceIdentitySha256=m.digest(self.transition), ScopeSha256=self.r.scope_sha, ExpectedHeadSha256=s['head'],
            ManualInventorySha256=self.pin, RequiredArchivePathSha256=self.pin, AdministrativeOnly=True, PaidAuthority=False,
            CurrentDatabaseSha256=self.pin)
        with self.assertRaisesRegex(m.Rejected, 'MaintenanceState'):
            m._maintenance('release', self.r, release, m.digest(release), 'inspect')
        self.operate('claim'); receipt = self.receipt(); receipt['probeInputEmpty'] = False
        with self.assertRaises(m.Rejected): self.operate('complete', receipt)
        self.assertIsNotNone(self.state()['pending'])

    def retirement(self):
        s=self.state()
        return dict(SchemaVersion=1,ReleaseId=str(uuid.uuid4()),
            ReleaseApprovalSha256=m.digest({'synthetic':'explicitly retire expired session; no paid authority'}),
            MaintenanceIdentitySha256=m.digest(self.transition),ScopeSha256=self.r.scope_sha,
            ExpectedHeadSha256=s['head'],ManualInventorySha256=self.pin,RequiredArchivePathSha256=self.pin,
            AdministrativeOnly=True,PaidAuthority=False,CurrentDatabaseSha256=self.pin)

    def test_explicit_retirement_preserves_stop_financial_identity_and_used_counters(self):
        self.install();self.operate('claim');self.operate('complete',self.receipt())
        before=self.state();r=self.retirement();pin=m.digest(r)
        for phase in ('inspect','claim','check','complete'):
            result=m._maintenance('release',self.r,r,pin,phase,self.pin)
            self.assertFalse(result['authority'])
        after=self.state();self.assertTrue(after['retired']);self.assertTrue(after['stopped'])
        for key in ('scope','refresh','admissions','resume-admission','clockEpoch','lastGoodUtc',
                'diagnosticPolicySha256','diagnosticDispatch','preparation'):
            self.assertEqual(before[key],after[key],key)
        with self.assertRaisesRegex(m.Rejected,'RunStopped'):self.r.status()
        with self.assertRaises(m.Rejected):m._maintenance('release',self.r,r,pin,'claim')

    def test_retirement_rejects_changed_counter_failed_preparation_and_reused_consent(self):
        self.install();self.operate('claim');self.operate('complete',self.receipt())
        s=self.state();r=self.retirement()
        for key,value in (('refresh',s['refresh']+1),('admissions',s['admissions']+1),
                ('diagnosticDispatch',{}),('diagnosticPolicySha256',self.pin),('pending',{'type':'claim'})):
            changed=copy.deepcopy(s);changed[key]=value
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                m._maintenance('release_ready',changed,r,m.digest(r),self.clock.now,s['head'])
        changed=copy.deepcopy(s);changed['preparation'][0]['outcome']='Failed'
        with self.assertRaises(m.Rejected):m._maintenance('release_ready',changed,r,m.digest(r),self.clock.now,s['head'])
        reused=dict(r,ReleaseApprovalSha256=self.plan['ApprovalSha256'])
        with self.assertRaises(m.Rejected):m._maintenance('release',self.r,reused,m.digest(reused),'inspect')


if __name__ == '__main__': unittest.main()
