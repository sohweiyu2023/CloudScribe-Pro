"""Synthetic linked1->2->3 counters only; no profile/provider/installer access."""
import concurrent.futures
import json
import unittest
import uuid
from unittest.mock import patch

from completion_register import Register, Rejected, canonical, digest, path_digest, validate_scope, validate_handoff
import test_successor_run as successor_fixtures
from test_successor_run import H, sha

NEW_GATE = sha(b"reviewed continuation gate")
EVIDENCE = sha(b"reviewed actual seven accepted evidence fixture")


class ContinuationRegisterTests(unittest.TestCase):
    def setUp(self):
        self.f = successor_fixtures.SuccessorCoreTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.f.claim(); self.f.complete(); self.old = self.f.new; self.root = self.f.old
        pilot = self.old.claim("pilot-admission", H, str(uuid.uuid4()))
        self.old.complete(pilot["id"], self.receipt(pilot, 1))
        completed = self.old.claim("completion-admission", H, str(uuid.uuid4()), pilot_proof_sha=H)
        self.old.complete(completed["id"], self.receipt(completed, 125))
        self.path = self.old.path.parent / "continuation.db"
        self.scope = dict(self.f.scope, schema=3, approvalSha256=sha(b"NEW explicit continuation approval"),
                          acceptedEvidenceSha256=EVIDENCE, maximumNewAttempts=119, plansSha256=sha(b"exact119projection"),
                          predecessorRegisterPath=str(self.old.path), predecessorScopeSha256=self.old.scope_sha,
                          registerPathSha256=path_digest(self.path), transitionId=str(uuid.uuid4()))
        self.new = Register(self.path, digest(self.scope), lambda: self.f.b.f.now)
        state = self.read(self.old)
        self.h = {"SchemaVersion": 2, "TransitionKind": "accepted-continuation", "TransitionId": self.scope["transitionId"],
                  "SourceApprovalSha256": self.scope["approvalSha256"], "PreviousConfigPath": str(self.path.parent / "old2.json"),
                  "PreviousGateSha256": H, "PreviousScopeSha256": self.old.scope_sha,
                  "ExpectedPreviousHeadSha256": state["head"], "NewConfigPath": str(self.path.parent / "new3.json"),
                  "NewGateSha256": NEW_GATE, "NewScopePath": str(self.path.parent / "scope3.json"),
                  "NewScopeSha256": self.new.scope_sha, "DatabaseSha256": H,
                  "OriginalManifestSha256": state["scope"]["plansSha256"], "AcceptedEvidenceSha256": EVIDENCE}

    def read(self, register):
        with register._open() as db:
            return register._read(db)

    def receipt(self, claim, maximum=None):
        result = {"id": claim["id"], "identitySha256": H, "outcome": "Succeeded", "evidenceSha256": H, "policyVerified": True}
        if claim["kind"] == "resume-admission": result["zeroNewFunds"] = True
        else: result.update(maximumNewAttempts=maximum, additionalReservationCents=maximum)
        return result

    def activate(self):
        self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.old.claim_successor(self.new, self.scope, self.h, H)
        self.old.check_successor(self.new, self.scope, self.h, H)
        self.old.complete_successor(self.new, self.scope, self.h, H, H)
        self.new.require_predecessor_retired(NEW_GATE)

    def test_exact_scope_and_handoff_shapes_and_boundaries(self):
        validate_scope(self.scope); validate_handoff(self.h)
        for value in (0, 126, True, "119"):
            with self.subTest(value=value), self.assertRaises(Rejected):
                validate_scope(dict(self.scope, maximumNewAttempts=value))
        for bad in (dict(self.h, SchemaVersion=1), dict(self.h, TransitionKind="failed-range"),
                    dict(self.h, FailedCoverageOrdinal=3), dict(self.h, AcceptedEvidenceSha256="0" * 64)):
            with self.assertRaises(Rejected): validate_handoff(bad)

    def test_inactive3_has_no_authority_and_no_pilot(self):
        Register.create(self.path, self.scope, self.new.scope_sha, lambda: self.f.b.f.now)
        with self.assertRaisesRegex(Rejected, "SuccessorNotActivated"):
            self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)

    def test_chain_activation_preserves_old_history_counts_and_holds_metadata(self):
        before = self.read(self.old); root_bytes = self.root.path.read_bytes(); old_events = None
        with self.old._open() as db: old_events = db.execute("SELECT seq,body,sha FROM events ORDER BY seq").fetchall()
        self.activate(); after = self.read(self.old)
        for key in ("scope", "startUtc", "startMono", "admissions", "pilot-admission", "completion-admission", "refresh"):
            self.assertEqual(before[key], after[key])
        with self.old._open() as db:
            self.assertEqual(old_events, db.execute("SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?", (len(old_events),)).fetchall())
        self.assertEqual(root_bytes, self.root.path.read_bytes())
        self.assertTrue(after["retired"]); self.assertTrue(self.read(self.new)["activated"])
        self.assertEqual(0, self.new.status()["totalAdmissions"])

    def test_exact_evidence_required_completion119_and_one_completion_only(self):
        self.activate()
        for proof in (None, H):
            with self.assertRaises(Rejected): self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=proof)
        with self.assertRaisesRegex(Rejected, "UnsupportedOperation"):
            self.new.claim("pilot-admission", H, str(uuid.uuid4()))
        claim = self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)
        self.assertEqual(EVIDENCE, self.read(self.new)["pending"]["acceptedProofSha256"])
        self.new.complete(claim["id"], self.receipt(claim, 119))
        with self.assertRaisesRegex(Rejected, "OperationLimit"):
            self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)

    def test_over119_receipt_stops_and_cannot_silently_retry(self):
        self.activate(); claim = self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)
        self.assertEqual("Stopped", self.new.complete(claim["id"], self.receipt(claim, 120)))
        with self.assertRaisesRegex(Rejected, "RunStopped"): self.new.claim("resume-admission", H, str(uuid.uuid4()))

    def test_total24_includes_completion_and_resume(self):
        self.activate(); claim = self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)
        self.new.complete(claim["id"], self.receipt(claim, 119))
        for _ in range(23):
            claim = self.new.claim("resume-admission", H, str(uuid.uuid4())); self.new.complete(claim["id"], self.receipt(claim))
        self.assertEqual(24, self.new.status()["totalAdmissions"])
        with self.assertRaisesRegex(Rejected, "OperationLimit"): self.new.claim("resume-admission", H, str(uuid.uuid4()))

    def test_wrong_manifest_evidence_approval_or_skipped_generation_rejects(self):
        for h in (dict(self.h, OriginalManifestSha256=NEW_GATE), dict(self.h, AcceptedEvidenceSha256=H)):
            with self.assertRaises(Rejected): self.old.inspect_successor(self.new, self.scope, h, H)
        bad = dict(self.scope, approvalSha256=self.f.scope["approvalSha256"])
        with self.assertRaises(Rejected): self.old.claim_successor(Register(self.path, digest(bad)), bad, self.h, H)
        with self.assertRaises(Rejected): self.root.claim_successor(self.new, self.scope, self.h, H)

    def test_pending_ancestor_or_corrupt_ancestor_fails_without_new_register(self):
        with patch.object(Register, "_require_ancestor_snapshot", side_effect=Rejected("PredecessorNotRetired")):
            with self.assertRaises(Rejected): self.old.claim_successor(self.new, self.scope, self.h, H)
        self.assertFalse(self.path.exists())
        original = self.root.anchor.read_bytes(); self.root.anchor.write_bytes(original + b"invalid\n")
        with self.assertRaises(Rejected): self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.root.anchor.write_bytes(original)

    def test_post_activation_ancestor_corruption_detected_without_deadlock(self):
        self.activate(); self.root.anchor.write_bytes(b"invalid\n")
        with self.assertRaises(Rejected): self.new.require_predecessor_retired(NEW_GATE)

    def test_interruption_same_id_only_and_replay_failclosed(self):
        self.old.claim_successor(self.new, self.scope, self.h, H)
        append = self.new._append
        def fail(db, event, previous):
            if event["type"] == "activate": raise OSError("synthetic storage interruption")
            return append(db, event, previous)
        with patch.object(self.new, "_append", side_effect=fail):
            with self.assertRaises(OSError): self.old.complete_successor(self.new, self.scope, self.h, H, H)
        self.assertTrue(self.read(self.old)["retired"])
        self.old.complete_successor(self.new, self.scope, self.h, H, H)
        self.new.require_predecessor_retired(NEW_GATE)
        with self.assertRaises(Rejected): self.old.complete_successor(self.new, self.scope, self.h, H, H)

    def test_concurrent_claims_only_one_and_expiry_does_not_renew(self):
        def claim():
            try: self.old.claim_successor(self.new, self.scope, self.h, H); return True
            except (Rejected, OSError): return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: claim(), range(2))))
        self.f.b.f.now += 6 * 3600 * 1_000_000
        with self.assertRaisesRegex(Rejected, "WindowExpired"): self.old.complete_successor(self.new, self.scope, self.h, H, H)
        self.assertFalse(self.read(self.new)["activated"])

    def test_rehashed_wrong_accepted_evidence_event_fails_replay(self):
        self.activate(); self.new.claim("completion-admission", H, str(uuid.uuid4()), accepted_proof_sha=EVIDENCE)
        with self.new._open() as db:
            db.execute("DROP TRIGGER immutable_events_update")
            seq, body = db.execute("SELECT seq,body FROM events ORDER BY seq DESC LIMIT 1").fetchone()
            previous = db.execute("SELECT sha FROM events WHERE seq=?", (seq - 1,)).fetchone()[0]
            event = json.loads(body); event["acceptedProofSha256"] = H
            db.execute("UPDATE events SET body=?,sha=? WHERE seq=?", (canonical(event), digest({"previous": previous, "event": event}), seq))
            db.commit(); self.new.anchor.write_bytes(b"".join(f"{s}:{pin}\n".encode() for s, pin in db.execute("SELECT seq,sha FROM events ORDER BY seq")))
        with self.assertRaisesRegex(Rejected, "ContinuationAcceptedProof"): self.read(self.new)


if __name__ == "__main__":
    unittest.main()
