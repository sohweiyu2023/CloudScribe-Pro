"""Offline fixture only: inert DLL bytes, fake account, no real app/profile/key access."""
import hashlib
import json
import os
from pathlib import Path
import sys
import types
from datetime import datetime, timedelta, timezone


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    root = Path(os.path.abspath(sys.argv[1]))
    root.mkdir(exist_ok=False)
    package = root / "package"
    install = root / "install"
    profile = root / "synthetic-profile"
    profile.mkdir()
    for folder in (package / "app", install / "app"):
        folder.mkdir(parents=True)
        (folder / "CloudScribe.dll").write_bytes(b"inert synthetic app fixture")
        (folder / "CloudScribe.Infrastructure.dll").write_bytes(b"inert synthetic infrastructure fixture")
    tools = Path(__file__).parent
    module = types.ModuleType("verified_fixture_register")
    exec(compile((tools / "completion_register.py").read_bytes(), str(tools / "completion_register.py"), "exec"), module.__dict__)
    fixtures = types.ModuleType("synthetic_fixture_data")
    exec(compile((tools / "fixture_data.py").read_bytes(), str(tools / "fixture_data.py"), "exec"), fixtures.__dict__)
    manifest, resume, completion = fixtures.make_plan_fixture(module.digest)
    manifest_path = root / "synthetic-plans.private.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    resume_path = root / "synthetic-resume.private.json"
    resume_path.write_text(json.dumps(resume), encoding="utf-8")
    completion_path = root / "synthetic-completion.private.json"
    completion_path.write_text(json.dumps(completion), encoding="utf-8")
    config = {"SchemaVersion": 1, "PythonPath": sys.executable, "PythonSha256": sha(Path(sys.executable).read_bytes()),
              "ScriptPath": str(tools / "gate_cli.py"), "ScriptSha256": sha((tools / "gate_cli.py").read_bytes()),
              "ModuleSha256": sha((tools / "completion_register.py").read_bytes()), "RegisterPath": str(root / "synthetic.db"),
              "ScopeSha256": "", "ApplicationDirectory": str(install / "app"), "CandidateApplicationDirectory": str(package / "app"),
              "AppSha256": sha((package / "app/CloudScribe.dll").read_bytes()),
              "InfrastructureSha256": sha((package / "app/CloudScribe.Infrastructure.dll").read_bytes()),
              "ProfileRootSha256": sha(str(profile).upper().encode()), "AccountId": "synthetic-account",
              "ProjectId": "synthetic-project", "CredentialReferenceId": "synthetic-reference",
              "CatalogEndpoint": "https://texttospeech.googleapis.com/v1/voices", "SynthesisEndpoint": "https://texttospeech.googleapis.com/v1/text:synthesize",
              "PlanManifestPath": str(manifest_path), "PlanManifestSha256": sha(manifest_path.read_bytes()),
              "VoiceName": "cmn-CN-Chirp3-HD-Alnilam", "LanguageCode": "cmn-CN"}
    start = datetime.now(timezone.utc) - timedelta(seconds=1)
    scope = {"schema": 1, "approvalSha256": sha(b"SYNTHETIC FIXTURE IS NOT USER AUTHORITY"),
             "buildSha256": sha((config["AppSha256"] + "|" + config["InfrastructureSha256"]).encode()),
             "profileSha256": config["ProfileRootSha256"], "accountSha256": sha(config["AccountId"].encode()),
             "projectSha256": sha(config["ProjectId"].encode()), "plansSha256": module.digest(manifest),
             "registerPathSha256": module.path_digest(config["RegisterPath"]),
             "startUtc": start.isoformat().replace("+00:00", "Z"), "endUtc": (start + timedelta(hours=6)).isoformat().replace("+00:00", "Z"),
             "maxRefreshes": 24, "maxResumeAdmissions": 24}
    config["ScopeSha256"] = module.digest(scope)
    module.Register.create(config["RegisterPath"], scope, config["ScopeSha256"])
    path = root / "synthetic-gate.private.json"
    path.write_text(json.dumps(config), encoding="utf-8")
    metadata = {"root": str(root), "package": str(package), "target": str(install), "profile": str(profile),
                "gatePath": str(path), "gateSha256": sha(path.read_bytes()), "scopeSha256": config["ScopeSha256"],
                "resumePath": str(resume_path), "resumeSha256": sha(resume_path.read_bytes()),
                "completionPath": str(completion_path), "completionSha256": sha(completion_path.read_bytes())}
    (root / "fixture.private.json").write_text(json.dumps(metadata), encoding="utf-8")
    print(json.dumps(metadata))


if __name__ == "__main__":
    main()
