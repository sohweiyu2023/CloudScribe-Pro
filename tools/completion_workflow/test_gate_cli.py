"""Real isolated bridge/process, synthetic binaries/scope, ZERO network/credentials."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timedelta, timezone

from completion_register import Register, digest, path_digest
from fixture_data import make_plan_fixture


def sha(data):
    return hashlib.sha256(data).hexdigest()


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.app = self.root / "app"
        self.app.mkdir()
        (self.app / "CloudScribe.dll").write_bytes(b"synthetic app, not executable")
        (self.app / "CloudScribe.Infrastructure.dll").write_bytes(b"synthetic infrastructure, not executable")
        self.cli = self.root / "gate_cli.py"
        self.cli.write_bytes(Path(__file__).with_name("gate_cli.py").read_bytes())
        self.module = self.root / "completion_register.py"
        self.module.write_bytes(Path(__file__).with_name("completion_register.py").read_bytes())
        self.db = self.root / "register.db"
        self.profile = self.root / "synthetic-profile"
        self.profile.mkdir()
        self.config_path = self.root / "gate.private.json"
        manifest, resume, completion = make_plan_fixture(digest)
        self.manifest_path = self.root / "plans.private.json"
        self.manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        self.resume_path = self.root / "resume.private.json"
        self.resume_path.write_text(json.dumps(resume), encoding="utf-8")
        self.completion_path = self.root / "completion.private.json"
        self.completion_path.write_text(json.dumps(completion), encoding="utf-8")
        self.config = {"SchemaVersion": 1, "PythonPath": sys.executable, "PythonSha256": sha(Path(sys.executable).read_bytes()),
                       "ScriptPath": str(self.cli), "ScriptSha256": sha(self.cli.read_bytes()), "ModuleSha256": sha(self.module.read_bytes()),
                       "RegisterPath": str(self.db), "ScopeSha256": "", "ApplicationDirectory": str(self.app),
                       "CandidateApplicationDirectory": str(self.app), "AppSha256": sha((self.app / "CloudScribe.dll").read_bytes()),
                       "InfrastructureSha256": sha((self.app / "CloudScribe.Infrastructure.dll").read_bytes()),
                       "ProfileRootSha256": sha(str(self.profile).upper().encode()), "AccountId": "synthetic-account", "ProjectId": "synthetic-project",
                       "CredentialReferenceId": "synthetic-reference", "CatalogEndpoint": "https://texttospeech.googleapis.com/v1/voices",
                       "SynthesisEndpoint": "https://texttospeech.googleapis.com/v1/text:synthesize",
                       "PlanManifestPath": str(self.manifest_path), "PlanManifestSha256": sha(self.manifest_path.read_bytes()),
                       "VoiceName": "cmn-CN-Chirp3-HD-Alnilam", "LanguageCode": "cmn-CN"}
        start = datetime.now(timezone.utc) - timedelta(minutes=1)
        self.scope = {"schema": 1, "approvalSha256": sha(b"synthetic unapproved fixture"),
                      "buildSha256": sha((self.config["AppSha256"] + "|" + self.config["InfrastructureSha256"]).encode()),
                      "profileSha256": self.config["ProfileRootSha256"], "accountSha256": sha(b"synthetic-account"),
                      "projectSha256": sha(b"synthetic-project"), "plansSha256": digest(manifest),
                      "registerPathSha256": path_digest(self.db), "startUtc": start.isoformat().replace("+00:00", "Z"),
                      "endUtc": (start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"),
                      "maxRefreshes": 24, "maxResumeAdmissions": 24}
        self.config["ScopeSha256"] = digest(self.scope)
        Register.create(self.db, self.scope, self.config["ScopeSha256"])
        self.save_config()
        self.identity = sha(b"synthetic operation")

    def save_config(self):
        self.config_path.write_text(json.dumps(self.config), encoding="utf-8")
        self.pin = sha(self.config_path.read_bytes())

    def command(self, command, operation_id=None, receipt=None, isolated=True):
        policy = self.resume_path if command == "claim-resume" else self.completion_path if command in ("claim-completion", "verify-completion") else None
        if policy:
            self.identity = sha(policy.read_bytes())
        request = {"command": command, "configPath": str(self.config_path), "configSha256": self.pin,
                   "appDirectory": str(self.app), "id": operation_id or str(uuid.uuid4()),
                   "identitySha256": self.identity, "receipt": receipt, "policyPath": str(policy) if policy else None}
        arguments = [sys.executable] + (["-I", "-S"] if isolated else []) + [str(self.cli)]
        environment = dict(os.environ, PYTHONPATH=str(self.root))
        result = subprocess.run(arguments, input=json.dumps(request), text=True, capture_output=True,
                                cwd=self.root, env=environment, timeout=20)
        self.assertEqual("", result.stderr)
        return result.returncode, json.loads(result.stdout)

    def test_actual_bridge_claim_check_completion_restart(self):
        operation_id = str(uuid.uuid4())
        self.assertEqual(0, self.command("claim-refresh", operation_id)[0])
        self.assertEqual((0, {"valid": True}), self.command("check", operation_id))
        receipt = {"id": operation_id, "identitySha256": self.identity, "outcome": "Succeeded",
                   "evidenceSha256": sha(b"synthetic observed response"), "catalogVerified": True,
                   "probeInputEmpty": True, "probeHttpStatus": 400, "probeStatus": "INVALID_ARGUMENT", "audioAbsent": True}
        self.assertEqual((0, {"outcome": "Succeeded"}), self.command("complete", operation_id, receipt))
        status = self.command("status")[1]
        self.assertEqual(1, status["refreshes"])
        self.assertIsNone(status["pendingId"])

    def test_interpreter_isolation_ignores_sitecustomize_and_module_shadow(self):
        sentinel = self.root / "SHOULD_NOT_EXIST"
        malicious = f"from pathlib import Path;Path({str(sentinel)!r}).write_text('injected')\n"
        (self.root / "sitecustomize.py").write_text(malicious)
        # Cwd shadowing uses another directory because the verified module has a fixed hash.
        shadow = self.root / "shadow"
        shadow.mkdir()
        (shadow / "completion_register.py").write_text(malicious)
        self.assertEqual(0, self.command("inspect")[0])
        self.assertFalse(sentinel.exists())
        self.assertEqual(1, self.command("inspect", isolated=False)[0])

    def test_changed_build_or_module_cannot_claim(self):
        (self.app / "CloudScribe.dll").write_bytes(b"changed binary")
        self.assertEqual(1, self.command("claim-refresh")[0])
        self.assertEqual(0, Register(self.db, self.config["ScopeSha256"]).status()["refreshes"])
        (self.app / "CloudScribe.dll").write_bytes(b"synthetic app, not executable")
        self.module.write_text("raise RuntimeError('must not execute')")
        self.assertEqual(1, self.command("claim-refresh")[0])

    def test_wrong_account_scope_rejected_before_claim(self):
        self.config["AccountId"] = "other-account"
        self.save_config()
        self.assertEqual(1, self.command("claim-refresh")[0])
        self.assertEqual(0, Register(self.db, self.config["ScopeSha256"]).status()["refreshes"])

    def test_unfinished_installer_claim_blocks_restart(self):
        operation_id = str(uuid.uuid4())
        self.assertEqual(0, self.command("claim-resume", operation_id)[0])
        self.assertEqual(1, self.command("claim-resume")[0])
        status = self.command("status")[1]
        self.assertEqual(operation_id, status["pendingId"])
        self.assertEqual(1, status["resumeAdmissions"])

    def test_single_completion_policy_admission_is_bounded(self):
        operation_id = str(uuid.uuid4())
        self.assertEqual(0, self.command("claim-completion", operation_id)[0])
        receipt = {"id": operation_id, "identitySha256": self.identity, "outcome": "Succeeded",
                   "evidenceSha256": sha(b"synthetic installed pin"), "policyVerified": True,
                   "maximumNewAttempts": 108, "additionalReservationCents": 108}
        self.write_installed_policy(self.completion_path, resume=False)
        self.assertEqual((0, {"outcome": "Succeeded"}), self.command("complete", operation_id, receipt))
        self.assertEqual((0, {"valid": True}), self.command("verify-completion"))
        self.assertEqual(1, self.command("claim-completion")[0])

    def test_out_of_scope_completion_budget_stops_run(self):
        policy = json.loads(self.completion_path.read_text())
        policy["MaximumNewAttempts"] = 109
        policy["MaximumAdditionalReservationMinorUnits"] = 109
        self.completion_path.write_text(json.dumps(policy))
        self.assertEqual(1, self.command("claim-completion")[0])
        self.assertEqual(0, self.command("status")[1]["completionAdmissions"])

    def write_installed_policy(self, path, resume):
        policy_name = "chirp-reservation-resume-admission.json" if resume else "chirp-article-admission-policy.json"
        (self.app.parent / policy_name).write_bytes(path.read_bytes())
        marker = {"Product": "CloudScribe Pro", "CompletionGateProtocolVersion": 1, "Profile": str(self.profile),
                  "CompletionWorkflowGateSha256": self.pin,
                  "ChirpReservationResumePolicySha256" if resume else "ChirpArticlePolicySha256": sha(path.read_bytes())}
        (self.app.parent / "cloudscribe-install.json").write_text(json.dumps(marker), encoding="utf-8")
        (self.profile / ".completion-workflow-required.json").write_text(json.dumps({"GateSha256": self.pin, "ScopeSha256": self.config["ScopeSha256"]}), encoding="utf-8")

    def test_installer_success_assertion_without_actual_readback_cannot_complete(self):
        operation_id = str(uuid.uuid4())
        self.assertEqual(0, self.command("claim-resume", operation_id)[0])
        fake = {"id": operation_id, "identitySha256": self.identity, "outcome": "Succeeded", "evidenceSha256": sha(b"not proof"),
                "policyVerified": True, "zeroNewFunds": True}
        self.assertEqual(1, self.command("complete", operation_id, fake)[0])
        self.assertEqual(operation_id, self.command("status")[1]["pendingId"])
        self.write_installed_policy(self.resume_path, resume=True)
        self.assertEqual((0, {"outcome": "Succeeded"}), self.command("complete", operation_id, fake))

    def test_unadmitted_completion_policy_cannot_be_retained(self):
        self.assertEqual(1, self.command("verify-completion")[0])

    def prepare_uninitialized_reviewed_fixture(self):
        profile = self.root / "new-synthetic-profile"
        profile.mkdir()
        approval = self.root / "synthetic-approval.private.txt"
        approval.write_text("SYNTHETIC UNAPPROVED FIXTURE ONLY", encoding="utf-8")
        self.config["ProfileRootSha256"] = sha(str(profile).upper().encode())
        self.config["RegisterPath"] = str(self.root / "not-yet-initialized.db")
        self.scope["profileSha256"] = self.config["ProfileRootSha256"]
        self.scope["registerPathSha256"] = path_digest(self.config["RegisterPath"])
        self.scope["approvalSha256"] = sha(approval.read_bytes())
        self.config["ScopeSha256"] = digest(self.scope)
        self.save_config()
        marker = self.app.parent / "cloudscribe-install.json"
        marker.write_text(json.dumps({"Product": "CloudScribe Pro", "CompletionGateProtocolVersion": 1, "Profile": str(profile)}), encoding="utf-8")
        scope_file = self.root / "synthetic-scope.private.json"
        scope_file.write_text(json.dumps(self.scope), encoding="utf-8")
        script = Path(__file__).with_name("initialize_reviewed_register.py")
        return [sys.executable, "-I", "-S", str(script), "--gate", str(self.config_path), "--gate-sha256", self.pin,
                "--scope", str(scope_file), "--scope-sha256", self.config["ScopeSha256"], "--approval", str(approval),
                "--approval-sha256", sha(approval.read_bytes())]

    def test_initialization_is_separate_single_use_and_supplies_no_spending_authority(self):
        arguments = self.prepare_uninitialized_reviewed_fixture()
        result = subprocess.run(arguments, capture_output=True, text=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stdout)
        record = json.loads(result.stdout)
        self.assertFalse(record["spendingAuthority"])
        self.assertFalse(record["appActivated"])
        self.assertEqual(0, record["providerCalls"])
        self.assertEqual(0, Register(self.config["RegisterPath"], self.config["ScopeSha256"]).status()["refreshes"])
        replay = subprocess.run(arguments, capture_output=True, text=True, timeout=20)
        self.assertEqual(1, replay.returncode)

    def test_initialization_rejects_wrong_approval_and_old_installed_protocol(self):
        arguments = self.prepare_uninitialized_reviewed_fixture()
        arguments[-1] = "a" * 64
        self.assertEqual(1, subprocess.run(arguments, capture_output=True, text=True, timeout=20).returncode)
        self.assertFalse(Path(self.config["RegisterPath"]).exists())
        arguments[-1] = self.scope["approvalSha256"]
        marker = self.app.parent / "cloudscribe-install.json"
        data = json.loads(marker.read_text())
        data["CompletionGateProtocolVersion"] = 0
        marker.write_text(json.dumps(data), encoding="utf-8")
        self.assertEqual(1, subprocess.run(arguments, capture_output=True, text=True, timeout=20).returncode)
        self.assertFalse(Path(self.config["RegisterPath"]).exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
