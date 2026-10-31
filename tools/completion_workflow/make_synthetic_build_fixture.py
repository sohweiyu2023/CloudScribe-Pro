"""Fresh synthetic installer fixture only. Never opens an existing profile/root."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import types
import uuid


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    # Reuse the existing fresh-root fixture; it refuses any existing destination.
    tools = Path(__file__).resolve().parent
    result = subprocess.run([sys.executable, "-I", "-S", str(tools / "make_synthetic_gate_fixture.py"), sys.argv[1]],
                            capture_output=True, text=True, check=True, timeout=30)
    metadata = json.loads(result.stdout)
    root = Path(metadata["root"])
    module = types.ModuleType("synthetic_build_register")
    exec(compile((tools / "completion_register.py").read_bytes(), str(tools / "completion_register.py"), "exec"), module.__dict__)
    old_path = Path(metadata["gatePath"])
    old = json.loads(old_path.read_text())
    # Preserve the immutable predecessor source/config independently of successor.
    old_source = root / "old-source"
    old_source.mkdir()
    for name in ("gate_cli.py", "completion_register.py"):
        (old_source / name).write_bytes((tools / name).read_bytes())
    old["ScriptPath"] = str(old_source / "gate_cli.py")
    old_path.write_text(json.dumps(old), encoding="utf-8")
    old_pin = sha(old_path.read_bytes())
    target, package, profile = (Path(metadata[key]) for key in ("target", "package", "profile"))
    marker = {"Product": "CloudScribe Pro", "Version": 1, "Profile": str(profile),
              "AppSha256": old["AppSha256"].upper(), "CompletionGateProtocolVersion": 1,
              "CompletionWorkflowGateSha256": old_pin}
    (target / "cloudscribe-install.json").write_text(json.dumps(marker), encoding="utf-8")
    (target / "completion-workflow-gate.json").write_bytes(old_path.read_bytes())
    (profile / ".completion-workflow-required.json").write_text(json.dumps({"GateSha256": old_pin, "ScopeSha256": old["ScopeSha256"]}), encoding="utf-8")
    register = module.Register(old["RegisterPath"], old["ScopeSha256"])
    operation = str(uuid.uuid4())
    identity = sha(b"SYNTHETIC COMPLETED REFRESH; NO PROVIDER REQUEST")
    register.claim("refresh", identity, operation)
    register.complete(operation, {"id": operation, "identitySha256": identity, "outcome": "Succeeded",
                                 "evidenceSha256": identity, "catalogVerified": True, "probeInputEmpty": True,
                                 "probeHttpStatus": 400, "probeStatus": "INVALID_ARGUMENT", "audioAbsent": True})
    # Only owned fresh fixture payload changes; never an executable real DLL.
    for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"):
        (package / "app" / name).write_bytes(("SUCCESSOR INERT SYNTHETIC " + name).encode())
    new = dict(old, ScriptPath=str(tools / "gate_cli.py"),
               AppSha256=sha((package / "app/CloudScribe.dll").read_bytes()),
               InfrastructureSha256=sha((package / "app/CloudScribe.Infrastructure.dll").read_bytes()))
    new_path = root / "synthetic-successor-gate.private.json"
    new_path.write_text(json.dumps(new), encoding="utf-8")
    new_pin = sha(new_path.read_bytes())
    with register._open() as db:
        state = register._read(db)
    transition = {"SchemaVersion": 1, "TransitionId": str(uuid.uuid4()),
                  "SourceApprovalSha256": state["scope"]["approvalSha256"], "ScopeSha256": old["ScopeSha256"],
                  "ExpectedHeadSha256": state["head"], "PreviousConfigPath": str(old_path),
                  "PreviousGateSha256": old_pin, "NewGateSha256": new_pin,
                  "PreviousBuildSha256": state["currentBuild"],
                  "NewBuildSha256": sha((new["AppSha256"] + "|" + new["InfrastructureSha256"]).encode())}
    transition_path = root / "synthetic-build-transition.private.json"
    transition_path.write_text(json.dumps(transition), encoding="utf-8")
    metadata.update(gatePath=str(new_path), gateSha256=new_pin, transitionPath=str(transition_path),
                    transitionSha256=sha(transition_path.read_bytes()), transitionId=transition["TransitionId"],
                    previousGateSha256=old_pin, previousConfigPath=str(old_path))
    (root / "build-fixture.private.json").write_text(json.dumps(metadata), encoding="utf-8")
    print(json.dumps(metadata))


if __name__ == "__main__":
    main()
