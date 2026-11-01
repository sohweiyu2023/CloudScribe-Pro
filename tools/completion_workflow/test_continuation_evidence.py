"""Negative actual bridge proofs using only the existing inert SQLite fixture."""
from contextlib import closing
import json
import sqlite3
import unittest
import uuid

import test_continuation_cli as fixtures


class ContinuationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.f = fixtures.ContinuationCliTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)

    def mutate(self, sql, parameters=()):
        with closing(sqlite3.connect(self.f.f.db)) as connection:
            connection.execute(sql, parameters); connection.commit()

    def retained_reservations(self):
        with closing(sqlite3.connect(self.f.f.db)) as connection:
            return connection.execute("SELECT AuthorizationId,ReservedMinorUnits FROM chirp_article_owned_attempts ORDER BY AuthorizationId").fetchall()

    def expect_code(self, result, code):
        self.assertEqual(1, result[0], result)
        self.assertEqual(code, result[1]["code"], result)
        self.assertTrue(result[1]["requiresReconciliation"])

    def test_remaining_accepted_queue_before_persistence_cannot_be_admitted_as_new(self):
        self.f.activate(); policy_path = self.f.policy(); policy = json.loads(policy_path.read_text())
        auth, _ = self.f.add_attempt(8, policy, True)
        self.mutate("DELETE FROM chirp_article_receipts WHERE AuthorizationId=?", (auth,))
        self.mutate("DELETE FROM chirp_article_output_journals WHERE AuthorizationId=?", (auth,))
        before = self.retained_reservations()
        self.expect_code(self.f.command("claim-completion", policy_path), "ContinuationRemainingNotUntouched")
        self.assertEqual(before, self.retained_reservations())
        status = self.f.command("status"); self.assertEqual(0, status[0], status)
        self.assertEqual(0, status[1]["totalAdmissions"])

    def test_original_accepted_member_missing_output_journal_blocks_transition(self):
        auth = self.f.accepted[0]["authorizationId"]
        self.mutate("DELETE FROM chirp_article_output_journals WHERE AuthorizationId=?", (auth,))
        # The reviewed DB pin alone cannot authenticate incomplete accepted proof.
        self.f.h["DatabaseSha256"] = fixtures.sha(self.f.f.db.read_bytes())
        self.f.h_path.write_text(json.dumps(self.f.h)); self.f.h_pin = fixtures.sha(self.f.h_path.read_bytes())
        self.expect_code(self.f.command("inspect-successor"), "ContinuationOutputIncomplete")
        self.assertFalse(self.f.new_folder.joinpath("register.db").exists())

    def test_progress_missing_final_receipt_preserves_recoverable_journal_and_hold(self):
        self.f.activate(); path = self.f.admit(); policy = json.loads(path.read_text())
        auth, _ = self.f.add_attempt(8, policy, True)
        self.mutate("DELETE FROM chirp_article_receipts WHERE AuthorizationId=?", (auth,))
        before = self.retained_reservations()
        self.expect_code(self.f.command("verify-completion", path), "ContinuationOutputIncomplete")
        self.assertEqual(before, self.retained_reservations())
        with closing(sqlite3.connect(self.f.f.db)) as connection:
            self.assertEqual(1, connection.execute("SELECT COUNT(*) FROM chirp_article_output_journals WHERE AuthorizationId=?", (auth,)).fetchone()[0])

    def test_unadmitted_installed_policy_cannot_establish_retention(self):
        self.f.activate(); path = self.f.policy()
        self.f.install_policy(path, self.f.config, self.f.pin)
        self.expect_code(self.f.command("verify-completion", path), "ContinuationRetentionNotAdmitted")

    def test_wrong_target_second_held_and_uncertain_work_cannot_resume(self):
        self.f.activate(); path = self.f.admit(); policy = json.loads(path.read_text())
        self.f.add_attempt(8, policy, True); held, envelope = self.f.add_attempt(9, policy, False)
        resume = {"HistoricalPolicy": policy, "AuthorizationId": str(uuid.uuid4()), "ExecutionEnvelope": envelope,
                  "ExpiresAtUtc": self.f.scope["endUtc"], "MaximumExistingDispatches": 1,
                  "MaximumNewReservations": 0, "MaximumAdditionalReservationMinorUnits": 0}
        resume_path = self.f.new_folder / "negative-resume.json"; resume_path.write_text(json.dumps(resume))
        before = self.retained_reservations()
        self.expect_code(self.f.command("claim-resume", resume_path), "ContinuationResumeTarget")
        self.assertEqual(before, self.retained_reservations())
        second, _ = self.f.add_attempt(10, policy, False)
        self.expect_code(self.f.command("verify-completion", path), "ContinuationResumeTarget")
        self.mutate("UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey=(SELECT ChildKey FROM chirp_article_attempts WHERE AuthorizationId=?)", (held,))
        result = self.f.command("verify-completion", path)
        self.expect_code(result, "PendingOrUncertainWork")
        after = self.retained_reservations()
        self.assertIn((held, 1), after); self.assertIn((second, 1), after)


if __name__ == "__main__":
    unittest.main(verbosity=2)
