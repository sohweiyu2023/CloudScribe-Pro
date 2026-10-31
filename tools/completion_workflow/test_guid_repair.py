"""Strict identity correction and one-time source-only repair; all fixtures inert."""
import concurrent.futures
import copy
import json
from pathlib import Path
import subprocess
import sys
import unittest
import uuid
from unittest.mock import patch

import test_build_transition as build_fixtures
from completion_register import Register, Rejected, digest, uuid_identity, verify_plan_policy
from fixture_data import make_plan_fixture


H = build_fixtures.H
SOURCE_GATE = build_fixtures.cli_fixtures.sha(b"synthetic source repair gate")


class GuidIdentityTests(unittest.TestCase):
    def setUp(self):
        self.manifest, self.resume, _ = make_plan_fixture(digest)
        synthetic = uuid.UUID("a12bcdef-1234-5678-9abc-def012345678")
        self.manifest["heldAuthorizationId"] = synthetic.hex
        self.resume["AuthorizationId"] = str(synthetic)
        self.scope = {"plansSha256": digest(self.manifest), "endUtc": "2099-01-01T00:00:00Z"}

    def test_actual_manifest_n_and_csharp_policy_d_contract(self):
        # Same serialization forms as the real private manifest and C# output,
        # synthetic UUID/payload only; no private artifact is bundled.
        identity = uuid.UUID(self.manifest["heldAuthorizationId"])
        self.assertEqual(identity.hex, self.manifest["heldAuthorizationId"])
        for manifest_value in (identity.hex, identity.hex.upper(), str(identity), str(identity).upper()):
            for policy_value in (identity.hex, identity.hex.upper(), str(identity), str(identity).upper()):
                with self.subTest(manifest=manifest_value, policy=policy_value):
                    manifest = dict(self.manifest, heldAuthorizationId=manifest_value)
                    scope = dict(self.scope, plansSha256=digest(manifest))
                    policy = dict(self.resume, AuthorizationId=policy_value)
                    verify_plan_policy(manifest, scope, policy, "resume-admission")

    def test_invalid_nil_mismatched_and_coerced_values_rejected(self):
        valid = self.manifest["heldAuthorizationId"]
        invalid = [None, 1, True, uuid.UUID(valid), "", "0" * 32, str(uuid.UUID(int=0)),
                   " " + valid, "{" + valid + "}", "urn:uuid:" + str(uuid.UUID(valid)), valid[:-1], valid + "g"]
        for value in invalid:
            with self.subTest(value=str(value)):
                with self.assertRaises(Rejected):
                    uuid_identity(value)
                with self.assertRaises(Rejected):
                    verify_plan_policy(self.manifest, self.scope, dict(self.resume, AuthorizationId=value), "resume-admission")
        with self.assertRaisesRegex(Rejected, "OriginalHeldIdentity"):
            verify_plan_policy(self.manifest, self.scope, dict(self.resume, AuthorizationId=str(uuid.uuid4())), "resume-admission")


class SourceRegisterTests(unittest.TestCase):
    def setUp(self):
        self.b = build_fixtures.BuildRegisterTests()
        self.b.setUp()
        self.addCleanup(self.b.doCleanups)
        self.b.claim()
        self.b.complete()
        self.r = self.b.r
        self.t = dict(self.b.transition(), TransitionKind="helper-source", PreviousGateSha256=build_fixtures.NEW_GATE,
                      NewGateSha256=SOURCE_GATE, NewBuildSha256=build_fixtures.NEW)

    def claim(self):
        return self.r.claim_build(self.t, H, self.t["TransitionId"])

    def complete(self):
        return self.r.complete_build(self.t, H, self.t["TransitionId"], H)

    def state(self):
        return self.b.state()

    def test_source_repair_preserves_build_counts_scope_and_deadline(self):
        before = self.state()
        self.claim()
        self.r.check_build(self.t, H, self.t["TransitionId"])
        self.complete()
        after = self.state()
        for key in ("currentBuild", "scope", "buildTransitions", "refresh", "resume-admission", "completion-admission", "startUtc", "startMono"):
            self.assertEqual(before[key], after[key])
        self.assertEqual(1, after["buildTransitions"])
        self.assertEqual(1, after["helperSourceRepairs"])
        self.assertEqual(SOURCE_GATE, after["currentGate"])

    def test_no_second_source_repair_and_no_build_count_relaxation(self):
        self.claim()
        self.complete()
        next_transition = dict(self.t, TransitionId=str(uuid.uuid4()), ExpectedHeadSha256=self.state()["head"],
                               PreviousGateSha256=SOURCE_GATE, NewGateSha256=H)
        with self.assertRaises(Rejected):
            self.r.claim_build(next_transition, H, next_transition["TransitionId"])
        build = dict(next_transition, NewBuildSha256=H)
        del build["TransitionKind"]
        with self.assertRaises(Rejected):
            self.r.claim_build(build, H, build["TransitionId"])

    def test_source_only_requires_completed_prior_build(self):
        # A fresh second fixture has no preceding build transition.
        f = build_fixtures.register_fixtures.RegisterTests()
        f.setUp()
        self.addCleanup(f.doCleanups)
        with f.r._open() as db:
            state = f.r._read(db)
        t = dict(self.t, ScopeSha256=f.pin, ExpectedHeadSha256=state["head"],
                 PreviousBuildSha256=f.scope["buildSha256"], NewBuildSha256=f.scope["buildSha256"])
        with self.assertRaises(Rejected):
            f.r.claim_build(t, H, t["TransitionId"])

    def test_source_cannot_change_build_or_unknown_transition_kind(self):
        for changes in ({"NewBuildSha256": H}, {"TransitionKind": "auto-renew"}, {"TransitionKind": None}):
            t = dict(self.t, **changes)
            with self.assertRaises(Rejected):
                self.r.claim_build(t, H, t["TransitionId"])

    def test_restart_retains_pending_and_old_source_gate_rejected_after_complete(self):
        old_client = Register(self.b.f.path, self.r.scope_sha, lambda: self.b.f.now,
                              expected_build=build_fixtures.NEW, expected_gate=build_fixtures.NEW_GATE)
        self.claim()
        with self.assertRaises(Rejected):
            old_client.claim("refresh", H, str(uuid.uuid4()))
        restarted = Register(self.b.f.path, self.r.scope_sha, lambda: self.b.f.now)
        restarted.complete_build(self.t, H, self.t["TransitionId"], H)
        with self.assertRaisesRegex(Rejected, "CurrentBuildBinding"):
            old_client.status()

    def test_source_expiry_and_anchor_fault_fail_closed(self):
        self.claim()
        self.b.f.now = build_fixtures.register_fixtures.utc(self.b.f.scope["endUtc"])
        with self.assertRaisesRegex(Rejected, "WindowExpired"):
            self.complete()
        self.assertEqual(1, self.state()["buildTransitions"])
        self.assertTrue(self.state()["stopped"])

    def test_source_anchor_fault_returns_no_permit(self):
        with patch.object(self.r, "_seal", side_effect=OSError("synthetic disk full")):
            with self.assertRaises(OSError):
                self.claim()
        with self.assertRaisesRegex(Rejected, "AnchorDatabaseMismatch"):
            self.r.status()

    def test_source_repair_rejects_prior_financial_admission(self):
        c = self.b.f.claim("resume-admission")
        self.r.complete(c["id"], self.b.f.receipt(c))
        self.t["ExpectedHeadSha256"] = self.state()["head"]
        with self.assertRaisesRegex(Rejected, "FinancialAdmission"):
            self.claim()

    def test_source_concurrency_issues_only_one_claim(self):
        def attempt():
            try:
                self.claim()
                return True
            except (Rejected, OSError):
                return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(1, sum(pool.map(lambda _: attempt(), range(2))))
        self.assertEqual(1, self.state()["helperSourceRepairs"])
        self.assertEqual(1, self.state()["buildTransitions"])

    def test_source_clock_rollback_persists_terminal_stop(self):
        self.claim()
        self.b.f.now -= 1
        with self.assertRaisesRegex(Rejected, "ClockRollback"):
            self.r.check_build(self.t, H, self.t["TransitionId"])
        self.b.f.now += 100
        with self.assertRaises(Rejected):
            self.complete()
        self.assertTrue(self.state()["stopped"])


class SourceCliTests(unittest.TestCase):
    def setUp(self):
        self.b = build_fixtures.BuildCliTests()
        self.b.setUp()
        self.addCleanup(self.b.doCleanups)
        self.assertEqual(0, self.b.command("claim-build")[0])
        self.b.swap()
        self.assertEqual(0, self.b.command("complete-build")[0])
        self.old = copy.deepcopy(self.b.config)
        self.source = self.b.f.root / "source-repair"
        self.source.mkdir()
        for name in ("gate_cli.py", "completion_register.py"):
            # Inert extra comment gives a distinct reviewed source SHA.
            (self.source / name).write_bytes(Path(__file__).with_name(name).read_bytes() + b"\n# synthetic source revision\n")
        self.config = dict(self.old, ScriptPath=str(self.source / "gate_cli.py"),
                           ScriptSha256=build_fixtures.cli_fixtures.sha((self.source / "gate_cli.py").read_bytes()),
                           ModuleSha256=build_fixtures.cli_fixtures.sha((self.source / "completion_register.py").read_bytes()))
        self.path = self.b.f.root / "source-config.json"
        self.path.write_text(json.dumps(self.config))
        self.pin = build_fixtures.cli_fixtures.sha(self.path.read_bytes())
        register = Register(self.b.f.db, self.config["ScopeSha256"])
        with register._open() as db:
            state = register._read(db)
        self.t = dict(self.b.t, TransitionKind="helper-source", TransitionId=str(uuid.uuid4()),
                      ExpectedHeadSha256=state["head"], PreviousConfigPath=str(self.b.config_path),
                      PreviousGateSha256=self.b.pin, NewGateSha256=self.pin,
                      PreviousBuildSha256=state["currentBuild"], NewBuildSha256=state["currentBuild"])
        self.tp = self.b.f.root / "source-transition.json"
        self.save_transition()

    def save_transition(self):
        self.tp.write_text(json.dumps(self.t))
        self.transition_pin = build_fixtures.cli_fixtures.sha(self.tp.read_bytes())

    def command(self, command):
        req = {"command": command, "configPath": str(self.path), "configSha256": self.pin,
               "appDirectory": str(self.b.f.app if command in ("complete-build", "status") else self.b.candidate),
               "id": self.t["TransitionId"], "identitySha256": self.transition_pin,
               "transitionPath": str(self.tp), "transitionSha256": self.transition_pin}
        p = subprocess.run([sys.executable, "-I", "-S", str(self.source / "gate_cli.py")], input=json.dumps(req),
                           text=True, capture_output=True, timeout=20)
        self.assertEqual("", p.stderr)
        return p.returncode, json.loads(p.stdout)

    def save_changed_config(self, **changes):
        self.config.update(changes)
        self.path.write_text(json.dumps(self.config))
        self.pin = build_fixtures.cli_fixtures.sha(self.path.read_bytes())
        self.t["NewGateSha256"] = self.pin
        self.save_transition()

    def test_actual_cli_source_repair_same_binaries_and_counts(self):
        before = [(self.b.f.app / n).read_bytes() for n in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll")]
        self.assertEqual(0, self.command("inspect-build")[0])
        self.assertEqual(0, self.command("claim-build")[0])
        self.assertEqual(0, self.command("check-build")[0])
        self.assertEqual(1, self.command("complete-build")[0])
        self.b.write_installed(self.config, self.pin, self.path)
        self.assertEqual(0, self.command("complete-build")[0])
        status = self.command("status")[1]
        self.assertEqual(1, status["buildTransitions"])
        self.assertEqual(1, status["helperSourceRepairs"])
        self.assertEqual(0, status["resumeAdmissions"])
        self.assertEqual(before, [(self.b.f.app / n).read_bytes() for n in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll")])
        self.assertEqual(1, self.b.command("status", appDirectory=str(self.b.f.app))[0])
        self.assertEqual(1, self.command("claim-build")[0])

    def test_source_only_cannot_change_identity_candidate_or_financial_binding(self):
        for key in ("CandidateApplicationDirectory", "AccountId", "ProjectId", "CredentialReferenceId", "PlanManifestSha256", "PythonSha256", "AppSha256"):
            with self.subTest(field=key):
                original = self.config[key]
                value = H if key.endswith("Sha256") else original + "-changed"
                self.save_changed_config(**{key: value})
                self.assertEqual(1, self.command("inspect-build")[0])
                self.save_changed_config(**{key: original})

    def test_source_only_unchanged_source_relocation_is_not_repair(self):
        # Same source SHA with only a location change cannot claim a repair.
        for name in ("gate_cli.py", "completion_register.py"):
            (self.source / name).write_bytes((self.b.new_source / name).read_bytes())
        self.save_changed_config(ScriptSha256=self.old["ScriptSha256"], ModuleSha256=self.old["ModuleSha256"])
        self.assertEqual(1, self.command("inspect-build")[0])


if __name__ == "__main__":
    unittest.main()
