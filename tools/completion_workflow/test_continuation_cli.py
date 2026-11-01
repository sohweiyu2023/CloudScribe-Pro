"""Actual isolated bridge entrypoints with synthetic SQLite/media only."""
import copy
from contextlib import closing
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import unittest
import uuid

import completion_register as module
import test_successor_run as fixture_module


sha = fixture_module.sha
FINANCIAL = ("google_generation_spend_authorizations", "google_recovery_attempts", "google_recovery_scopes",
             "google_recovery_grant_transitions", "chirp_article_owned_attempts", "chirp_article_legacy_associations")


class ContinuationCliTests(unittest.TestCase):
    def setUp(self):
        self.f = fixture_module.SuccessorCliTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.text = "".join(f"{i:03}" for i in range(1, 127))
        self.f.proposal["SourceSha256"] = sha(self.text.encode())
        for i, part in enumerate(self.f.proposal["Parts"], 1): part["Range"]["TextSha256"] = sha(f"{i:03}".encode())
        self.f.manifest["proposals"][0]["proposalSha256"] = module.digest(self.f.proposal)
        self.f.manifest_path.write_text(json.dumps(self.f.manifest))
        self.f.scope["plansSha256"] = module.digest(self.f.manifest)
        self.f.scope_path.write_text(json.dumps(self.f.scope))
        self.f.config.update(ScopeSha256=module.digest(self.f.scope), PlanManifestSha256=sha(self.f.manifest_path.read_bytes()))
        self.f.config_path.write_text(json.dumps(self.f.config)); self.f.pin = sha(self.f.config_path.read_bytes())
        self.f.h.update(NewGateSha256=self.f.pin, NewScopeSha256=self.f.config["ScopeSha256"])
        self.f.h_path.write_text(json.dumps(self.f.h)); self.f.h_pin = sha(self.f.h_path.read_bytes())
        self.f.activate(); pilot, _ = self.f.accept_pilot()
        completion = self.f.policy(False); operation = str(uuid.uuid4())
        result = self.f.command("claim-completion", completion, operation); self.assertEqual(0, result[0], result)
        self.install_policy(completion, self.f.config, self.f.pin)
        self.assertEqual(0, self.f.command("complete", completion, operation, {"outcome": "Succeeded"})[0])
        with closing(sqlite3.connect(self.f.db)) as c, c:
            c.execute("UPDATE chirp_article_attempts SET Currency='USD',Scale=2,EstimateMinorUnits=1 WHERE AuthorizationId=?", (pilot,))
            c.execute("CREATE TABLE documents(Id TEXT,CurrentRevisionId TEXT,DraftText TEXT)")
            c.execute("CREATE TABLE document_revisions(Id TEXT,ContentText TEXT)")
            c.execute("INSERT INTO documents VALUES(?,?,?)", (self.f.proposal["DocumentId"].lower(), self.f.proposal["RevisionId"], self.text))
            c.execute("INSERT INTO document_revisions VALUES(?,?)", (self.f.proposal["RevisionId"], self.text))
            for table in FINANCIAL:
                if table not in ("chirp_article_owned_attempts",): c.execute(f"CREATE TABLE {table}(Id TEXT,Amount INTEGER)")
            c.execute("INSERT INTO google_generation_spend_authorizations VALUES('preserved synthetic old hold',152)")
        self.template_auth = pilot
        for i in range(2, 8): self.add_attempt(i, json.loads(completion.read_text()), accepted=True)
        self.new_folder = self.f.b.f.root / "continuation"; self.new_folder.mkdir()
        self.accepted = self.accepted_entries()
        budget = {"projectCommittedMinorUnits": 160, "allocationCommittedMinorUnits": 30,
                  "projectCeilingMinorUnits": 279, "allocationCeilingMinorUnits": 149, "overallCeilingMinorUnits": 500,
                  "taxFeeUpperBoundMinorUnits": 221, "taxFeeEvidenceSha256": sha(b"SYNTHETIC independently reviewed total tax/fee bound")}
        self.manifest = dict(copy.deepcopy(self.f.manifest), schema=3, continuation={"originalManifestSha256": module.digest(self.f.manifest),
                             "accepted": self.accepted, "budget": budget, "financialRowsSha256": self.financial_pin()})
        manifest_path = self.new_folder / "manifest.json"; manifest_path.write_text(json.dumps(self.manifest))
        start = datetime.now(timezone.utc) - timedelta(seconds=1)
        self.scope = dict(self.f.scope, schema=3, startUtc=start.isoformat().replace("+00:00", "Z"),
                          endUtc=(start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"),
                          approvalSha256=sha(b"SYNTHETIC distinct human continuation approval"),
                          predecessorRegisterPath=self.f.config["RegisterPath"], predecessorScopeSha256=self.f.config["ScopeSha256"],
                          registerPathSha256=module.path_digest(self.new_folder / "register.db"), transitionId=str(uuid.uuid4()),
                          plansSha256=module.digest(self.manifest), acceptedEvidenceSha256=module.digest(self.accepted), maximumNewAttempts=119)
        self.scope_path = self.new_folder / "scope.json"; self.scope_path.write_text(json.dumps(self.scope))
        self.config = dict(self.f.config, RegisterPath=str(self.new_folder / "register.db"), ScopeSha256=module.digest(self.scope),
                           PlanManifestPath=str(manifest_path), PlanManifestSha256=sha(manifest_path.read_bytes()))
        self.config_path = self.new_folder / "gate.json"; self.config_path.write_text(json.dumps(self.config)); self.pin = sha(self.config_path.read_bytes())
        old = module.Register(self.f.config["RegisterPath"], self.f.config["ScopeSha256"])
        with old._open() as c: head = old._read(c)["head"]
        self.h = {"SchemaVersion": 2, "TransitionKind": "accepted-continuation", "TransitionId": self.scope["transitionId"],
                  "SourceApprovalSha256": self.scope["approvalSha256"], "PreviousConfigPath": str(self.f.config_path),
                  "PreviousGateSha256": self.f.pin, "PreviousScopeSha256": old.scope_sha, "ExpectedPreviousHeadSha256": head,
                  "NewConfigPath": str(self.config_path), "NewGateSha256": self.pin, "NewScopePath": str(self.scope_path),
                  "NewScopeSha256": self.config["ScopeSha256"], "DatabaseSha256": sha(self.f.db.read_bytes()),
                  "OriginalManifestSha256": module.digest(self.f.manifest), "AcceptedEvidenceSha256": module.digest(self.accepted)}
        self.h_path = self.new_folder / "handoff.json"; self.h_path.write_text(json.dumps(self.h)); self.h_pin = sha(self.h_path.read_bytes())

    def financial_pin(self):
        with closing(sqlite3.connect(self.f.db)) as c, c:
            c.row_factory = sqlite3.Row
            return module.digest({t: sorted((dict(r) for r in c.execute("SELECT * FROM " + t)), key=module.canonical) for t in FINANCIAL})

    def install_policy(self, path, config, pin, resume=False):
        app = self.f.b.f.app
        (app.parent / ("chirp-reservation-resume-admission.json" if resume else "chirp-article-admission-policy.json")).write_bytes(path.read_bytes())
        marker_path = app.parent / "cloudscribe-install.json"; marker = json.loads(marker_path.read_text())
        marker["ChirpReservationResumePolicySha256" if resume else "ChirpArticlePolicySha256"] = sha(path.read_bytes())
        marker_path.write_text(json.dumps(marker))

    def add_attempt(self, ordinal, policy, accepted):
        with closing(sqlite3.connect(self.f.db)) as c, c:
            c.row_factory = sqlite3.Row
            a = dict(c.execute("SELECT * FROM chirp_article_attempts WHERE AuthorizationId=?", (self.template_auth,)).fetchone())
            part = self.f.proposal["Parts"][ordinal - 1]; auth = uuid.uuid4().hex; local = uuid.uuid4().hex
            envelope = json.loads(a["EnvelopeJson"]); envelope.update(ChirpAuthorizationId=str(uuid.UUID(auth)), CompiledPayloadSha256=part["PayloadSha256"], CompiledPayloadBytes=part["PayloadBytes"])
            a.update(AuthorizationId=auth, CoverageOrdinal=ordinal, PolicySha256=sha(json.dumps(policy).encode()),
                     SourceApprovalSha256=policy["SourceApprovalSha256"], ChildKey="synthetic-key-" + auth, Consumed=int(accepted),
                     ClaimedLocalAttemptId=local if accepted else None, EnvelopeJson=json.dumps(envelope))
            c.execute("INSERT INTO chirp_article_attempts(" + ",".join(a) + ") VALUES(" + ",".join("?" for _ in a) + ")", tuple(a.values()))
            c.execute("INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)", (a["AccountId"], "synthesize-speech", a["ChildKey"], int(accepted), 0, None, "google-accepted" if accepted else None, 0, local if accepted else None, part["PayloadSha256"], part["PayloadBytes"]))
            if accepted:
                media = self.f.b.f.profile / "accepted-chirp-article-audio" / (auth + ".mp3"); media.write_bytes(("inert synthetic media " + auth).encode())
                audio = {"DocumentId": self.f.proposal["DocumentId"], "RevisionId": self.f.proposal["RevisionId"], "TextSha256": part["Range"]["TextSha256"],
                         "AccountId": a["AccountId"], "OperationId": "synthesize-speech", "IdempotencyKey": a["ChildKey"], "LocalAttemptId": local,
                         "PayloadSha256": part["PayloadSha256"], "AudioPath": str(media), "AudioBytes": media.stat().st_size,
                         "AudioSha256": sha(media.read_bytes()), "VoiceName": self.config["VoiceName"] if hasattr(self, "config") else self.f.config["VoiceName"], "LanguageCode": self.f.config["LanguageCode"]}
                receipt = {"PlanSha256": a["PlanSha256"], "CoverageOrdinal": ordinal, "Range": part["Range"], "Audio": audio, "ExecutionMappingSha256": None}
                raw = json.dumps(receipt); c.execute("INSERT INTO chirp_article_receipts VALUES(?,?,?,?,?)", (a["PlanSha256"], ordinal, auth, raw, sha(raw.encode())))
                c.execute("INSERT INTO chirp_article_output_journals VALUES(?,?,?)", (auth, raw, sha(raw.encode())))
        return auth, envelope

    def accepted_entries(self):
        with closing(sqlite3.connect(self.f.db)) as c, c:
            c.row_factory = sqlite3.Row
            result = []
            for a in c.execute("SELECT * FROM chirp_article_attempts WHERE PlanSha256=? ORDER BY CoverageOrdinal", (self.f.proposal["PlanSha256"],)):
                r = c.execute("SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone(); receipt = json.loads(r["ReceiptJson"])
                result.append({"planSha256": a["PlanSha256"], "coverageOrdinal": a["CoverageOrdinal"], "authorizationId": a["AuthorizationId"],
                               "policySha256": a["PolicySha256"], "sourceApprovalSha256": a["SourceApprovalSha256"],
                               "receiptSha256": r["ReceiptSha256"], "audioSha256": receipt["Audio"]["AudioSha256"], "executionMappingSha256": None})
            return result

    def command(self, command, policy=None, operation=None, receipt=None):
        transition = command.endswith("successor")
        req = {"command": command, "configPath": str(self.f.b.f.app.parent / "completion-workflow-gate.json" if command == "complete-successor" else self.config_path),
               "configSha256": self.pin, "appDirectory": str(self.f.new_app if transition and command != "complete-successor" else self.f.b.f.app),
               "id": self.h["TransitionId"] if transition else operation or str(uuid.uuid4()),
               "identitySha256": self.h_pin if transition else sha(policy.read_bytes()) if policy else fixture_module.H,
               "handoffPath": str(self.h_path), "handoffSha256": self.h_pin, "policyPath": str(policy) if policy else None, "receipt": receipt}
        p = subprocess.run([sys.executable, "-I", "-S", str(self.f.source / "gate_cli.py")], input=json.dumps(req), text=True, capture_output=True, timeout=20)
        self.assertEqual("", p.stderr); return p.returncode, json.loads(p.stdout)

    def activate(self):
        for command in ("inspect-successor", "claim-successor", "check-successor"):
            r = self.command(command); self.assertEqual(0, r[0], r)
        self.f.b.write_installed(self.config, self.pin, self.config_path)
        r = self.command("complete-successor"); self.assertEqual(0, r[0], r)

    def policy(self):
        p = json.loads(self.f.policy(False).read_text()); p.update(SourceApprovalSha256=self.scope["approvalSha256"], ExpiresAtUtc=self.scope["endUtc"], MaximumNewAttempts=119, MaximumAdditionalReservationMinorUnits=119)
        p["Budget"].update(ProjectCommittedMinorUnits=160, AllocationCommittedMinorUnits=30, ProjectCeilingMinorUnits=279)
        p["Articles"][0]["AllowedSegments"] = [{"CoverageOrdinal": i, "MaximumMinorUnits": 1} for i in range(8, 127)]
        path = self.new_folder / "remaining.json"; path.write_text(json.dumps(p)); return path

    def admit(self):
        path = self.policy(); operation = str(uuid.uuid4()); r = self.command("claim-completion", path, operation); self.assertEqual(0, r[0], r)
        r = self.command("check", path, operation); self.assertEqual(0, r[0], r)
        self.install_policy(path, self.config, self.pin); r = self.command("complete", path, operation, {"outcome": "Succeeded"}); self.assertEqual(0, r[0], r)
        return path

    def test_actual_cli_2_to_3_acceptance_and_119_admission(self):
        self.activate(); path = self.admit()
        self.assertEqual(0, self.command("verify-completion", path)[0])
        status = self.command("status"); self.assertEqual(0, status[0], status)
        self.assertEqual((0, 1), (status[1]["pilotAdmissions"], status[1]["totalAdmissions"]))

    def test_tampered_accepted_audio_and_missing_final_output_block_before_transition(self):
        media = self.f.b.f.profile / "accepted-chirp-article-audio" / (self.accepted[0]["authorizationId"] + ".mp3")
        media.write_bytes(b"tampered synthetic file")
        self.assertNotEqual(0, self.command("inspect-successor")[0])
        self.assertFalse(Path(self.config["RegisterPath"]).exists())

    def test_unrelated_new_hold_blocks_current_financial_evidence(self):
        self.activate()
        with closing(sqlite3.connect(self.f.db)) as c, c: c.execute("INSERT INTO google_generation_spend_authorizations VALUES('new unrelated synthetic hold',1)")
        self.assertNotEqual(0, self.command("claim-completion", self.policy())[0])

    def test_progress_then_one_clean_held_resume_preserves_same_reservation(self):
        self.activate(); path = self.admit(); policy = json.loads(path.read_text())
        self.add_attempt(8, policy, True); held, envelope = self.add_attempt(9, policy, False)
        self.assertEqual(0, self.command("verify-completion", path)[0])
        resume = {"HistoricalPolicy": policy, "AuthorizationId": str(uuid.UUID(held)), "ExecutionEnvelope": envelope,
                  "ExpiresAtUtc": self.scope["endUtc"], "MaximumExistingDispatches": 1, "MaximumNewReservations": 0,
                  "MaximumAdditionalReservationMinorUnits": 0}
        resume_path = self.new_folder / "resume.json"; resume_path.write_text(json.dumps(resume)); op = str(uuid.uuid4())
        result = self.command("claim-resume", resume_path, op); self.assertEqual(0, result[0], result)
        self.assertEqual(0, self.command("check", resume_path, op)[0])
        self.install_policy(resume_path, self.config, self.pin, resume=True)
        result = self.command("complete", resume_path, op, {"outcome": "Succeeded"}); self.assertEqual(0, result[0], result)


if __name__ == "__main__":
    unittest.main(verbosity=2)
