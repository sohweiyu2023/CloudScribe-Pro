"""Fresh inert successor installer fixture; no existing profile or provider access."""
import json
from pathlib import Path
import sys
import uuid
from unittest.mock import patch


def main():
    if len(sys.argv) != 2:
        raise ValueError("FreshSyntheticRootRequired")
    root = Path(sys.argv[1]).absolute()
    if not root.is_absolute() or root.exists():
        raise ValueError("FreshSyntheticRootRequired")
    for ancestor in root.parents:
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError("LinkedSyntheticRoot")
    root.mkdir()  # Exclusive creation: an existing destination is never reused.
    # These are reviewed synthetic fixtures, not application composition.
    tools = Path(__file__).resolve().parent
    sys.path.insert(0, str(tools))
    import test_successor_run as fixtures

    class OwnedDirectory:
        name = str(root)

        def cleanup(self):
            pass  # Caller owns retention/deletion of this fresh synthetic root.

    fixture = fixtures.SuccessorCliTests()
    with patch("test_gate_cli.tempfile.TemporaryDirectory", return_value=OwnedDirectory()):
        fixture.setUp()
    package = fixture.new_folder / "package"
    package.mkdir()
    app = package / "app"
    fixture.new_app.rename(app)
    fixture.new_app = app
    fixture.config["CandidateApplicationDirectory"] = str(app)
    fixture.config_path.write_text(json.dumps(fixture.config), encoding="utf-8")
    fixture.pin = fixtures.sha(fixture.config_path.read_bytes())
    fixture.h["NewGateSha256"] = fixture.pin
    fixture.h["FailedAuthorizationId"] = str(uuid.UUID(fixture.h["FailedAuthorizationId"]))
    fixture.h_path.write_text(json.dumps(fixture.h), encoding="utf-8")
    fixture.h_pin = fixtures.sha(fixture.h_path.read_bytes())
    result = fixture.command("inspect-successor")
    if result[0] != 0 or result[1].get("valid") is not True:
        raise ValueError("SyntheticSuccessorInspectionFailed")
    metadata = {"root": str(root), "package": str(package), "target": str(fixture.b.f.app.parent),
                "profile": str(fixture.b.f.profile), "gatePath": str(fixture.config_path), "gateSha256": fixture.pin,
                "scopeSha256": fixture.config["ScopeSha256"], "scopePath": str(fixture.scope_path),
                "handoffPath": str(fixture.h_path), "handoffSha256": fixture.h_pin, "handoffId": fixture.h["TransitionId"],
                "previousConfigPath": fixture.h["PreviousConfigPath"], "previousGateSha256": fixture.old_pin,
                "previousScopeSha256": fixture.old["ScopeSha256"], "syntheticOnly": True, "providerCalls": 0}
    print(json.dumps(metadata, separators=(",", ":")))


if __name__ == "__main__":
    main()
