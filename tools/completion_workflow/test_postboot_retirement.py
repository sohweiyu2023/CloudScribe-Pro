"""Expired authority retirement only; synthetic journals, no user profile or provider."""
import concurrent.futures
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import completion_register as m
import test_continuation_register as fixtures
import test_continuation_cli as cli_fixtures
import gate_cli


class ExpiredMonotonicRetirementTests(unittest.TestCase):
    def setUp(self):
        self.f = fixtures.ContinuationRegisterTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.f.f.b.f.now += 6 * 3600 * 1_000_000 + 1
        start = datetime.fromtimestamp(self.f.f.b.f.now / 1_000_000, timezone.utc)
        self.f.scope.update(startUtc=start.isoformat().replace("+00:00", "Z"),
                            endUtc=(start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"))
        self.f.new = m.Register(self.f.path, m.digest(self.f.scope), lambda: self.f.f.b.f.now)
        self.f.h["NewScopeSha256"] = self.f.new.scope_sha
        self.observed = 1_000_000  # Inert example of a fresh monotonic epoch.
        self.f.old.monotonic = lambda: self.observed

    def test_expired_retirement_preserves_anchors_and_audits_raw_observation(self):
        before = self.f.read(self.f.old)
        self.assertEqual("ClockRollback", self.f.old._time_rejection(before))
        self.f.activate(); after = self.f.read(self.f.old)
        for key in ("startUtc", "startMono", "scope", "refresh", "admissions"):
            self.assertEqual(before[key], after[key])
        event = after["retirement"]
        self.assertEqual("ExpiredMonotonicDiscontinuity", event["clockCondition"])
        self.assertEqual(self.observed, event["observedMono"])
        self.assertEqual(before["lastMono"], event["mono"])
        self.assertTrue(after["retired"])
        self.assertEqual(0, self.f.new.status()["totalAdmissions"])

    def test_backward_utc_unexpired_old_and_original_scope_still_reject(self):
        s = self.f.read(self.f.old)
        for state, now in ((s, s["lastUtc"] - 1), (s, m.utc(s["scope"]["endUtc"]) - 1),
                           (dict(s, scope=dict(s["scope"], schema=1)), self.f.f.b.f.now)):
            with self.subTest(now=now, schema=state["scope"]["schema"]), self.assertRaisesRegex(m.Rejected, "ClockRollback"):
                self.f.old._retirement_observation(state, now, self.observed)

    def test_ordinary_operation_still_commits_stop_and_cannot_be_retired(self):
        with self.assertRaisesRegex(m.Rejected, "ClockRollback"):
            self.f.old.claim("refresh", fixtures.H, "49df34fa-f0a2-4088-b6a8-1a6ca1ab446d")
        self.f.h["ExpectedPreviousHeadSha256"] = self.f.read(self.f.old)["head"]
        with self.assertRaisesRegex(m.Rejected, "RetirementClockOrStop"):
            self.f.old.claim_successor(self.f.new, self.f.scope, self.f.h, fixtures.H)
        self.assertFalse(self.f.new.path.exists())

    def test_pending_work_and_changed_authority_do_not_create_successor(self):
        s = self.f.read(self.f.old)
        with self.assertRaisesRegex(m.Rejected, "ClockRollback"):
            self.f.old._retirement_observation(dict(s, pending={"type": "claim"}), self.f.f.b.f.now, self.observed)
        bad = dict(self.f.scope, approvalSha256=s["scope"]["approvalSha256"])
        with self.assertRaises(m.Rejected):
            self.f.old.inspect_successor(self.f.new, bad, self.f.h, fixtures.H)
        self.assertFalse(self.f.new.path.exists())

    def test_further_monotonic_reset_during_pending_retirement_stops(self):
        self.f.old.claim_successor(self.f.new, self.f.scope, self.f.h, fixtures.H)
        self.observed -= 1
        with self.assertRaisesRegex(m.Rejected, "ClockRollback"):
            self.f.old.check_successor(self.f.new, self.f.scope, self.f.h, fixtures.H)
        self.assertFalse(self.f.read(self.f.new)["activated"])
        self.assertEqual("ClockRollback", self.f.read(self.f.old)["stopReason"])

    def test_interruption_after_retirement_only_resumes_same_identity(self):
        self.f.old.claim_successor(self.f.new, self.f.scope, self.f.h, fixtures.H)
        append = self.f.new._append
        def fail(db, event, previous):
            if event["type"] == "activate": raise OSError("synthetic interruption")
            return append(db, event, previous)
        with patch.object(self.f.new, "_append", side_effect=fail), self.assertRaises(OSError):
            self.f.old.complete_successor(self.f.new, self.f.scope, self.f.h, fixtures.H, fixtures.H)
        self.assertTrue(self.f.read(self.f.old)["retired"])
        self.f.old.complete_successor(self.f.new, self.f.scope, self.f.h, fixtures.H, fixtures.H)
        self.f.new.require_predecessor_retired(fixtures.NEW_GATE)
        with self.assertRaises(m.Rejected):
            self.f.old.complete_successor(self.f.new, self.f.scope, self.f.h, fixtures.H, fixtures.H)

    def test_rehashed_observation_or_condition_tamper_is_rejected(self):
        self.f.old.claim_successor(self.f.new, self.f.scope, self.f.h, fixtures.H)
        with self.f.old._open() as db:
            db.execute("DROP TRIGGER immutable_events_update")
            seq, body = db.execute("SELECT seq,body FROM events ORDER BY seq DESC LIMIT 1").fetchone()
            previous = db.execute("SELECT sha FROM events WHERE seq=?", (seq - 1,)).fetchone()[0]
            event = json.loads(body); event["observedMono"] += 1
            db.execute("UPDATE events SET body=?,sha=? WHERE seq=?", (m.canonical(event), m.digest({"previous": previous, "event": event}), seq))
            db.commit(); self.f.old.anchor.write_bytes(b"".join(f"{i}:{h}\n".encode() for i, h in db.execute("SELECT seq,sha FROM events ORDER BY seq")))
        with self.assertRaisesRegex(m.Rejected, "InvalidRetirementHistory"):
            self.f.read(self.f.old)

    def test_concurrent_claims_only_one_retirement(self):
        def claim():
            try: self.f.old.claim_successor(self.f.new, self.f.scope, self.f.h, fixtures.H); return True
            except (m.Rejected, OSError): return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: claim(), range(2))))


class ExpiredMonotonicProductionBridgeTests(unittest.TestCase):
    def test_actual_successor_entrypoints_with_fresh_monotonic_epoch(self):
        f = cli_fixtures.ContinuationCliTests(); f.setUp(); self.addCleanup(f.doCleanups)
        # Only test clocks are injected. The operational protocol has no clock override.
        now = m.utc(f.f.scope["endUtc"]) + 1_000_000
        start = datetime.fromtimestamp(now / 1_000_000, timezone.utc)
        f.scope.update(startUtc=start.isoformat().replace("+00:00", "Z"),
                       endUtc=(start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"))
        Path(f.h["NewScopePath"]).write_text(json.dumps(f.scope))
        f.config["ScopeSha256"] = m.digest(f.scope)
        f.config_path.write_text(json.dumps(f.config)); f.pin = gate_cli.sha(f.config_path.read_bytes())
        f.h.update(NewScopeSha256=f.config["ScopeSha256"], NewGateSha256=f.pin)
        f.h_path.write_text(json.dumps(f.h)); f.h_pin = gate_cli.sha(f.h_path.read_bytes())
        def call(command):
            installed = command == "complete-successor"
            request = {"command": command, "handoffPath": str(f.h_path), "handoffSha256": f.h_pin,
                       "identitySha256": f.h_pin, "id": f.h["TransitionId"], "configSha256": f.pin,
                       "configPath": str(f.f.b.f.app.parent / "completion-workflow-gate.json" if installed else f.config_path)}
            return gate_cli.successor_command(request, f.config, set(f.config), m,
                                               f.f.b.f.app if installed else f.f.new_app)
        with patch.object(m.time, "time_ns", return_value=now * 1000), patch.object(m.time, "monotonic_ns", return_value=1_000_000_000):
            self.assertTrue(call("inspect-successor")["valid"])
            call("claim-successor"); self.assertTrue(call("check-successor")["valid"])
            f.f.b.write_installed(f.config, f.pin, f.config_path)
            self.assertEqual("Succeeded", call("complete-successor")["outcome"])
            new = m.Register(f.config["RegisterPath"], f.config["ScopeSha256"])
            new.require_predecessor_retired(f.pin)
            self.assertEqual(0, new.status()["totalAdmissions"])
            old_config = json.loads(Path(f.h["PreviousConfigPath"]).read_text())
            old = m.Register(old_config["RegisterPath"], old_config["ScopeSha256"])
            with old._open() as db: state = old._read(db)
            self.assertTrue(state["retired"])
            self.assertEqual("ExpiredMonotonicDiscontinuity", state["retirement"]["clockCondition"])
            self.assertEqual(1_000_000, state["retirement"]["observedMono"])


if __name__ == "__main__":
    unittest.main()
