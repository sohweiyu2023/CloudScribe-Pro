"""Inert two-register handoff and actual local pilot-evidence fixtures; no providers."""
import concurrent.futures
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import unittest
import uuid
from unittest.mock import patch

import test_build_transition as bt
from completion_register import Register, Rejected, canonical, digest, path_digest, verify_plan_policy


H = bt.H
sha = bt.cli_fixtures.sha


def plans():
    manifest, base, _ = bt.cli_fixtures.make_plan_fixture(digest)
    proposal = copy.deepcopy(base["HistoricalPolicy"]["Articles"][0]["Proposal"])
    proposal["Parts"] = [{"CoverageOrdinal": i, "Range": {"StartScalar": (i - 1) * 3, "EndScalarExclusive": i * 3,
                         "TextSha256": sha(str(i).encode()), "ReusedReceiptSha256": None},
                          "PayloadSha256": sha(("payload" + str(i)).encode()), "PayloadBytes": 100} for i in range(1, 127)]
    proposal["SourceScalars"] = 378
    manifest = {"schema": 2, "accountId": "synthetic-account", "projectId": "synthetic-project",
                "proposals": [{"planSha256": proposal["PlanSha256"], "proposalSha256": digest(proposal)}],
                "parts": [{"planSha256": proposal["PlanSha256"], "coverageOrdinal": x["CoverageOrdinal"],
                           "payloadSha256": x["PayloadSha256"], "payloadBytes": x["PayloadBytes"], "voiceName": proposal["VoiceName"],
                           "languageCode": proposal["LanguageCode"], "kind": "new"} for x in proposal["Parts"]],
                "pilot": {"planSha256": proposal["PlanSha256"], "coverageOrdinal": 1}}
    return manifest, proposal


class SuccessorCoreTests(unittest.TestCase):
    def setUp(self):
        self.b = bt.BuildRegisterTests(); self.b.setUp(); self.addCleanup(self.b.doCleanups)
        self.b.claim(); self.b.complete(); self.old = self.b.r
        self.new_path = self.b.f.path.parent / "new.db"
        self.scope = dict(self.b.f.scope, schema=2, maxAdmissions=24, approvalSha256=sha(b"NEW SYNTHETIC HUMAN APPROVAL"),
                          registerPathSha256=path_digest(self.new_path), predecessorRegisterPath=str(self.old.path),
                          predecessorScopeSha256=self.old.scope_sha, transitionId=str(uuid.uuid4()))
        self.new = Register(self.new_path, digest(self.scope), lambda: self.b.f.now)
        self.h = self.handoff()

    def read(self, r):
        with r._open() as db:
            return r._read(db)

    def handoff(self):
        state = self.read(self.old)
        return {"SchemaVersion": 1, "TransitionId": self.scope["transitionId"], "SourceApprovalSha256": self.scope["approvalSha256"],
                "PreviousConfigPath": str(self.b.f.path.parent / "old.json"), "PreviousGateSha256": state["currentGate"],
                "PreviousScopeSha256": self.old.scope_sha, "ExpectedPreviousHeadSha256": state["head"],
                "NewConfigPath": str(self.b.f.path.parent / "new.json"), "NewGateSha256": H,
                "NewScopePath": str(self.b.f.path.parent / "scope.json"), "NewScopeSha256": self.new.scope_sha,
                "DatabaseSha256": H, "FailedAuthorizationId": str(uuid.uuid4()), "FailedLocalAttemptId": "a" * 32,
                "FailedPlanSha256": H, "FailedCoverageOrdinal": 3, "FailedPayloadSha256": H, "FailedPayloadBytes": 100}

    def claim(self):
        return self.old.claim_successor(self.new, self.scope, self.h, H)

    def complete(self):
        return self.old.complete_successor(self.new, self.scope, self.h, H, H)

    def test_retirement_then_activation_preserves_old_and_starts_new_separately(self):
        before = self.read(self.old)
        self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.assertFalse(self.new.path.exists())
        self.claim()
        for r in (self.old, self.new):
            with self.assertRaises(Rejected): r.claim("refresh", H, str(uuid.uuid4()))
        self.old.check_successor(self.new, self.scope, self.h, H)
        self.complete()
        after = self.read(self.old)
        for key in ("scope", "startUtc", "startMono", "refresh", "admissions", "buildTransitions", "helperSourceRepairs"):
            self.assertEqual(before[key], after[key])
        self.assertTrue(after["retired"])
        self.new.require_predecessor_retired(H)
        self.assertEqual(0, self.new.status()["totalAdmissions"])

    def test_new_register_cannot_self_activate_or_reuse_approval(self):
        fresh = Register.create(self.new_path, self.scope, self.new.scope_sha, lambda: self.b.f.now)
        with self.assertRaisesRegex(Rejected, "SuccessorNotActivated"): fresh.claim("pilot-admission", H, str(uuid.uuid4()))
        with self.assertRaises(Rejected): self.claim()

    def test_wrong_head_profile_and_replay_reject(self):
        bad = dict(self.h, ExpectedPreviousHeadSha256=sha(b"wrong"))
        with self.assertRaises(Rejected): self.old.claim_successor(self.new, self.scope, bad, H)
        bad_scope = dict(self.scope, profileSha256=sha(b"other-profile"))
        with self.assertRaises(Rejected): self.old.claim_successor(Register(self.new_path, digest(bad_scope)), bad_scope, self.h, H)
        self.claim(); self.complete()
        with self.assertRaises(Rejected): self.claim()
        with self.assertRaises(Rejected): self.complete()

    def test_unknown_old_pending_prevents_retirement(self):
        self.old.claim("refresh", H, str(uuid.uuid4()))
        self.h["ExpectedPreviousHeadSha256"] = self.read(self.old)["head"]
        with self.assertRaises(Rejected): self.claim()

    def test_crash_between_old_complete_and_new_activate_can_only_reconcile_same_id(self):
        self.claim()
        original = self.new._append
        def fail(db, event, previous):
            if event["type"] == "activate": raise OSError("synthetic interruption")
            return original(db, event, previous)
        with patch.object(self.new, "_append", side_effect=fail):
            with self.assertRaises(OSError): self.complete()
        self.assertTrue(self.read(self.old)["retired"])
        self.assertFalse(self.read(self.new)["activated"])
        wrong = dict(self.h, TransitionId=str(uuid.uuid4()))
        with self.assertRaises(Rejected): self.old.complete_successor(self.new, self.scope, wrong, H, H)
        self.complete(); self.new.require_predecessor_retired(H)

    def test_old_anchor_fault_returns_no_new_permit(self):
        with patch.object(self.old, "_seal", side_effect=OSError("synthetic storage")):
            with self.assertRaises(OSError): self.claim()
        self.assertFalse(self.new.path.exists())
        with self.assertRaises(Rejected): self.read(self.old)

    def test_clock_rollback_and_new_deadline_fail_closed(self):
        self.claim(); self.b.f.now -= 1
        with self.assertRaises(Rejected): self.old.check_successor(self.new, self.scope, self.h, H)
        self.b.f.now += 10
        with self.assertRaises(Rejected): self.complete()

    def test_concurrent_crossrun_claims_issue_one_permit(self):
        def attempt():
            try: self.claim(); return True
            except (Rejected, OSError): return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: attempt(), range(2))))

    def test_admissions_are_24_total_and_completion_requires_pilot_proof(self):
        self.claim(); self.complete()
        with self.assertRaises(Rejected): self.new.claim("completion-admission", H, str(uuid.uuid4()))
        p = self.new.claim("pilot-admission", H, str(uuid.uuid4()))
        receipt = {"id": p["id"], "identitySha256": H, "outcome": "Succeeded", "evidenceSha256": H,
                   "policyVerified": True, "maximumNewAttempts": 1, "additionalReservationCents": 1}
        self.new.complete(p["id"], receipt)
        for _ in range(23):
            item = self.new.claim("resume-admission", H, str(uuid.uuid4()))
            self.new.complete(item["id"], {"id": item["id"], "identitySha256": H, "outcome": "Succeeded",
                                          "evidenceSha256": H, "policyVerified": True, "zeroNewFunds": True})
        self.assertEqual(24, self.new.status()["totalAdmissions"])
        with self.assertRaisesRegex(Rejected, "OperationLimit"): self.new.claim("resume-admission", H, str(uuid.uuid4()))

    def forward_clock(self, drift=4_773_917):
        self.b.f.now += 17_300_000_000
        self.mono = self.b.f.now - drift
        self.old.monotonic = lambda: self.mono
        start = datetime.fromtimestamp(self.b.f.now / 1_000_000, timezone.utc)
        self.scope.update(startUtc=start.isoformat().replace("+00:00", "Z"),
                          endUtc=(start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"))
        self.new = Register(self.new_path, digest(self.scope), lambda: self.b.f.now)
        self.h = self.handoff()

    def test_forward_retirement_audited_preserves_old_anchors_and_fresh_new_window(self):
        self.forward_clock(); before = self.read(self.old)
        self.assertEqual("ClockDiscontinuity", self.old._time_rejection(before))
        self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.claim(); event = self.read(self.old)["retirement"]
        self.assertEqual(("ForwardDiscontinuity", 4_773_917), (event["clockCondition"], event["clockDriftMicros"]))
        self.old.check_successor(self.new, self.scope, self.h, H); self.complete()
        after = self.read(self.old)
        for key in ("scope", "startUtc", "startMono", "refresh", "admissions", "buildTransitions", "helperSourceRepairs"):
            self.assertEqual(before[key], after[key])
        self.new.require_predecessor_retired(H)
        self.assertEqual(0, self.new.status()["totalAdmissions"])
        self.b.f.now += 6 * 60 * 60 * 1_000_000
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            self.new.claim("pilot-admission", H, str(uuid.uuid4()))

    def test_forward_discontinuity_never_reopens_normal_old_operations(self):
        self.forward_clock()
        with self.assertRaisesRegex(Rejected, "ClockDiscontinuity"):
            self.old.claim("refresh", H, str(uuid.uuid4()))
        self.h["ExpectedPreviousHeadSha256"] = self.read(self.old)["head"]
        with self.assertRaisesRegex(Rejected, "RetirementClockOrStop"): self.claim()
        self.assertFalse(self.new.path.exists())

    def test_negative_discrepancy_or_monotonic_rollback_stays_durably_blocked(self):
        self.forward_clock(-4_773_917)
        with self.assertRaisesRegex(Rejected, "ClockRollback"): self.claim()
        self.assertEqual("ClockRollback", self.read(self.old)["stopReason"])
        self.assertFalse(self.new.path.exists())

    def test_forward_retirement_pending_and_changed_head_still_block(self):
        self.old.claim("refresh", H, str(uuid.uuid4()))
        self.forward_clock()
        with self.assertRaisesRegex(Rejected, "SuccessorPending"): self.claim()
        self.assertFalse(self.new.path.exists())

    def test_forward_clock_audit_replay_rejects_rehashed_tampering(self):
        self.forward_clock(); self.claim()
        with self.old._open() as db:
            db.execute("DROP TRIGGER immutable_events_update")  # Synthetic tamper fixture only.
            seq, body = db.execute("SELECT seq,body FROM events ORDER BY seq DESC LIMIT 1").fetchone()
            event = json.loads(body); event["clockDriftMicros"] = -4_773_917
            previous = db.execute("SELECT sha FROM events WHERE seq=?", (seq - 1,)).fetchone()[0]
            db.execute("UPDATE events SET body=?,sha=? WHERE seq=?", (canonical(event), digest({"previous": previous, "event": event}), seq))
            db.execute("CREATE TRIGGER immutable_events_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'immutable'); END")
            db.commit()
            self.old.anchor.write_bytes(b"".join(f"{s}:{pin}\n".encode() for s, pin in db.execute("SELECT seq,sha FROM events ORDER BY seq")))
        with self.assertRaisesRegex(Rejected, "InvalidRetirementHistory"): self.read(self.old)

    def test_forward_handoff_interruption_and_concurrent_claims_remain_single_use(self):
        self.forward_clock()
        def claim_once():
            try: self.claim(); return True
            except (Rejected, OSError): return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: claim_once(), range(2))))
        original = self.new._append
        def fail(db, event, previous):
            if event["type"] == "activate": raise OSError("synthetic interruption")
            return original(db, event, previous)
        with patch.object(self.new, "_append", side_effect=fail):
            with self.assertRaises(OSError): self.complete()
        self.assertTrue(self.read(self.old)["retired"])
        self.complete(); self.new.require_predecessor_retired(H)
        with self.assertRaises(Rejected): self.complete()


class SuccessorCliTests(unittest.TestCase):
    def setUp(self):
        self.b = bt.BuildCliTests(); self.b.setUp(); self.addCleanup(self.b.doCleanups)
        self.assertEqual(0, self.b.command("claim-build")[0]); self.b.swap(); self.assertEqual(0, self.b.command("complete-build")[0])
        self.old = copy.deepcopy(self.b.config); self.old_pin = self.b.pin
        self.manifest, self.proposal = plans()
        self.manifest_path = self.b.f.root / "new-plans.json"; self.manifest_path.write_text(json.dumps(self.manifest))
        self.new_folder = self.b.f.root / "successor"; self.new_folder.mkdir()
        self.new_app = self.new_folder / "candidate"; self.new_app.mkdir()
        for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"): (self.new_app / name).write_bytes(("inert successor " + name).encode())
        self.source = self.new_folder / "source"; self.source.mkdir()
        for name in ("gate_cli.py", "completion_register.py"): (self.source / name).write_bytes(Path(__file__).with_name(name).read_bytes())
        self.scope = dict(self.b.f.scope, schema=2, approvalSha256=sha(b"NEW APPROVAL"), maxAdmissions=24,
                          predecessorRegisterPath=self.old["RegisterPath"], predecessorScopeSha256=self.old["ScopeSha256"],
                          transitionId=str(uuid.uuid4()), plansSha256=digest(self.manifest),
                          registerPathSha256=path_digest(self.new_folder / "new.db"))
        self.config = dict(self.old, RegisterPath=str(self.new_folder / "new.db"), PlanManifestPath=str(self.manifest_path),
                           PlanManifestSha256=sha(self.manifest_path.read_bytes()), CandidateApplicationDirectory=str(self.new_app),
                           ScriptPath=str(self.source / "gate_cli.py"), ScriptSha256=sha((self.source / "gate_cli.py").read_bytes()),
                           ModuleSha256=sha((self.source / "completion_register.py").read_bytes()), AppSha256=sha((self.new_app / "CloudScribe.dll").read_bytes()),
                           InfrastructureSha256=sha((self.new_app / "CloudScribe.Infrastructure.dll").read_bytes()))
        self.scope["buildSha256"] = sha((self.config["AppSha256"] + "|" + self.config["InfrastructureSha256"]).encode())
        self.scope["startUtc"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat().replace("+00:00", "Z")
        self.scope["endUtc"] = (datetime.fromisoformat(self.scope["startUtc"].replace("Z", "+00:00")) + timedelta(hours=6)).isoformat().replace("+00:00", "Z")
        self.scope_path = self.new_folder / "scope.json"; self.scope_path.write_text(json.dumps(self.scope))
        self.config["ScopeSha256"] = digest(self.scope)
        self.config_path = self.new_folder / "gate.json"; self.config_path.write_text(json.dumps(self.config)); self.pin = sha(self.config_path.read_bytes())
        self.db = self.b.f.profile / "data/cloudscribe.db"; self.db.parent.mkdir()
        self.failed = uuid.uuid4().hex; self.failed_local = uuid.uuid4().hex
        self.create_database()
        old_r = Register(self.old["RegisterPath"], self.old["ScopeSha256"])
        with old_r._open() as db: head = old_r._read(db)["head"]
        self.h = {"SchemaVersion": 1, "TransitionId": self.scope["transitionId"], "SourceApprovalSha256": self.scope["approvalSha256"],
                  "PreviousConfigPath": str(self.b.config_path), "PreviousGateSha256": self.old_pin, "PreviousScopeSha256": self.old["ScopeSha256"],
                  "ExpectedPreviousHeadSha256": head, "NewConfigPath": str(self.config_path), "NewGateSha256": self.pin,
                  "NewScopePath": str(self.scope_path), "NewScopeSha256": self.config["ScopeSha256"], "DatabaseSha256": sha(self.db.read_bytes()),
                  "FailedAuthorizationId": self.failed, "FailedLocalAttemptId": self.failed_local, "FailedPlanSha256": H,
                  "FailedCoverageOrdinal": 3, "FailedPayloadSha256": H, "FailedPayloadBytes": 100}
        self.h_path = self.new_folder / "handoff.json"; self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())

    def create_database(self):
        with sqlite3.connect(self.db) as db:
            db.executescript("""
                CREATE TABLE chirp_article_attempts(AuthorizationId TEXT,PlanSha256 TEXT,CoverageOrdinal INTEGER,
                  PolicySha256 TEXT,SourceApprovalSha256 TEXT,AccountId TEXT,ProjectId TEXT,ChildKey TEXT,ReviewSha256 TEXT,
                  EnvelopeJson TEXT,Currency TEXT,Scale INTEGER,EstimateMinorUnits INTEGER,ReservedMinorUnits INTEGER,
                  ApprovedFinancialSha256 TEXT,ApprovedAtUnixMilliseconds INTEGER,Consumed INTEGER,ClaimedLocalAttemptId TEXT);
                CREATE TABLE chirp_article_successor_attempts AS SELECT *, CAST(NULL AS TEXT) AS RootAuthorizationId,
                  CAST(NULL AS TEXT) AS ParentAuthorizationId, CAST(NULL AS TEXT) AS ParentStateSha256,
                  CAST(NULL AS TEXT) AS GrantSha256, CAST(NULL AS TEXT) AS GrantId FROM chirp_article_attempts WHERE 0;
                CREATE VIEW chirp_article_owned_attempts AS SELECT * FROM chirp_article_attempts UNION ALL
                  SELECT AuthorizationId,PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
                  ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,
                  ApprovedFinancialSha256,ApprovedAtUnixMilliseconds,Consumed,ClaimedLocalAttemptId FROM chirp_article_successor_attempts;
                CREATE TABLE google_generation_queue_states(AccountId TEXT,OperationStableId TEXT,IdempotencyKey TEXT,
                  SubmissionAttempted INTEGER,UnresolvedSubmission INTEGER,ProviderRequestId TEXT,DiagnosticCode TEXT,ErrorStatus INTEGER,
                  LocalAttemptId TEXT,PayloadSha256 TEXT,PayloadBytes INTEGER);
                CREATE TABLE chirp_article_receipts(PlanSha256 TEXT,CoverageOrdinal INTEGER,AuthorizationId TEXT,ReceiptJson TEXT,ReceiptSha256 TEXT);
                CREATE TABLE chirp_article_output_journals(AuthorizationId TEXT,ReceiptJson TEXT,ReceiptSha256 TEXT);
                CREATE TABLE chirp_article_plans(PlanSha256 TEXT,ProposalJson TEXT,AccountId TEXT,ProjectId TEXT);
                CREATE TABLE chirp_reserved_catalog_mappings(AuthorizationId TEXT,MappingId TEXT,PolicyJson TEXT,PolicySha256 TEXT,CreatedAtUnixMilliseconds INTEGER,PriorMappingSha256 TEXT);
                CREATE TABLE chirp_reserved_dispatch_mappings(AuthorizationId TEXT,MappingId TEXT,PolicySha256 TEXT,ClaimedLocalAttemptId TEXT,ClaimedAtUnixMilliseconds INTEGER);
            """)
            db.execute("INSERT INTO chirp_article_attempts(AuthorizationId,PlanSha256,CoverageOrdinal,AccountId,ProjectId,Consumed,ClaimedLocalAttemptId,ChildKey,PolicySha256,SourceApprovalSha256,ReservedMinorUnits,EnvelopeJson) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", (self.failed, H, 3, self.config["AccountId"], self.config["ProjectId"], 1, self.failed_local, "failed-key", H, H, 1, "{}"))
            db.execute("INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)", (self.config["AccountId"], "synthesize-speech", "failed-key", 1, 0, None, "google-http-400", 1, self.failed_local, H, 100))

    def command(self, command, policy=None, operation=None, receipt=None, config_path=None):
        successor = command.endswith("successor")
        selected_config = config_path or (self.b.f.app.parent / "completion-workflow-gate.json" if command == "complete-successor" else self.config_path)
        req = {"command": command, "configPath": str(selected_config), "configSha256": self.pin,
               "appDirectory": str(self.new_app if successor and command != "complete-successor" else self.b.f.app),
               "id": self.h["TransitionId"] if successor else operation or str(uuid.uuid4()),
               "identitySha256": self.h_pin if successor else sha(policy.read_bytes()) if policy else H,
               "handoffPath": str(self.h_path), "handoffSha256": self.h_pin, "receipt": receipt, "policyPath": str(policy) if policy else None}
        p = subprocess.run([sys.executable, "-I", "-S", str(self.source / "gate_cli.py")], input=json.dumps(req), text=True, capture_output=True, timeout=20)
        self.assertEqual("", p.stderr); return p.returncode, json.loads(p.stdout)

    def activate(self):
        inspected = self.command("inspect-successor"); self.assertEqual(0, inspected[0], inspected)
        claimed = self.command("claim-successor"); self.assertEqual(0, claimed[0], claimed)
        self.assertEqual(0, self.command("check-successor")[0])
        for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"): (self.b.f.app / name).write_bytes((self.new_app / name).read_bytes())
        self.b.write_installed(self.config, self.pin, self.config_path)
        self.assertEqual(0, self.command("complete-successor")[0])

    def policy(self, pilot=True):
        maximum, baseline = (1, (153, 23)) if pilot else (125, (154, 24))
        allowed = [1] if pilot else list(range(2, 127))
        value = {"SchemaVersion": 2, "AppSha256": self.config["AppSha256"], "InfrastructureSha256": self.config["InfrastructureSha256"],
                 "ProfileRootSha256": self.config["ProfileRootSha256"], "SourceApprovalSha256": self.scope["approvalSha256"],
                 "AccountId": self.config["AccountId"], "ProjectId": self.config["ProjectId"], "Currency": "USD", "Scale": 2,
                 "ExpiresAtUtc": self.scope["endUtc"], "MaximumNewAttempts": maximum, "MaximumAdditionalReservationMinorUnits": maximum,
                 "Budget": {"ProjectCommittedMinorUnits": baseline[0], "AllocationCommittedMinorUnits": baseline[1], "ProjectCeilingMinorUnits": 500, "AllocationCeilingMinorUnits": 149},
                 "Articles": [{"Proposal": self.proposal, "AllowedSegments": [{"CoverageOrdinal": x, "MaximumMinorUnits": 1} for x in allowed]}]}
        path = self.new_folder / ("pilot.json" if pilot else "completion.json"); path.write_text(json.dumps(value)); return path

    def admit_pilot(self):
        pilot = self.policy(); operation = str(uuid.uuid4())
        result = self.command("claim-completion", pilot, operation); self.assertEqual(0, result[0], result)
        (self.b.f.app.parent / "chirp-article-admission-policy.json").write_bytes(pilot.read_bytes())
        marker_path = self.b.f.app.parent / "cloudscribe-install.json"; marker = json.loads(marker_path.read_text())
        marker["ChirpArticlePolicySha256"] = sha(pilot.read_bytes()); marker_path.write_text(json.dumps(marker))
        result = self.command("complete", pilot, operation, {"outcome": "Succeeded"}); self.assertEqual(0, result[0], result)
        return pilot

    def accept_pilot(self):
        pilot = self.admit_pilot(); auth = uuid.uuid4().hex; local = uuid.uuid4().hex; key = "pilot-key"
        part = self.proposal["Parts"][0]; media = self.b.f.profile / "accepted-chirp-article-audio" / (auth + ".mp3")
        media.parent.mkdir(); media.write_bytes(b"inert synthetic audio bytes, never decoded")
        envelope = {"AccountId": self.config["AccountId"], "CredentialReferenceId": self.config["CredentialReferenceId"],
                    "VoiceName": self.config["VoiceName"], "AudioEncoding": "MP3", "RecoveryAuthorizationId": None,
                    "ChirpAuthorizationId": str(uuid.UUID(auth)), "CompiledPayloadSha256": part["PayloadSha256"], "CompiledPayloadBytes": part["PayloadBytes"], "RequestRevision": 1}
        audio = {"DocumentId": self.proposal["DocumentId"], "RevisionId": self.proposal["RevisionId"], "TextSha256": part["Range"]["TextSha256"],
                 "AccountId": self.config["AccountId"], "OperationId": "synthesize-speech", "IdempotencyKey": key, "LocalAttemptId": local,
                 "PayloadSha256": part["PayloadSha256"], "AudioPath": str(media), "AudioBytes": media.stat().st_size, "AudioSha256": sha(media.read_bytes()),
                 "VoiceName": self.config["VoiceName"], "LanguageCode": self.config["LanguageCode"]}
        receipt = {"PlanSha256": self.proposal["PlanSha256"], "CoverageOrdinal": 1, "Range": part["Range"], "Audio": audio, "ExecutionMappingSha256": None}
        raw = json.dumps(receipt)
        with sqlite3.connect(self.db) as db:
            db.execute("INSERT INTO chirp_article_attempts(AuthorizationId,PlanSha256,CoverageOrdinal,AccountId,ProjectId,Consumed,ClaimedLocalAttemptId,ChildKey,PolicySha256,SourceApprovalSha256,ReservedMinorUnits,EnvelopeJson) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", (auth, self.proposal["PlanSha256"], 1, self.config["AccountId"], self.config["ProjectId"], 1, local, key, sha(pilot.read_bytes()), self.scope["approvalSha256"], 1, json.dumps(envelope)))
            db.execute("INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)", (self.config["AccountId"], "synthesize-speech", key, 1, 0, None, "google-accepted", 0, local, part["PayloadSha256"], part["PayloadBytes"]))
            db.execute("INSERT INTO chirp_article_plans VALUES(?,?,?,?)", (self.proposal["PlanSha256"], json.dumps(self.proposal), self.config["AccountId"], self.config["ProjectId"]))
            db.execute("INSERT INTO chirp_article_receipts VALUES(?,?,?,?,?)", (self.proposal["PlanSha256"], 1, auth, raw, sha(raw.encode())))
            db.execute("INSERT INTO chirp_article_output_journals VALUES(?,?,?)", (auth, raw, sha(raw.encode())))
        return auth, media

    def test_real_cli_handoff_and_actual_pilot_evidence_gate(self):
        self.activate()
        completion = self.policy(False)
        self.assertEqual(1, self.command("claim-completion", completion)[0])
        self.accept_pilot()
        result = self.command("claim-completion", completion); self.assertEqual(0, result[0], result)
        self.assertEqual("completion-admission", result[1]["kind"])
        self.assertEqual(1, self.b.command("status", appDirectory=str(self.b.f.app))[0])

    def test_completion_requires_installed_config_and_retains_private_pin(self):
        self.assertEqual(0, self.command("claim-successor")[0])
        for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"):
            (self.b.f.app / name).write_bytes((self.new_app / name).read_bytes())
        self.b.write_installed(self.config, self.pin, self.config_path)
        result = self.command("complete-successor", config_path=self.config_path)
        self.assertEqual("HandoffConfigPin", result[1]["code"])
        original = self.config_path.read_bytes(); self.config_path.write_bytes(original + b" ")
        result = self.command("complete-successor")
        self.assertEqual("HandoffConfigPin", result[1]["code"])
        self.config_path.write_bytes(original)
        self.assertEqual(0, self.command("complete-successor")[0])

    def test_real_cli_forward_old_clock_handoff_and_installed_readback(self):
        # Synthetic history only: valid, fully hashed timestamps produce the
        # actual CLI clock discrepancy. No runtime override or real register.
        old = Register(self.old["RegisterPath"], self.old["ScopeSha256"])
        with old._open() as db:
            db.execute("DROP TRIGGER immutable_events_update")  # Construct synthetic clock history, never a live DB.
            rows = db.execute("SELECT seq,body FROM events ORDER BY seq").fetchall()
            previous = old.scope_sha
            for seq, body in rows:
                event = json.loads(body); event["utc"] -= 4_773_917
                if event["type"] in ("build-claim", "source-claim"):
                    event["transition"]["ExpectedHeadSha256"] = previous
                current = digest({"previous": previous, "event": event})
                db.execute("UPDATE events SET body=?,sha=? WHERE seq=?", (canonical(event), current, seq))
                previous = current
            db.execute("CREATE TRIGGER immutable_events_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'immutable'); END")
            db.commit()
            old.anchor.write_bytes(b"".join(f"{s}:{pin}\n".encode() for s, pin in db.execute("SELECT seq,sha FROM events ORDER BY seq")))
        self.h["ExpectedPreviousHeadSha256"] = previous
        self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        self.activate()
        with old._open() as db:
            state = old._read(db)
        self.assertTrue(state["retired"])
        self.assertEqual("ForwardDiscontinuity", state["retirement"]["clockCondition"])
        self.assertGreater(state["retirement"]["clockDriftMicros"], 250_000)
        self.assertEqual((0, 1, 0), (state["refresh"], state["buildTransitions"], state["admissions"]))
        self.assertEqual(0, self.command("status")[0])

    def test_pilot_admission_alone_or_caller_success_does_not_unlock_completion(self):
        self.activate(); self.admit_pilot()
        self.assertEqual(1, self.command("claim-completion", self.policy(False), receipt={"pilotAccepted": True})[0])

    def test_missing_journal_media_tamper_and_wrong_queue_block_completion(self):
        self.activate(); auth, media = self.accept_pilot(); completion = self.policy(False)
        original = media.read_bytes(); media.write_bytes(b"wrong")
        self.assertEqual(1, self.command("claim-completion", completion)[0]); media.write_bytes(original)
        with sqlite3.connect(self.db) as db: db.execute("DELETE FROM chirp_article_output_journals WHERE AuthorizationId=?", (auth,))
        self.assertEqual(1, self.command("claim-completion", completion)[0])

    def test_changed_or_uncertain_terminal_failure_rejects_before_retirement(self):
        with sqlite3.connect(self.db) as db: db.execute("UPDATE google_generation_queue_states SET UnresolvedSubmission=1")
        self.assertEqual(1, self.command("claim-successor")[0])
        self.assertEqual(0, self.b.command("status", appDirectory=str(self.b.f.app))[0])

    def test_handoff_config_profile_or_manifest_mismatch_rejected(self):
        self.config["CredentialReferenceId"] = "other-reference"; self.config_path.write_text(json.dumps(self.config)); self.pin = sha(self.config_path.read_bytes())
        self.h["NewGateSha256"] = self.pin; self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        result = self.command("inspect-successor")
        self.assertEqual(1, result[0]); self.assertEqual("SuccessorScopeMismatch", result[1]["reason"])

    def test_safe_specific_head_database_and_missing_pilot_reasons(self):
        original = self.h["ExpectedPreviousHeadSha256"]
        self.h["ExpectedPreviousHeadSha256"] = sha(b"wrong-head")
        self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        result = self.command("inspect-successor")
        self.assertEqual((1, "SuccessorHeadMismatch"), (result[0], result[1]["reason"]))
        self.assertEqual("SuccessorHeadMismatch", result[1]["code"])
        self.h["ExpectedPreviousHeadSha256"] = original
        self.h["DatabaseSha256"] = sha(b"wrong-database")
        self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        result = self.command("inspect-successor")
        self.assertEqual((1, "SuccessorDatabaseMismatch"), (result[0], result[1]["reason"]))
        self.assertEqual("TerminalDatabasePin", result[1]["code"])
        self.h["DatabaseSha256"] = sha(self.db.read_bytes())
        self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        self.activate()
        result = self.command("claim-completion", self.policy(False))
        self.assertEqual((1, "PilotEvidenceMissing"), (result[0], result[1]["reason"]))
        self.assertEqual("PilotNotAccepted", result[1]["code"])

    def test_expired_successor_has_specific_reason_and_no_effect(self):
        end = datetime.now(timezone.utc) - timedelta(seconds=1)
        self.scope["startUtc"] = (end - timedelta(hours=6)).isoformat().replace("+00:00", "Z")
        self.scope["endUtc"] = end.isoformat().replace("+00:00", "Z")
        self.scope_path.write_text(json.dumps(self.scope)); self.config["ScopeSha256"] = digest(self.scope)
        self.config_path.write_text(json.dumps(self.config)); self.pin = sha(self.config_path.read_bytes())
        self.h.update(NewScopeSha256=self.config["ScopeSha256"], NewGateSha256=self.pin)
        self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())
        result = self.command("claim-successor")
        self.assertEqual((1, "Expired"), (result[0], result[1]["reason"]))
        self.assertEqual("OutsideWindow", result[1]["code"])
        self.assertFalse(Path(self.config["RegisterPath"]).exists())

    def test_completion_cannot_include_pilot_or_increase_budget(self):
        completion = json.loads(self.policy(False).read_text())
        completion["Articles"][0]["AllowedSegments"].append({"CoverageOrdinal": 1, "MaximumMinorUnits": 1})
        with self.assertRaises(Rejected): verify_plan_policy(self.manifest, self.scope, completion, "completion-admission")
        pilot = json.loads(self.policy().read_text()); pilot["MaximumNewAttempts"] = 2
        with self.assertRaises(Rejected): verify_plan_policy(self.manifest, self.scope, pilot, "pilot-admission")

    def test_real_pilot_mapping_chain_equal_timestamps_and_dispatch_mismatch(self):
        self.activate(); auth, _ = self.accept_pilot()
        pilot = json.loads((self.new_folder / "pilot.json").read_text())
        created = int(datetime.now(timezone.utc).timestamp() * 1000) - 1000
        previous = None
        with sqlite3.connect(self.db) as db:
            db.row_factory = sqlite3.Row
            a = db.execute("SELECT * FROM chirp_article_owned_attempts WHERE AuthorizationId=?", (auth,)).fetchone()
            envelope = json.loads(a["EnvelopeJson"])
            for index in range(2):
                mapping = uuid.uuid4().hex
                value = {"SchemaVersion": 1, "MappingId": str(uuid.UUID(mapping)), "AuthorizationId": str(uuid.UUID(auth)),
                         "OriginalEnvelope": envelope, "ExecutionEnvelope": dict(envelope, RequestRevision=2 + index),
                         "HistoricalPolicy": pilot, "SourceApprovalSha256": sha(("approval" + str(index)).encode()),
                         "ProfileRootSha256": self.config["ProfileRootSha256"], "AppSha256": self.config["AppSha256"],
                         "InfrastructureSha256": self.config["InfrastructureSha256"], "MaximumExistingDispatches": 1,
                         "MaximumNewReservations": 0, "MaximumAdditionalReservationMinorUnits": 0,
                         "ExpiresAtUtc": self.scope["endUtc"], "CatalogExpiresAtUtc": self.scope["endUtc"]}
                if previous: value["PriorMappingSha256"] = previous
                raw = json.dumps(value); pin = sha(raw.encode())
                db.execute("INSERT INTO chirp_reserved_catalog_mappings VALUES(?,?,?,?,?,?)", (auth, mapping, raw, pin, created, previous)); previous = pin
            db.execute("INSERT INTO chirp_reserved_dispatch_mappings VALUES(?,?,?,?,?)", (auth, mapping, pin, a["ClaimedLocalAttemptId"], created + 1))
            rr = db.execute("SELECT ReceiptJson FROM chirp_article_receipts WHERE AuthorizationId=?", (auth,)).fetchone()
            receipt = json.loads(rr[0]); receipt["ExecutionMappingSha256"] = pin
            raw = json.dumps(receipt); pin = sha(raw.encode())
            db.execute("UPDATE chirp_article_receipts SET ReceiptJson=?,ReceiptSha256=? WHERE AuthorizationId=?", (raw, pin, auth))
            db.execute("UPDATE chirp_article_output_journals SET ReceiptJson=?,ReceiptSha256=? WHERE AuthorizationId=?", (raw, pin, auth))
        with sqlite3.connect(self.db) as db:
            db.execute("UPDATE chirp_reserved_dispatch_mappings SET ClaimedLocalAttemptId=?", (uuid.uuid4().hex,))
        rejected = self.command("claim-completion", self.policy(False))
        self.assertEqual((1, "PilotEvidenceMismatch"), (rejected[0], rejected[1]["reason"]))
        with sqlite3.connect(self.db) as db:
            db.execute("UPDATE chirp_reserved_dispatch_mappings SET ClaimedLocalAttemptId=?", (a["ClaimedLocalAttemptId"],))
        result = self.command("claim-completion", self.policy(False)); self.assertEqual(0, result[0], result)


if __name__ == "__main__":
    unittest.main()
