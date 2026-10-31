"""Synthetic build succession: inert bytes only, no app/provider/credential access."""
import concurrent.futures
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

import test_completion_register as register_fixtures
import test_gate_cli as cli_fixtures
from completion_register import Register, Rejected, digest


H = cli_fixtures.sha(b"synthetic transition identity")
NEW = cli_fixtures.sha(b"synthetic new build")
OLD_GATE = cli_fixtures.sha(b"synthetic predecessor config")
NEW_GATE = cli_fixtures.sha(b"synthetic successor config")


class BuildRegisterTests(unittest.TestCase):
    def setUp(self):
        self.f = register_fixtures.RegisterTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.r = self.f.r
        self.t = self.transition()

    def state(self):
        with self.r._open() as db:
            return self.r._read(db)

    def transition(self):
        state = self.state()
        return {"SchemaVersion": 1, "TransitionId": str(uuid.uuid4()), "SourceApprovalSha256": state["scope"]["approvalSha256"],
                "ScopeSha256": self.r.scope_sha, "ExpectedHeadSha256": state["head"],
                "PreviousConfigPath": str(self.f.path.parent / "old-config.json"), "PreviousGateSha256": OLD_GATE,
                "NewGateSha256": NEW_GATE, "PreviousBuildSha256": state["currentBuild"], "NewBuildSha256": NEW}

    def claim(self):
        return self.r.claim_build(self.t, H, self.t["TransitionId"])

    def complete(self):
        return self.r.complete_build(self.t, H, self.t["TransitionId"], H)

    def test_counts_window_and_original_scope_preserved(self):
        claim = self.f.claim()
        self.r.complete(claim["id"], self.f.receipt(claim))
        self.t = self.transition()
        before = self.state()
        self.claim()
        self.r.check_build(self.t, H, self.t["TransitionId"])
        self.complete()
        after = self.state()
        for field in ("scope", "startUtc", "startMono", "refresh", "resume-admission", "completion-admission"):
            self.assertEqual(before[field], after[field])
        self.assertEqual(NEW, after["currentBuild"])
        self.assertEqual(NEW_GATE, after["currentGate"])
        self.assertEqual(1, after["buildTransitions"])

    def test_inspect_is_readonly_and_expected_head_compare_precedes_append(self):
        before = self.f.path.read_bytes(), self.r.anchor.read_bytes()
        self.r.inspect_build(self.t, H, self.t["TransitionId"])
        self.assertEqual(before, (self.f.path.read_bytes(), self.r.anchor.read_bytes()))
        self.t["ExpectedHeadSha256"] = H
        with self.assertRaises(Rejected):
            self.claim()
        self.assertEqual(before, (self.f.path.read_bytes(), self.r.anchor.read_bytes()))

    def test_only_one_successor_and_no_claim_replay(self):
        self.claim()
        with self.assertRaises(Rejected):
            self.claim()
        self.complete()
        with self.assertRaises(Rejected):
            self.complete()
        self.t = self.transition()
        self.t["NewBuildSha256"] = H
        with self.assertRaises(Rejected):
            self.claim()

    def test_pending_blocks_normal_claim_check_and_completion(self):
        self.claim()
        with self.assertRaises(Rejected):
            self.f.claim()
        with self.assertRaises(Rejected):
            self.r.require_pending_before_effect(self.t["TransitionId"], H)
        receipt = {"id": self.t["TransitionId"], "identitySha256": H, "outcome": "Succeeded", "evidenceSha256": H}
        with self.assertRaises(Rejected):
            self.r.complete(self.t["TransitionId"], receipt)

    def test_existing_pending_refresh_and_prior_financial_admission_reject(self):
        c = self.f.claim()
        self.t = self.transition()
        with self.assertRaises(Rejected):
            self.claim()
        self.r.complete(c["id"], self.f.receipt(c))
        c = self.f.claim("resume-admission")
        self.r.complete(c["id"], self.f.receipt(c))
        self.t = self.transition()
        with self.assertRaisesRegex(Rejected, "FinancialAdmission"):
            self.claim()

    def test_stale_normal_client_cannot_claim_after_transition(self):
        old = Register(self.f.path, self.r.scope_sha, lambda: self.f.now, expected_build=self.f.scope["buildSha256"], expected_gate=OLD_GATE)
        old.status()
        self.t = self.transition()
        self.claim()
        self.complete()
        with self.assertRaisesRegex(Rejected, "CurrentBuildBinding"):
            old.claim("refresh", H, str(uuid.uuid4()))
        new = Register(self.f.path, self.r.scope_sha, lambda: self.f.now, expected_build=NEW, expected_gate=NEW_GATE)
        self.assertEqual(1, new.status()["buildTransitions"])

    def test_restart_resumes_only_exact_pending_transition(self):
        self.claim()
        restarted = Register(self.f.path, self.r.scope_sha, lambda: self.f.now)
        wrong = dict(self.t, TransitionId=str(uuid.uuid4()))
        with self.assertRaises(Rejected):
            restarted.check_build(wrong, H, wrong["TransitionId"])
        restarted.complete_build(self.t, H, self.t["TransitionId"], H)
        self.assertEqual(NEW, self.state()["currentBuild"])

    def test_clock_rollback_durably_stops_pending_and_retains_history(self):
        self.claim()
        self.f.now -= 1
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            self.r.check_build(self.t, H, self.t["TransitionId"])
        self.f.now += 10
        with self.assertRaises(Rejected):
            self.complete()
        self.assertIsNotNone(self.state()["pending"])
        self.assertTrue(self.state()["stopped"])

    def test_anchor_failure_no_permit_and_no_recovery_by_repeat(self):
        with patch.object(self.r, "_seal", side_effect=OSError("synthetic disk full")):
            with self.assertRaises(OSError):
                self.claim()
        with self.assertRaisesRegex(Rejected, "AnchorDatabaseMismatch"):
            self.r.status()

    def test_expiry_stops_claim_and_does_not_extend_deadline(self):
        self.f.now = register_fixtures.utc(self.f.scope["endUtc"])
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            self.claim()
        self.assertTrue(self.state()["stopped"])
        self.assertEqual(0, self.state()["buildTransitions"])
        self.f.now -= 100
        with self.assertRaises(Rejected):
            self.claim()

    def test_pending_transition_expiry_cannot_be_completed_or_renewed(self):
        self.claim()
        self.f.now = register_fixtures.utc(self.f.scope["endUtc"])
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            self.complete()
        self.assertIsNotNone(self.state()["pending"])
        self.assertEqual(self.f.scope["buildSha256"], self.state()["currentBuild"])

    def test_approval_schema_and_tamper_rejected(self):
        for field, value in (("SourceApprovalSha256", NEW), ("ScopeSha256", NEW), ("SchemaVersion", True), ("PreviousBuildSha256", NEW)):
            with self.subTest(field=field):
                bad = dict(self.t, **{field: value})
                with self.assertRaises(Rejected):
                    self.r.claim_build(bad, H, bad["TransitionId"])

    def test_concurrent_claims_issue_exactly_one_permit(self):
        def attempt():
            try:
                self.claim()
                return True
            except (Rejected, OSError):
                return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: attempt(), range(2))))
        self.assertEqual(1, self.state()["buildTransitions"])


class BuildCliTests(unittest.TestCase):
    def setUp(self):
        self.f = cli_fixtures.CliTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.old = copy.deepcopy(self.f.config)
        self.old_path = self.f.config_path
        self.old_pin = self.f.pin
        self.candidate = self.f.root / "candidate"
        self.candidate.mkdir()
        for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"):
            (self.candidate / name).write_bytes(("new inert " + name).encode())
        self.new_source = self.f.root / "new-source"
        self.new_source.mkdir()
        for name in ("gate_cli.py", "completion_register.py"):
            (self.new_source / name).write_bytes(Path(__file__).with_name(name).read_bytes())
        self.config = dict(self.old, CandidateApplicationDirectory=str(self.candidate),
                           AppSha256=cli_fixtures.sha((self.candidate / "CloudScribe.dll").read_bytes()),
                           InfrastructureSha256=cli_fixtures.sha((self.candidate / "CloudScribe.Infrastructure.dll").read_bytes()),
                           ScriptPath=str(self.new_source / "gate_cli.py"),
                           ScriptSha256=cli_fixtures.sha((self.new_source / "gate_cli.py").read_bytes()),
                           ModuleSha256=cli_fixtures.sha((self.new_source / "completion_register.py").read_bytes()))
        self.config_path = self.f.root / "new-config.json"
        self.config_path.write_text(json.dumps(self.config))
        self.pin = cli_fixtures.sha(self.config_path.read_bytes())
        self.write_installed(self.old, self.old_pin, self.old_path)
        r = Register(self.f.db, self.old["ScopeSha256"])
        with r._open() as db:
            head = r._read(db)["head"]
        bd = lambda c: cli_fixtures.sha((c["AppSha256"] + "|" + c["InfrastructureSha256"]).encode())
        self.t = {"SchemaVersion": 1, "TransitionId": str(uuid.uuid4()), "SourceApprovalSha256": self.f.scope["approvalSha256"],
                  "ScopeSha256": self.old["ScopeSha256"], "ExpectedHeadSha256": head, "PreviousConfigPath": str(self.old_path),
                  "PreviousGateSha256": self.old_pin, "NewGateSha256": self.pin,
                  "PreviousBuildSha256": bd(self.old), "NewBuildSha256": bd(self.config)}
        self.tp = self.f.root / "transition.json"
        self.save_transition()

    def save_transition(self):
        self.tp.write_text(json.dumps(self.t))
        self.transition_pin = cli_fixtures.sha(self.tp.read_bytes())

    def write_installed(self, config, pin, config_path):
        marker = {"Product": "CloudScribe Pro", "CompletionGateProtocolVersion": 1, "Profile": str(self.f.profile),
                  "CompletionWorkflowGateSha256": pin, "AppSha256": config["AppSha256"]}
        (self.f.app.parent / "cloudscribe-install.json").write_text(json.dumps(marker))
        (self.f.app.parent / "completion-workflow-gate.json").write_bytes(config_path.read_bytes())
        (self.f.profile / ".completion-workflow-required.json").write_text(json.dumps({"GateSha256": pin, "ScopeSha256": config["ScopeSha256"]}))

    def command(self, command, **overrides):
        req = {"command": command, "configPath": str(self.config_path), "configSha256": self.pin,
               "appDirectory": str(self.f.app if command == "complete-build" else self.candidate),
               "id": self.t["TransitionId"], "identitySha256": self.transition_pin,
               "transitionPath": str(self.tp), "transitionSha256": self.transition_pin}
        req.update(overrides)
        p = subprocess.run([sys.executable, "-I", "-S", str(self.new_source / "gate_cli.py")],
                           input=json.dumps(req), text=True, capture_output=True, timeout=20)
        self.assertEqual("", p.stderr)
        return p.returncode, json.loads(p.stdout)

    def swap(self):
        for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"):
            (self.f.app / name).write_bytes((self.candidate / name).read_bytes())
        self.write_installed(self.config, self.pin, self.config_path)

    def test_real_cli_inert_build_readback_and_normal_new_old_gate(self):
        self.assertEqual(0, self.command("inspect-build")[0])
        self.assertEqual(0, self.command("claim-build")[0])
        self.assertEqual((0, {"valid": True}), self.command("check-build"))
        # Caller assertions cannot complete before the files and pins really change.
        self.assertEqual(1, self.command("complete-build", receipt={"outcome": "Succeeded"})[0])
        self.swap()
        self.assertEqual("Succeeded", self.command("complete-build")[1]["outcome"])
        self.assertEqual(0, self.command("status", appDirectory=str(self.f.app))[0])
        self.assertEqual(1, self.f.command("status")[0])
        self.assertEqual(1, self.command("claim-build")[0])

    def test_wrong_installed_requirement_prevents_claim(self):
        (self.f.profile / ".completion-workflow-required.json").write_text("{}")
        self.assertEqual(1, self.command("claim-build")[0])
        self.assertEqual(0, Register(self.f.db, self.old["ScopeSha256"]).status()["buildTransitions"])

    def test_changed_config_binding_and_transition_pin_fail(self):
        self.assertEqual(1, self.command("claim-build", transitionSha256=H)[0])
        self.config["CredentialReferenceId"] = "different-synthetic-reference"
        self.config_path.write_text(json.dumps(self.config))
        self.pin = cli_fixtures.sha(self.config_path.read_bytes())
        self.t["NewGateSha256"] = self.pin
        self.save_transition()
        self.assertEqual(1, self.command("claim-build")[0])

    def test_missing_or_tampered_predecessor_source_fails(self):
        self.f.module.write_bytes(b"not the predecessor source")
        self.assertEqual(1, self.command("inspect-build")[0])

    def test_financial_marker_refused_before_transition(self):
        marker_path = self.f.app.parent / "cloudscribe-install.json"
        marker = json.loads(marker_path.read_text())
        marker["ChirpArticlePolicySha256"] = H
        marker_path.write_text(json.dumps(marker))
        self.assertEqual(1, self.command("claim-build")[0])

    def test_interrupted_swap_rejects_normal_operations_until_exact_complete(self):
        self.assertEqual(0, self.command("claim-build")[0])
        self.swap()
        self.assertEqual(1, self.command("claim-refresh", appDirectory=str(self.f.app))[0])
        self.assertEqual(0, self.command("complete-build")[0])
        self.assertEqual(0, self.command("status", appDirectory=str(self.f.app))[0])

    def test_partial_requirement_update_cannot_complete(self):
        self.assertEqual(0, self.command("claim-build")[0])
        self.swap()
        (self.f.profile / ".completion-workflow-required.json").write_text(json.dumps({"GateSha256": self.old_pin, "ScopeSha256": self.old["ScopeSha256"]}))
        self.assertEqual(1, self.command("complete-build")[0])
        r = Register(self.f.db, self.old["ScopeSha256"])
        with r._open() as db:
            state = r._read(db)
        self.assertIsNotNone(state["pending"])
        self.assertEqual(self.t["PreviousBuildSha256"], state["currentBuild"])

    def test_fresh_standalone_fixture_generator_contract(self):
        fresh = self.f.root / "generated-build-fixture"
        p = subprocess.run([sys.executable, "-I", "-S", str(Path(__file__).with_name("make_synthetic_build_fixture.py")), str(fresh)],
                           text=True, capture_output=True, timeout=30)
        self.assertEqual(0, p.returncode, p.stderr)
        output = json.loads(p.stdout)
        for key in ("package", "target", "profile", "gatePath", "gateSha256", "scopeSha256", "transitionPath",
                    "transitionSha256", "transitionId", "previousGateSha256", "previousConfigPath"):
            self.assertIn(key, output)
        config = json.loads(Path(output["gatePath"]).read_text())
        register = Register(config["RegisterPath"], config["ScopeSha256"])
        with register._open() as db:
            before = register._read(db)
        self.assertEqual(1, before["refresh"])
        self.assertIsNone(before["pending"])
        transition = json.loads(Path(output["transitionPath"]).read_text())
        self.assertEqual(before["head"], transition["ExpectedHeadSha256"])


if __name__ == "__main__":
    unittest.main()
