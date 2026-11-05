"""Isolated journal tests. No user profile, credential, UI or HTTP access."""
import copy
from datetime import datetime, timezone
import unittest
import uuid

import completion_register as m
import test_released_diagnostic_successor as previous


class ReservedDiagnosticResumeTests(unittest.TestCase):
    def setUp(self):
        self.f = previous.ReleasedDiagnosticSuccessorTests()
        self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.f.operation('claim'); self.f.operation('complete')
        self.old = self.f.new; self.clock = self.f.clock
        op = self.old.claim('diagnostic-admission', m.digest({'synthetic':'old policy'}), str(uuid.uuid4()))
        self.old.complete(op['id'],dict(id=op['id'],identitySha256=op['identitySha256'],
            evidenceSha256=m.digest({'synthetic':'installed'}),outcome='Succeeded',policyVerified=True,
            maximumNewAttempts=1,additionalReservationCents=1))
        with self.old._open() as db:self.before=self.old._read(db)
        self.clock.now=m.utc(self.before['scope']['endUtc'])+1_000_000
        now=self.clock.now
        stamp=lambda x:datetime.fromtimestamp(x/1e6,timezone.utc).isoformat().replace('+00:00','Z')
        approval=m.digest({'synthetic':'exact fresh same cent authority'})
        consent=m.digest({'synthetic':'fresh protected response consent'})
        gate=m.digest({'synthetic':'H9 gate'})
        path=self.old.path.parent/'reserved-resume9.db'
        self.scope=dict(self.before['scope'],diagnosticPurpose='reserved-provider-evidence',
            diagnosticWindowMode='fresh-10-minute',startUtc=stamp(now),endUtc=stamp(now+600_000_000),
            approvalSha256=approval,privateCaptureConsentSha256=consent,buildSha256=gate,plansSha256=gate,
            predecessorRegisterPath=str(self.old.path),predecessorScopeSha256=self.old.scope_sha,
            registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        epoch=dict(ObservedUtcMicros=now,ObservedMonoMicros=now,TrustedUtcLowerMicros=now-100_000,
            TrustedUtcUpperMicros=now+100_000,PreviousLastGoodUtcMicros=self.before['lastGoodUtc'],
            WindowsBootUtc=self.before['scope']['startUtc'],BootReadbackSha256=consent)
        self.h={k:v for k,v in self.f.h.items() if k not in m.RELEASED_HANDOFF_EXTRA}
        self.h.update(SchemaVersion=9,TransitionKind='expired-reserved-diagnostic-resume',
            TransitionId=self.scope['transitionId'],SourceApprovalSha256=approval,WindowApprovalSha256=approval,
            PreviousScopeSha256=self.old.scope_sha,PreviousGateSha256=self.before['currentGate'],
            PreviousConfigPath=str(path.parent/'old-gate.json'),ExpectedPreviousHeadSha256=self.before['head'],
            NewConfigPath=str(path.parent/'new-gate.json'),NewScopePath=str(path.parent/'new-scope.json'),
            NewScopeSha256=self.new.scope_sha,NewGateSha256=gate,OriginalManifestSha256=self.before['scope']['plansSha256'],
            InheritedClockEpochEvidence=self.before['clockEpoch'],ClockEpochEvidence=epoch,
            UsedRefreshes=self.before['refresh'],UsedAdmissions=self.before['admissions'],UsedResumeAdmissions=self.before['resume-admission'])
        self.pin=m.digest(self.h)

    def activate(self):
        self.old.inspect_successor(self.new,self.scope,self.h,self.pin)
        self.old.claim_successor(self.new,self.scope,self.h,self.pin)
        self.old.check_successor(self.new,self.scope,self.h,self.pin)
        self.old.complete_successor(self.new,self.scope,self.h,self.pin,self.h['NewGateSha256'])
        self.new.require_predecessor_retired(self.h['NewGateSha256'])

    def test_restart_preserves_counters_one_resume_and_single_outcome(self):
        self.activate()
        with self.new._open() as db:state=self.new._read(db)
        for k in ('refresh','admissions','resume-admission'):self.assertEqual(self.before[k],state[k])
        self.assertIsNone(state['diagnosticPolicySha256']);self.assertIsNone(state['diagnosticDispatch'])
        for k in ('diagnostic-admission','completion-admission','pilot-admission'):
            with self.assertRaises(m.Rejected):self.new.claim(k,self.pin,str(uuid.uuid4()))
        op=self.new.claim('resume-admission',self.pin,str(uuid.uuid4()))
        self.assertEqual('Succeeded',self.new.complete(op['id'],dict(id=op['id'],identitySha256=self.pin,
            evidenceSha256=self.pin,outcome='Succeeded',policyVerified=True,zeroNewFunds=True)))
        restarted=m.Register(self.new.path,self.new.scope_sha,lambda:self.clock.now)
        restarted.require_predecessor_retired(self.h['NewGateSha256'])
        with self.assertRaises(m.Rejected):restarted.claim('resume-admission',self.pin,str(uuid.uuid4()))
        correlation=str(uuid.uuid4());auth=uuid.uuid4().hex;attempt=uuid.uuid4().hex
        restarted.diagnostic_dispatch(correlation,self.pin,self.pin,auth,attempt)
        restarted.diagnostic_dispatch(correlation,self.pin,self.pin,auth,attempt)
        with self.assertRaises(m.Rejected):restarted.diagnostic_dispatch(correlation,self.pin,self.pin,auth,attempt)
        with self.assertRaises(m.Rejected):restarted.claim('refresh',self.pin,str(uuid.uuid4()))
        with self.assertRaises(m.Rejected):self.old.claim_successor(self.new,self.scope,self.h,self.pin)

    def test_authority_counter_window_and_second_renewal_reject(self):
        for key,value in [('UsedAdmissions',0),('WindowApprovalSha256',self.before['scope']['approvalSha256']),
                          ('ExpectedPreviousHeadSha256',self.pin),('SchemaVersion',7)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):
                self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),self.pin)
        bad=copy.deepcopy(self.before);bad['diagnosticDispatch']={'ordinal':1}
        self.assertFalse(m.reserved_diagnostic_predecessor(bad))
        bad=copy.deepcopy(self.before);bad['pending']={'type':'claim','kind':'refresh'}
        self.assertFalse(m.reserved_diagnostic_predecessor(bad))
        self.activate()
        with self.new._open() as db:self.assertFalse(m.reserved_diagnostic_predecessor(self.new._read(db)))
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):self.new.status()

    def test_post_release_h8_cannot_admit_reserved_purpose(self):
        with self.assertRaisesRegex(m.Rejected,'ReleasedDiagnosticWindow'):
            m.diagnostic_window_binding(self.f.before,dict(self.f.scope,diagnosticPurpose='reserved-provider-evidence'),self.f.h)

    def test_interrupted_claim_replay_and_expiry_never_activate(self):
        self.old.claim_successor(self.new,self.scope,self.h,self.pin)
        with self.assertRaises(m.Rejected):self.old.claim_successor(self.new,self.scope,self.h,self.pin)
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,self.pin,self.h['NewGateSha256'])
        with self.new._open() as db:self.assertFalse(self.new._read(db)['activated'])


if __name__=='__main__':unittest.main()
