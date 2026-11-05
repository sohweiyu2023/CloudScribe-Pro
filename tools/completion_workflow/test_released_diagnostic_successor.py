"""Synthetic post-manual handoff journals only; no profile, provider or real authority."""
import copy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import errno
import unittest
from unittest.mock import patch
import uuid

import completion_register as m
import test_stopped_maintenance as fixture


class ReleasedDiagnosticSuccessorTests(unittest.TestCase):
    def setUp(self):
        self.f = fixture.StoppedMaintenanceTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.f.action('claim')
        self.f.action('complete')
        self.old = self.f.r
        self.clock = self.f.clock
        state = self.read(self.old)
        self.manual_inventory = {'cloudscribe-install.json': m.digest({'synthetic': 'manual marker without gate pins'})}
        self.release = dict(SchemaVersion=1, ReleaseId=str(uuid.uuid4()),
            ReleaseApprovalSha256=m.digest({'synthetic': 'separate explicit manual release'}),
            MaintenanceIdentitySha256=self.f.pin, ScopeSha256=self.old.scope_sha,
            ExpectedHeadSha256=state['head'], ManualInventorySha256=m.digest(self.manual_inventory),
            RequiredArchivePathSha256=m.path_digest(self.old.path.parent / 'synthetic-required.retired'),
            AdministrativeOnly=True, PaidAuthority=False)
        self.release_pin = m.digest(self.release)
        for phase in ('claim', 'complete'):
            m._maintenance('release', self.old, self.release, self.release_pin, phase,
                m.digest({'synthetic': 'manual marker and archived requirement readback'}))
        self.before = self.read(self.old)
        self.assertTrue(self.before['retired'])
        self.assertTrue(self.before['maintenanceRelease']['completed'])
        with self.old._open() as db:
            self.prefix = db.execute('SELECT body,sha FROM events ORDER BY seq').fetchall()
        self.clock.now = max(self.clock.now, self.before['lastUtc']) + 1_000_000
        now = self.clock.now
        stamp = lambda value: datetime.fromtimestamp(value / 1e6, timezone.utc).isoformat().replace('+00:00', 'Z')
        self.epoch = dict(ObservedUtcMicros=now, ObservedMonoMicros=now,
            TrustedUtcLowerMicros=now - 100_000, TrustedUtcUpperMicros=now + 100_000,
            PreviousLastGoodUtcMicros=self.before['lastGoodUtc'], WindowsBootUtc=self.before['scope']['startUtc'],
            BootReadbackSha256=m.digest({'synthetic': 'independent boot metadata'}))
        path = self.old.path.parent / 'fresh-released-diagnostic.db'
        approval = m.digest({'synthetic': 'new exact one-outcome diagnostic approval'})
        consent = m.digest({'synthetic': 'new separate protected capture consent'})
        self.gate = m.digest({'synthetic': 'fresh immutable current gate'})
        self.scope = dict(self.before['scope'], approvalSha256=approval,
            privateCaptureConsentSha256=consent, buildSha256=self.before['currentBuild'], plansSha256=self.gate,
            diagnosticWindowMode='fresh-10-minute', startUtc=stamp(now), endUtc=stamp(now + 600_000_000),
            predecessorRegisterPath=str(self.old.path), predecessorScopeSha256=self.old.scope_sha,
            registerPathSha256=m.path_digest(path), transitionId=str(uuid.uuid4()))
        self.new = m.Register(path, m.digest(self.scope), lambda: self.clock.now)
        self.h = dict(copy.deepcopy(self.f.f.f.h), SchemaVersion=8,
            TransitionKind='post-manual-release-diagnostic', TransitionId=self.scope['transitionId'],
            SourceApprovalSha256=approval, WindowApprovalSha256=approval,
            PreviousScopeSha256=self.old.scope_sha, PreviousGateSha256=self.before['currentGate'],
            PreviousConfigPath=str(path.parent / 'immutable-maintained-predecessor.json'),
            ExpectedPreviousHeadSha256=self.before['head'], NewConfigPath=str(path.parent / 'fresh-gate.json'),
            NewScopePath=str(path.parent / 'fresh-scope.json'), NewGateSha256=self.gate,
            NewScopeSha256=self.new.scope_sha, OriginalManifestSha256=self.before['scope']['plansSha256'],
            InheritedClockEpochEvidence=self.before['clockEpoch'], ClockEpochEvidence=self.epoch,
            UsedRefreshes=self.before['refresh'], UsedAdmissions=self.before['admissions'],
            UsedResumeAdmissions=self.before['resume-admission'], ManualReleaseSha256=self.release_pin,
            ManualMarkerSha256=self.manual_inventory['cloudscribe-install.json'],
            RequiredArchiveSha256=self.f.record['NewRequiredSha256'],
            ManualInventorySha256=self.release['ManualInventorySha256'],
            FreshPrivateCaptureConsentSha256=consent)

    @staticmethod
    def read(register):
        with register._open() as db:
            return register._read(db)

    def operation(self, phase, handoff=None):
        h = self.h if handoff is None else handoff
        method = getattr(self.old, phase + '_successor')
        args = (self.new, self.scope, h, m.digest(h))
        return method(*args, self.gate) if phase == 'complete' else method(*args)

    def assert_old_preserved(self):
        state = self.read(self.old)
        self.assertTrue(state['retired'])
        self.assertTrue(state['stopped'])
        for key in ('scope', 'refresh', 'admissions', 'resume-admission', 'lastGoodUtc',
                    'stopReason', 'lastFailedKind', 'diagnosticDispatch', 'maintenance',
                    'maintenanceRelease', 'successorWithdrawal', 'currentGate', 'currentBuild'):
            self.assertEqual(self.before[key], state[key], key)
        with self.old._open() as db:
            self.assertEqual(self.prefix, db.execute('SELECT body,sha FROM events ORDER BY seq LIMIT ?',
                (len(self.prefix),)).fetchall())
        return state

    def activate(self):
        for phase in ('inspect', 'claim', 'check', 'complete'):
            self.operation(phase)
            self.assert_old_preserved()

    def test_fresh_handoff_preserves_retirement_history_and_cumulative_limits(self):
        self.assertFalse(self.new.path.exists())
        self.activate()
        self.new.require_predecessor_retired(self.gate)
        state = self.read(self.new)
        for key in ('refresh', 'admissions', 'resume-admission'):
            self.assertEqual(self.before[key], state[key])
        self.assertEqual(24, state['scope']['maxAdmissions'])
        self.assertEqual(24, state['scope']['maxRefreshes'])
        self.assertTrue(state['activated'])
        self.assertIsNone(state['diagnosticDispatch'])
        self.assertIsNone(state['diagnosticPolicySha256'])
        self.assertEqual(0, state['diagnostic-admission'])
        with self.assertRaises(m.Rejected):
            self.old.claim('refresh', self.gate, str(uuid.uuid4()))
        with self.assertRaises(m.Rejected):
            self.operation('claim')

    def test_wrong_release_archive_inventory_counter_or_head_rejects_without_effect(self):
        for key in ('ManualReleaseSha256', 'RequiredArchiveSha256',
                    'ExpectedPreviousHeadSha256', 'UsedRefreshes', 'UsedAdmissions', 'UsedResumeAdmissions'):
            changed = dict(self.h, **{key: m.digest({'wrong': key}) if key.endswith('Sha256') else self.h[key] + 1})
            with self.subTest(key=key), self.assertRaises(m.Rejected):
                self.operation('claim', changed)
            self.assertEqual(self.before['head'], self.assert_old_preserved()['head'])
            self.assertFalse(self.new.path.exists())

    def test_reused_narration_maintenance_release_and_withdrawal_approvals_reject(self):
        w = self.before['successorWithdrawal']
        forbidden = [self.before['scope']['approvalSha256'], self.before['scope']['privateCaptureConsentSha256'],
            self.release['ReleaseApprovalSha256'], self.f.record['MaintenanceApprovalSha256'],
            w['sourceApproval'], w['withdrawal']['WithdrawalApprovalSha256'], self.scope['privateCaptureConsentSha256']]
        for approval in forbidden:
            scope = dict(self.scope, approvalSha256=approval)
            child = m.Register(self.new.path, m.digest(scope), lambda: self.clock.now)
            handoff = dict(self.h, SourceApprovalSha256=approval, WindowApprovalSha256=approval,
                NewScopeSha256=child.scope_sha)
            with self.subTest(approval=approval), self.assertRaises(m.Rejected):
                self.old.claim_successor(child, scope, handoff, m.digest(handoff))
        self.assertEqual(self.before['head'], self.assert_old_preserved()['head'])

    def test_missing_reused_or_scope_mismatched_capture_consent_rejects(self):
        w = self.before['successorWithdrawal']
        forbidden = [self.before['scope']['approvalSha256'], self.before['scope']['privateCaptureConsentSha256'],
            self.release['ReleaseApprovalSha256'], self.f.record['MaintenanceApprovalSha256'],
            w['sourceApproval'], w['withdrawal']['WithdrawalApprovalSha256'], self.scope['approvalSha256']]
        for consent in forbidden:
            scope = dict(self.scope, privateCaptureConsentSha256=consent)
            child = m.Register(self.new.path, m.digest(scope), lambda: self.clock.now)
            handoff = dict(self.h, FreshPrivateCaptureConsentSha256=consent, NewScopeSha256=child.scope_sha)
            with self.subTest(consent=consent), self.assertRaises(m.Rejected):
                self.old.claim_successor(child, scope, handoff, m.digest(handoff))
        missing = dict(self.h)
        missing.pop('FreshPrivateCaptureConsentSha256')
        for handoff in (missing, dict(self.h, FreshPrivateCaptureConsentSha256=m.digest({'synthetic': 'unbound consent'}))):
            with self.assertRaises(m.Rejected):
                self.operation('claim', handoff)
        self.assertEqual(self.before['head'], self.assert_old_preserved()['head'])

    def test_pending_or_incomplete_manual_release_cannot_start(self):
        for change in ({'pending': {'type': 'claim'}}, {'retired': False}, {'diagnosticDispatch': {'ordinal': 1}},
                       {'maintenanceRelease': dict(self.before['maintenanceRelease'], completed=False)}):
            state = copy.deepcopy(self.before)
            state.update(change)
            with self.subTest(change=tuple(change)), self.assertRaises(m.Rejected):
                self.old._handoff_ready(state, self.new, self.scope, self.h, m.digest(self.h))
        self.assertEqual(self.before['head'], self.assert_old_preserved()['head'])

    def test_pending_claim_has_one_owner_and_same_packet_only(self):
        def claim(_):
            try:
                self.operation('claim')
                return True
            except m.Rejected:
                return False
            except OSError as error:
                if error.errno not in (errno.EACCES, errno.EAGAIN):
                    raise
                return False
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(claim, range(2))))
        self.assert_old_preserved()
        with self.assertRaises(m.Rejected):
            self.operation('check', dict(self.h, ManualMarkerSha256=m.digest({'synthetic': 'foreign marker'})))
        self.operation('check')
        self.operation('complete')
        self.new.require_predecessor_retired(self.gate)

    def test_claim_interruption_restarts_without_reset_or_new_child(self):
        self.operation('claim')
        child_before = self.read(self.new)
        self.assertFalse(child_before['activated'])
        self.old = m.Register(self.old.path, self.old.scope_sha, lambda: self.clock.now)
        self.new = m.Register(self.new.path, self.new.scope_sha, lambda: self.clock.now)
        with self.assertRaises(m.Rejected):
            self.operation('claim')
        self.operation('check')
        self.operation('complete')
        self.assert_old_preserved()
        self.assertTrue(self.read(self.new)['activated'])

    def test_lost_activation_after_old_completion_can_finish_same_handoff(self):
        self.operation('claim')
        append = self.new._append
        def interrupt(db, event, previous):
            if event['type'] == 'activate':
                raise RuntimeError('synthetic interrupted activation')
            return append(db, event, previous)
        with patch.object(self.new, '_append', side_effect=interrupt):
            with self.assertRaisesRegex(RuntimeError, 'synthetic interrupted activation'):
                self.operation('complete')
        old_head = self.assert_old_preserved()['head']
        self.assertFalse(self.read(self.new)['activated'])
        self.operation('complete')
        self.assertEqual(old_head, self.assert_old_preserved()['head'])
        self.new.require_predecessor_retired(self.gate)

    def test_expiry_never_activates_or_extends_a_pending_diagnostic(self):
        self.operation('claim')
        self.clock.now = m.utc(self.scope['endUtc'])
        with self.assertRaises(m.Rejected):
            self.operation('complete')
        self.assertFalse(self.read(self.new)['activated'])
        self.assertEqual(self.scope['endUtc'], self.read(self.new)['scope']['endUtc'])
        self.assert_old_preserved()

    def test_forged_rehashed_handoff_counter_is_rejected_by_replay(self):
        # Append an authenticated but semantically invalid claim to this disposable
        # released fixture; never update/delete historical rows or disable triggers.
        handoff = dict(self.h, UsedRefreshes=self.h['UsedRefreshes'] + 1)
        sample = self.old.monotonic()
        event = dict(type='released-diagnostic-claim', utc=self.clock.now,
            mono=max(self.before['lastMono'], sample), observedMono=sample,
            id=handoff['TransitionId'], identity=m.digest(handoff), handoff=handoff,
            clockCondition='released-terminal', clockDriftMicros=0)
        with self.old._open() as db:
            self.old._append(db, event, self.before['head'])
            db.commit()
            self.old._seal(db)
        with self.assertRaises(m.Rejected):
            self.read(self.old)


if __name__ == '__main__':
    unittest.main()
