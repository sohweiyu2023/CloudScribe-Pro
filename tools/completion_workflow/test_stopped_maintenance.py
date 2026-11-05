"""Synthetic sealed history only; no installation, credentials, profile or provider."""
import copy
import hashlib
from pathlib import Path
import tempfile
import types
import unittest
import uuid
import completion_register as m
import test_successor_withdrawal as fixture


class StoppedMaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.f=fixture.SuccessorWithdrawalTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.f.action('claim');self.f.action('complete',pin=self.f.pin)
        self.r=self.f.old;self.clock=self.f.clock
        with self.r._open() as db:
            self.before=self.r._read(db);self.rows=db.execute('SELECT body,sha FROM events ORDER BY seq').fetchall()
        s=self.before
        self.record=dict(SchemaVersion=1,TransitionKind='stopped-unused-diagnostic-maintenance',TransitionId=str(uuid.uuid4()),
            MaintenanceApprovalSha256=m.digest({'synthetic':'fresh maintenance only'}),SourceApprovalSha256=s['scope']['approvalSha256'],
            ScopeSha256=self.r.scope_sha,ExpectedHeadSha256=s['head'],PreviousGateSha256=s['currentGate'],NewGateSha256=m.digest({'new':'gate'}),
            PreviousBuildSha256=s['currentBuild'],NewBuildSha256=m.digest({'new':'binary'}),DatabaseSha256=m.digest({'fake':'database'}),
            FinancialRowsSha256=m.digest({'fake':'174 cents retained'}),AcceptedEvidenceSha256=s['scope']['acceptedEvidenceSha256'],
            ProfileSha256=s['scope']['profileSha256'],OldInventorySha256=m.digest({'fake':'old files'}),NewInventorySha256=m.digest({'fake':'new files'}),
            PreviousRequiredSha256=m.digest({'fake':'old required'}),NewRequiredSha256=m.digest({'fake':'new required'}),
            UsedRefreshes=s['refresh'],UsedAdmissions=s['admissions'],UsedResumeAdmissions=s['resume-admission'],LastGoodUtcMicros=s['lastGoodUtc'],
            AdministrativeOnly=True,EnableNarration=False)
        self.pin=m.digest(self.record)

    def action(self,phase,record=None):
        r=record or self.record
        return self.r.maintenance(r,m.digest(r),phase,m.digest({'synthetic':'actual readback'}))

    def state(self):
        with self.r._open() as db:return self.r._read(db)

    def unchanged(self,s):
        for key in ('scope','refresh','admissions','resume-admission','completion-admission','pilot-admission','diagnostic-admission',
                    'stopReason','lastFailedKind','lastGoodUtc','clockEpoch','diagnosticDispatch','activation','successorWithdrawal'):
            self.assertEqual(self.before[key],s[key],key)
        self.assertTrue(s['stopped']);self.assertFalse(s['retired'])
        with self.r._open() as db:
            self.assertEqual(self.rows,db.execute('SELECT body,sha FROM events ORDER BY seq LIMIT ?',(len(self.rows),)).fetchall())

    def test_update_readback_restart_preserves_all_old_authority_and_history(self):
        raw=self.r.path.read_bytes();self.assertEqual({'valid':True,'authority':False},self.action('inspect'))
        self.assertEqual(raw,self.r.path.read_bytes())
        self.action('claim');self.assertEqual('maintenance-claim',self.state()['pending']['type'])
        self.r=m.Register(self.r.path,self.r.scope_sha,lambda:self.clock.now)
        self.action('check');self.action('complete');s=self.state();self.unchanged(s)
        self.assertIsNone(s['pending']);self.assertEqual(self.record['NewBuildSha256'],s['currentBuild'])
        self.assertEqual(self.record['NewGateSha256'],s['currentGate'])
        for kind in ('refresh','resume-admission','pilot-admission','completion-admission','diagnostic-admission'):
            with self.subTest(kind=kind),self.assertRaisesRegex(m.Rejected,'RunStopped'):
                self.r.claim(kind,m.digest({'synthetic':'no permission'}),str(uuid.uuid4()))
        with self.assertRaisesRegex(m.Rejected,'RunStopped'):self.r.status()
        for phase in ('claim','complete'):
            with self.assertRaises(m.Rejected):self.action(phase)

    def test_rollback_pending_or_completed_never_resets_paid_state(self):
        for completed in (False,True):
            case=StoppedMaintenanceTests();case.setUp()
            try:
                case.action('claim')
                if completed:case.action('complete')
                case.action('rollback-claim');case.action('rollback-check');case.action('rollback-complete')
                s=case.state();case.unchanged(s);self.assertIsNone(s['pending'])
                self.assertEqual(case.before['currentBuild'],s['currentBuild']);self.assertEqual(case.before['currentGate'],s['currentGate'])
                with self.assertRaises(m.Rejected):case.action('rollback-claim')
                with self.assertRaises(m.Rejected):case.action('rollback-complete')
            finally:case.doCleanups()

    def test_active_unexpired_uncertain_identity_or_reused_approval_rejects(self):
        for key,value in [('stopped',False),('retired',True),('activated',False),('stopReason','ClockRollback'),
                          ('diagnosticDispatch',{'ordinal':1}),('pending',{'type':'claim'}),('refresh',99)]:
            state=copy.deepcopy(self.before);state[key]=value
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                m._maintenance('ready',state,self.record,self.pin,self.clock.now)
        with self.assertRaises(m.Rejected):m._maintenance('ready',self.before,self.record,self.pin,m.utc(self.before['scope']['endUtc'])-1)
        forbidden=(self.before['scope']['approvalSha256'],self.before['scope']['privateCaptureConsentSha256'],
                   self.before['successorWithdrawal']['withdrawal']['WithdrawalApprovalSha256'])
        for approval in forbidden:
            r=dict(self.record,MaintenanceApprovalSha256=approval)
            with self.assertRaises(m.Rejected):self.action('inspect',r)
        for field,value in [('AdministrativeOnly',1),('EnableNarration',True),('ExpectedHeadSha256','a'*64),('ProfileSha256','b'*64),('TransitionId',uuid.uuid4().hex),('TransitionId',str(uuid.uuid4()).upper())]:
            with self.subTest(field=field),self.assertRaises(m.Rejected):self.action('claim',dict(self.record,**{field:value}))
        self.unchanged(self.state())

    def test_maintenance_approval_cannot_be_reused_for_paid_successor(self):
        self.action('claim');self.action('complete');state=self.state()
        h=dict(TransitionId=str(uuid.uuid4()),SourceApprovalSha256=self.record['MaintenanceApprovalSha256'])
        with self.assertRaisesRegex(m.Rejected,'MaintenanceAuthorityNotNarration'):m.reject_withdrawn_handoff(state,h)
        self.assertEqual(self.before['currentGate'],m._maintenance('activation',state,self.record['NewGateSha256']))

    def test_pinned_extension_is_bounded_and_tamper_rejected(self):
        core=Path(m.__file__);self.assertLessEqual(core.stat().st_size,131072)
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/core.name;path.write_bytes(core.read_bytes())
            path.with_name('maintenance_register.py').write_bytes(b'raise RuntimeError("untrusted source must never execute")')
            mod=types.ModuleType('synthetic_verified_core');exec(compile(path.read_bytes(),str(path),'exec'),mod.__dict__)
            with self.assertRaisesRegex(mod.Rejected,'ModuleHash'):mod._maintenance('shape','maintenance-claim')

    def test_explicit_release_retires_old_scope_without_authorizing_paid_work(self):
        self.action('claim');self.action('complete');state=self.state()
        record=dict(SchemaVersion=1,ReleaseId=str(uuid.uuid4()),ReleaseApprovalSha256=m.digest({'synthetic':'separate return to manual mode approval'}),
            MaintenanceIdentitySha256=self.pin,ScopeSha256=self.r.scope_sha,ExpectedHeadSha256=state['head'],
            ManualInventorySha256=m.digest({'synthetic':'unchanged latest build except administrative marker'}),
            RequiredArchivePathSha256=m.digest({'synthetic':'preserved requirement archive'}),AdministrativeOnly=True,PaidAuthority=False)
        pin=m.digest(record)
        earlier=copy.deepcopy(state);earlier['maintenance'].insert(0,dict(transition=dict(self.record,MaintenanceApprovalSha256=m.digest({'synthetic':'older approval'}))))
        reused=dict(record,ReleaseApprovalSha256=earlier['maintenance'][0]['transition']['MaintenanceApprovalSha256'])
        with self.assertRaises(m.Rejected):m._maintenance('release_ready',earlier,reused,m.digest(reused),self.clock.now,state['head'])
        for bad in (dict(record,PaidAuthority=True),dict(record,ReleaseId=uuid.uuid4().hex),dict(record,ReleaseId=str(uuid.uuid4()).upper()),dict(record,ReleaseApprovalSha256=self.record['MaintenanceApprovalSha256']),
                    dict(record,ExpectedHeadSha256='a'*64)):
            with self.assertRaises(m.Rejected):m._maintenance('release',self.r,bad,m.digest(bad),'claim')
        self.assertTrue(m._maintenance('release',self.r,record,pin,'inspect')['valid'])
        self.r=m.Register(self.r.path,self.r.scope_sha,lambda:self.clock.now,lambda:7)
        m._maintenance('release',self.r,record,pin,'claim')
        m._maintenance('release',self.r,record,pin,'check')
        m._maintenance('release',self.r,record,pin,'complete',m.digest({'synthetic':'actual manual installation readback'}))
        state=self.state();self.assertTrue(state['retired']);self.assertTrue(state['stopped']);self.assertTrue(state['maintenanceRelease']['completed'])
        with self.r._open() as db:
            event=m.json.loads(db.execute('SELECT body FROM events ORDER BY seq DESC LIMIT 1').fetchone()[0])
        self.assertEqual(7,event['observedMono']);self.assertGreaterEqual(event['mono'],self.before['lastMono'])
        for key in ('scope','refresh','admissions','resume-admission','lastGoodUtc','clockEpoch','diagnosticDispatch'):
            self.assertEqual(self.before[key],state[key],key)
        with self.assertRaisesRegex(m.Rejected,'RunStopped'):self.r.status()
        with self.assertRaises(m.Rejected):m._maintenance('release',self.r,record,pin,'claim')


if __name__=='__main__':unittest.main()
