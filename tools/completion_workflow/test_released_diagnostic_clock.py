"""Synthetic H8 epoch and completed-result reconciliation; no live state."""
import unittest
from unittest.mock import patch
import completion_register as m
import test_released_diagnostic_successor as fixture


class ReleasedDiagnosticClockTests(unittest.TestCase):
    def setUp(self):
        self.f = fixture.ReleasedDiagnosticSuccessorTests(); self.f.setUp()
        self.addCleanup(self.f.doCleanups)

    def assert_claim_replay_rejected(self, f, handoff):
        # An append-only forged claim in a disposable journal exercises the
        # authenticated replay path without changing historical rows/triggers.
        sample = f.old.monotonic()
        event = dict(type='released-diagnostic-claim', utc=f.clock.now,
            mono=max(f.before['lastMono'], sample), observedMono=sample,
            id=handoff['TransitionId'], identity=m.digest(handoff), handoff=handoff,
            clockCondition='released-terminal', clockDriftMicros=0)
        with f.old._open() as db:
            f.old._append(db, event, f.before['head'])
            db.commit()
            f.old._seal(db)
        with self.assertRaises(m.Rejected):
            f.read(f.old)

    def test_float_handoff_version_rejected_before_claim_and_by_replay(self):
        f = self.f
        handoff = dict(f.h, SchemaVersion=8.0)
        with self.assertRaises(m.Rejected):
            f.operation('claim', handoff)
        self.assertEqual(f.before, f.read(f.old))
        self.assertFalse(f.new.path.exists())
        self.assert_claim_replay_rejected(f, handoff)

    def test_changed_manifest_evidence_pins_rejected_before_claim(self):
        f = self.f
        for key in ('OriginalManifestSha256', 'AcceptedEvidenceSha256'):
            handoff = dict(f.h, **{key: m.digest({'synthetic': 'foreign ' + key})})
            with self.subTest(key=key), self.assertRaises(m.Rejected):
                f.operation('claim', handoff)
            self.assertEqual(f.before, f.read(f.old))
            self.assertFalse(f.new.path.exists())

    def test_changed_manifest_evidence_pins_rejected_by_authenticated_replay(self):
        for key in ('OriginalManifestSha256', 'AcceptedEvidenceSha256'):
            with self.subTest(key=key):
                f = fixture.ReleasedDiagnosticSuccessorTests()
                f.setUp()
                try:
                    handoff = dict(f.h, **{key: m.digest({'synthetic': 'foreign ' + key})})
                    self.assert_claim_replay_rejected(f, handoff)
                    self.assertFalse(f.new.path.exists())
                finally:
                    f.doCleanups()

    def test_drift_rollback_and_conservative_deadline_reject_before_claim(self):
        f = self.f; epoch = f.h['ClockEpochEvidence']
        for wall, mono in ((0, -1), (1_000_000, 2_000_000), (1_000_000, 601_000_000),
                           (599_700_000, 599_700_000)):
            f.new.clock = lambda w=wall: epoch['ObservedUtcMicros'] + w
            f.new.monotonic = lambda n=mono: epoch['ObservedMonoMicros'] + n
            with self.subTest(wall=wall, mono=mono), self.assertRaises(m.Rejected):
                f.operation('claim')
            self.assertEqual(f.before['head'], f.read(f.old)['head'])
            self.assertFalse(f.new.path.exists())

    def test_an_ancestor_approval_cannot_be_reused_for_new_authority(self):
        f = self.f; scope = f.before['scope']
        while scope['schema'] != 1:
            prior = m.Register(scope['predecessorRegisterPath'], scope['predecessorScopeSha256'])
            scope = f.read(prior)['scope']
        changed = dict(f.scope, approvalSha256=scope['approvalSha256'])
        child = m.Register(f.new.path, m.digest(changed), lambda: f.clock.now)
        handoff = dict(f.h, SourceApprovalSha256=scope['approvalSha256'],
            WindowApprovalSha256=scope['approvalSha256'], NewScopeSha256=child.scope_sha)
        with self.assertRaises(m.Rejected): f.old.claim_successor(child, changed, handoff, m.digest(handoff))
        self.assertEqual(f.before['head'], f.read(f.old)['head'])

    def test_completed_readback_after_expiry_does_not_reactivate_or_append(self):
        f = self.f; f.activate()
        old_head = f.read(f.old)['head']; new_head = f.read(f.new)['head']
        f.clock.now = m.utc(f.scope['endUtc']) + 1
        self.assertFalse(f.operation('complete')['authority'])
        self.assertEqual(old_head, f.read(f.old)['head']); self.assertEqual(new_head, f.read(f.new)['head'])
        with self.assertRaises(m.Rejected): f.new.status()

    def test_advancing_monotonic_clock_is_sampled_once_for_completion_evidence(self):
        f = self.f; sample = f.h['ClockEpochEvidence']['ObservedMonoMicros']
        def monotonic():
            nonlocal sample
            sample += 10
            return sample
        f.old.monotonic = monotonic; f.new.monotonic = monotonic
        f.activate()
        self.assertTrue(f.read(f.new)['activated'])
        self.assertIsNone(f.read(f.old)['pending'])
        f.new.require_predecessor_retired(f.gate)

    def test_final_old_wall_clock_rollback_cannot_complete_or_activate(self):
        f = self.f
        f.operation('claim')
        old_before = f.read(f.old); new_before = f.read(f.new)
        original_clock = f.old.clock
        original_guard = f.new._guard
        guarded = []

        def guard_then_rollback(*args, **kwargs):
            result = original_guard(*args, **kwargs)
            guarded.append(f.new._read(args[0]))
            # Readiness and the real child guard saw the valid clock. Only the
            # final predecessor observation moves backwards before its append.
            f.old.clock = lambda: old_before['lastUtc'] - 1_000_000
            return result

        try:
            with patch.object(f.new, '_guard', side_effect=guard_then_rollback):
                with self.assertRaisesRegex(m.Rejected, 'ClockRollback'):
                    f.operation('complete')
        finally:
            f.old.clock = original_clock

        self.assertEqual(1, len(guarded))
        self.assertEqual(old_before, f.read(f.old))
        # The real guard durably adds its ordinary safe check. Nothing after
        # that check may append a completion or activation using clamped UTC.
        self.assertNotEqual(new_before['head'], guarded[0]['head'])
        self.assertEqual(guarded[0], f.read(f.new))
        self.assertFalse(f.read(f.new)['activated'])
