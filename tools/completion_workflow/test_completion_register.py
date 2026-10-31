import concurrent.futures
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

from completion_register import Register, Rejected, digest, utc, path_digest
from guarded_workflow import ReviewedAdapter, CompletionWorkflow, live_workflow


H = hashlib.sha256(b"synthetic identity only").hexdigest()


class RegisterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "scope.db"
        self.scope = {"schema": 1, "approvalSha256": H, "buildSha256": H, "profileSha256": H,
                      "accountSha256": H, "projectSha256": H, "plansSha256": H,
                      "startUtc": "2026-10-31T10:00:00Z", "endUtc": "2026-10-31T16:00:00Z",
                      "maxRefreshes": 24, "maxResumeAdmissions": 24, "registerPathSha256": path_digest(self.path)}
        self.now = utc(self.scope["startUtc"])
        self.pin = digest(self.scope)
        self.r = Register.create(self.path, self.scope, self.pin, lambda: self.now)

    def claim(self, kind="refresh"):
        return self.r.claim(kind, H, str(uuid.uuid4()))

    def receipt(self, claim, **changes):
        result = {"id": claim["id"], "identitySha256": H, "outcome": "Succeeded", "evidenceSha256": H,
                  "catalogVerified": True, "probeInputEmpty": True, "probeHttpStatus": 400,
                  "probeStatus": "INVALID_ARGUMENT", "audioAbsent": True}
        if claim["kind"] == "resume-admission":
            result = {"id": claim["id"], "identitySha256": H, "outcome": "Succeeded", "evidenceSha256": H,
                      "policyVerified": True, "zeroNewFunds": True}
        result.update(changes)
        return result

    def test_independent_24_boundaries_count_claim_before_effect(self):
        for kind in ("refresh", "resume-admission"):
            for ordinal in range(1, 25):
                claim = self.claim(kind)
                self.assertEqual(ordinal, claim["ordinal"])
                self.assertFalse(claim["authority"])
                self.assertEqual("Succeeded", self.r.complete(claim["id"], self.receipt(claim)))
            with self.assertRaisesRegex(Rejected, "OperationLimit"):
                self.claim(kind)
        self.assertEqual(24, self.r.status()["refreshes"])
        self.assertEqual(24, self.r.status()["resumeAdmissions"])

    def test_crash_after_claim_retains_pending_across_restart(self):
        claim = self.claim()
        restarted = Register(self.path, self.pin, lambda: self.now)
        self.assertEqual(claim["id"], restarted.status()["pendingId"])
        with self.assertRaisesRegex(Rejected, "PendingOutcome"):
            restarted.claim("refresh", H, str(uuid.uuid4()))

    def test_replay_cannot_repeat_completed_operation_or_receipt(self):
        claim = self.claim()
        self.r.complete(claim["id"], self.receipt(claim))
        with self.assertRaisesRegex(Rejected, "Replay"):
            self.r.claim("refresh", H, claim["id"])
        with self.assertRaisesRegex(Rejected, "Replay"):
            self.r.complete(claim["id"], self.receipt(claim))

    def test_clock_rollback_stays_stopped_after_clock_recovers(self):
        self.now -= 1
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            self.r.status()
        self.now += 10
        with self.assertRaisesRegex(Rejected, "RunStopped"):
            self.claim()

    def test_exact_deadline_rejected_without_extension(self):
        self.now = utc(self.scope["endUtc"])
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            self.claim()

    def test_outcome_after_deadline_preserved_but_stops(self):
        claim = self.claim()
        self.now = utc(self.scope["endUtc"])
        self.assertEqual("Stopped", self.r.complete(claim["id"], self.receipt(claim)))

    def test_stop_after_unknown_or_failed_probe(self):
        for changes in ({"outcome": "Unknown"}, {"probeHttpStatus": 401}, {"probeStatus": "PERMISSION_DENIED"},
                        {"probeHttpStatus": 200}, {"probeInputEmpty": False}, {"audioAbsent": False},
                        {"catalogVerified": False}, {"probeStatus": ""}):
            with self.subTest(changes=changes):
                path = Path(self.temp.name) / (str(uuid.uuid4()) + ".db")
                scope = dict(self.scope, registerPathSha256=path_digest(path))
                r = Register.create(path, scope, digest(scope), lambda: self.now)
                claim = r.claim("refresh", H, str(uuid.uuid4()))
                self.assertEqual("Stopped", r.complete(claim["id"], self.receipt(claim, **changes)))
                with self.assertRaisesRegex(Rejected, "RunStopped"):
                    r.status()

    def test_resume_nonzero_funds_stops(self):
        claim = self.claim("resume-admission")
        self.assertEqual("Stopped", self.r.complete(claim["id"], self.receipt(claim, zeroNewFunds=False)))

    def test_receipt_mismatch_preserves_pending(self):
        claim = self.claim()
        with self.assertRaisesRegex(Rejected, "Mismatch"):
            self.r.complete(claim["id"], self.receipt(claim, identitySha256="a" * 64))
        self.assertTrue(self.r.status()["requiresReconciliation"])

    def test_missing_storage_never_creates_fresh_register(self):
        missing = Path(self.temp.name) / "missing.db"
        with self.assertRaises(Rejected):
            Register(missing, self.pin, lambda: self.now).status()
        self.assertFalse(missing.exists())

    def test_existing_register_never_reset_or_reinitialized(self):
        with self.assertRaises(FileExistsError):
            Register.create(self.path, self.scope, self.pin, lambda: self.now)

    def test_corrupt_register_rejected(self):
        broken = Path(self.temp.name) / "broken.db"
        broken.write_bytes(b"not a database")
        with self.assertRaises(sqlite3.DatabaseError):
            Register(broken, self.pin, lambda: self.now).status()

    def test_sqlite_exclusive_writer_competition(self):
        db = sqlite3.connect(self.path)
        try:
            db.execute("BEGIN IMMEDIATE")
            with self.assertRaises(sqlite3.OperationalError):
                self.claim()
        finally:
            db.rollback()
            db.close()
        self.assertEqual(0, self.r.status()["refreshes"])

    def test_concurrent_claims_only_one_succeeds(self):
        def attempt(_):
            try:
                return Register(self.path, self.pin, lambda: self.now).claim("refresh", H, str(uuid.uuid4()))
            except (Rejected, sqlite3.OperationalError, OSError):
                return None
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(attempt, range(8)))
        self.assertEqual(1, len([r for r in results if r]))
        self.assertEqual(1, self.r.status()["refreshes"])

    def test_interrupted_insert_rolls_back_no_action_permit(self):
        original = Register._append
        def fault(db, event, previous):
            original(db, event, previous)
            raise OSError("synthetic disk failure after insert before commit")
        with patch.object(Register, "_append", staticmethod(fault)):
            with self.assertRaises(OSError):
                self.claim()
        self.assertEqual(0, self.r.status()["refreshes"])
        self.assertIsNone(self.r.status()["pendingId"])

    def test_disk_or_commit_failure_returns_no_permit(self):
        with patch.object(Register, "_open", side_effect=PermissionError("synthetic denied storage")):
            with self.assertRaises(PermissionError):
                self.claim()
        self.assertEqual(0, self.r.status()["refreshes"])

    def test_tamper_triggers_and_changed_scope_pin(self):
        with closing(sqlite3.connect(self.path)) as db:
            for sql in ("DELETE FROM events", "UPDATE scope SET body='{}'", "DELETE FROM scope"):
                with self.assertRaises(sqlite3.IntegrityError):
                    db.execute(sql)
        with self.assertRaisesRegex(Rejected, "ScopePinMismatch"):
            Register(self.path, "a" * 64, lambda: self.now).status()

    def test_invalid_scope_and_widened_window_rejected(self):
        for delta in ({"schema": True}, {"maxRefreshes": 25}, {"maxResumeAdmissions": 25}, {"endUtc": "2026-10-31T17:00:00Z"},
                      {"startUtc": "2026-10-31T10:00:00"}, {"unexpectedSecret": "fake-private-content"}):
            with self.subTest(delta=delta):
                scope = dict(self.scope, **delta)
                with self.assertRaises(Rejected):
                    Register.create(Path(self.temp.name) / (str(uuid.uuid4()) + ".db"), scope, digest(scope), lambda: self.now)

    def test_before_start_rejected(self):
        path = Path(self.temp.name) / "future.db"
        scope = dict(self.scope, registerPathSha256=path_digest(path))
        with self.assertRaisesRegex(Rejected, "OutsideWindow"):
            Register.create(path, scope, digest(scope), lambda: self.now - 1)

    def test_same_scope_cannot_be_relocated_and_reset(self):
        with self.assertRaisesRegex(Rejected, "RegisterPathPinMismatch"):
            Register.create(Path(self.temp.name) / "other.db", self.scope, self.pin, lambda: self.now)

    def test_database_rollback_detected_by_external_anchor(self):
        old = self.path.read_bytes()
        self.claim()
        self.path.write_bytes(old)
        with self.assertRaisesRegex(Rejected, "AnchorDatabaseMismatch"):
            self.r.status()

    def test_interrupted_anchor_commit_returns_no_permit_and_stops(self):
        with patch.object(Register, "_seal", side_effect=OSError("synthetic disk full during anchor")):
            with self.assertRaises(OSError):
                self.claim()
        with self.assertRaisesRegex(Rejected, "AnchorDatabaseMismatch"):
            self.r.status()

    def test_unknown_receipt_data_not_persisted(self):
        claim = self.claim()
        with self.assertRaisesRegex(Rejected, "ReceiptSchema"):
            self.r.complete(claim["id"], self.receipt(claim, rawMessage="fake-private-content"))
        self.assertNotIn(b"fake-private-content", self.path.read_bytes())

    def test_terminal_status_does_not_grow_journal(self):
        self.now -= 1
        with self.assertRaises(Rejected):
            self.r.status()
        size = self.r.anchor.read_bytes()
        for _ in range(5):
            with self.assertRaisesRegex(Rejected, "RunStopped"):
                self.r.status()
        self.assertEqual(size, self.r.anchor.read_bytes())

    def test_process_exit_after_claim_retains_pending_and_slot(self):
        operation_id = str(uuid.uuid4())
        code = ("import os; from completion_register import Register; "
                f"r=Register({str(self.path)!r},{self.pin!r},lambda:{self.now}); "
                f"r.claim('refresh',{H!r},{operation_id!r}); os._exit(17)")
        result = subprocess.run([sys.executable, "-c", code], cwd=Path(__file__).parent, capture_output=True)
        self.assertEqual(17, result.returncode)
        self.assertEqual(operation_id, self.r.status()["pendingId"])
        self.assertEqual(1, self.r.status()["refreshes"])

    def test_process_exit_after_db_commit_before_anchor_stops(self):
        code = ("import os; from completion_register import Register; "
                f"r=Register({str(self.path)!r},{self.pin!r},lambda:{self.now}); "
                "r._seal=lambda db:os._exit(18); "
                f"r.claim('refresh',{H!r},{str(uuid.uuid4())!r})")
        result = subprocess.run([sys.executable, "-c", code], cwd=Path(__file__).parent, capture_output=True)
        self.assertEqual(18, result.returncode)
        with self.assertRaisesRegex(Rejected, "AnchorDatabaseMismatch"):
            self.r.status()

    def test_no_native_live_adapter_is_admitted(self):
        for route in ("create-mp3-autoload", "catalog-refresh-button", "bootstrap-direct", "resume-installer"):
            with self.assertRaisesRegex(Rejected, "NoReviewedOwningAdapter"):
                live_workflow(self.r).run(route, str(uuid.uuid4()))
        self.assertEqual(0, self.r.status()["refreshes"])

    def test_owned_synthetic_adapter_runs_only_after_durable_claim(self):
        def observed(ticket):
            self.assertEqual(ticket["id"], self.r.status()["pendingId"])
            return self.receipt(ticket)
        adapter = ReviewedAdapter("refresh", H, H, observed)
        workflow = CompletionWorkflow(self.r, {"synthetic": adapter}, {"synthetic": (H, H)})
        self.assertEqual("Succeeded", workflow.run("synthetic", str(uuid.uuid4())))
        self.assertEqual(1, self.r.status()["refreshes"])

    def test_adapter_mismatch_does_not_execute_or_claim(self):
        def forbidden(_):
            self.fail("Unreviewed callback executed")
        adapter = ReviewedAdapter("refresh", H, H, forbidden)
        workflow = CompletionWorkflow(self.r, {"synthetic": adapter}, {"synthetic": (H, "a" * 64)})
        with self.assertRaisesRegex(Rejected, "AdapterReviewPinMismatch"):
            workflow.run("synthetic", str(uuid.uuid4()))
        self.assertEqual(0, self.r.status()["refreshes"])

    def test_adapter_exception_stays_unknown_across_restart(self):
        def interrupted(_):
            raise OSError("fake-secret never persist this prose")
        adapter = ReviewedAdapter("refresh", H, H, interrupted)
        workflow = CompletionWorkflow(self.r, {"synthetic": adapter}, {"synthetic": (H, H)})
        with self.assertRaises(OSError):
            workflow.run("synthetic", str(uuid.uuid4()))
        self.assertTrue(self.r.status()["requiresReconciliation"])
        self.assertNotIn(b"fake-secret", self.path.read_bytes())

    def test_deadline_crossed_during_claim_seal_never_invokes_callback(self):
        seal = Register._seal
        def slow_seal(register, db):
            seal(register, db)
            event = json.loads(db.execute("SELECT body FROM events ORDER BY seq DESC LIMIT 1").fetchone()[0])
            if event["type"] == "claim":
                self.now = utc(self.scope["endUtc"])
        def forbidden(_):
            self.fail("Expired callback executed")
        adapter = ReviewedAdapter("refresh", H, H, forbidden)
        workflow = CompletionWorkflow(self.r, {"synthetic": adapter}, {"synthetic": (H, H)})
        with patch.object(Register, "_seal", slow_seal):
            with self.assertRaisesRegex(Rejected, "WindowExpired"):
                workflow.run("synthetic", str(uuid.uuid4()))

    def test_adapter_rechecks_after_approval_delay_before_effect(self):
        effects = []
        def delayed(ticket):
            self.now = utc(self.scope["endUtc"])
            self.r.require_pending_before_effect(ticket["id"], H)
            effects.append("should never occur")
        adapter = ReviewedAdapter("refresh", H, H, delayed)
        workflow = CompletionWorkflow(self.r, {"synthetic": adapter}, {"synthetic": (H, H)})
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            workflow.run("synthetic", str(uuid.uuid4()))
        self.assertEqual([], effects)

    def test_unobserved_wall_clock_change_cannot_extend_monotonic_deadline(self):
        path = Path(self.temp.name) / "monotonic.db"
        scope = dict(self.scope, registerPathSha256=path_digest(path))
        monotonic = [1000000]
        register = Register.create(path, scope, digest(scope), lambda: self.now, lambda: monotonic[0])
        self.now += 60 * 1000000
        monotonic[0] += 120 * 1000000
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            register.status()

    def test_ordinary_checks_persist_time_high_water_across_restart(self):
        self.now += 1000000
        self.r.status()
        restarted = Register(self.path, self.pin, lambda: self.now)
        self.now -= 1
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            restarted.status()

    def test_monotonic_reset_or_clock_discontinuity_stops(self):
        path = Path(self.temp.name) / "reboot.db"
        scope = dict(self.scope, registerPathSha256=path_digest(path))
        monotonic = [1000000]
        register = Register.create(path, scope, digest(scope), lambda: self.now, lambda: monotonic[0])
        monotonic[0] -= 1
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            register.status()

    def test_slow_initialization_samples_start_clock_after_storage_delay(self):
        path = Path(self.temp.name) / "slow-initialize.db"
        scope = dict(self.scope, registerPathSha256=path_digest(path))
        initialize = Register._initialize
        def delayed(register, body, reviewed_sha, early_now):
            self.now += 5000000
            initialize(register, body, reviewed_sha, early_now)
        with patch.object(Register, "_initialize", delayed):
            register = Register.create(path, scope, digest(scope), lambda: self.now)
        self.assertEqual(0, register.status()["refreshes"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
