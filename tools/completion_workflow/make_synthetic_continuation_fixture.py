"""Fresh inert installer continuation fixture. Never selects an existing profile."""
import json
from pathlib import Path
import sys
from unittest.mock import patch


def main():
    if len(sys.argv) != 2:
        raise ValueError("FreshSyntheticRootRequired")
    root = Path(sys.argv[1]).absolute()
    if root.exists() or any(p.is_symlink() or p.is_junction() for p in root.parents):
        raise ValueError("FreshSyntheticRootRequired")
    root.mkdir()
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from test_continuation_cli import ContinuationCliTests, sha

    class OwnedDirectory:
        name = str(root)
        def cleanup(self):
            pass  # Fresh caller-owned fixture retention, no hidden deletion.

    f = ContinuationCliTests()
    with patch("test_gate_cli.tempfile.TemporaryDirectory", return_value=OwnedDirectory()):
        f.setUp()
    package = f.new_folder / "package"; app = package / "app"; app.mkdir(parents=True)
    for name in ("CloudScribe.dll", "CloudScribe.Infrastructure.dll"):
        (app / name).write_bytes((f.f.new_app / name).read_bytes())
    f.f.new_app = app; f.config["CandidateApplicationDirectory"] = str(app)
    f.config_path.write_text(json.dumps(f.config)); f.pin = sha(f.config_path.read_bytes())
    f.h["NewGateSha256"] = f.pin; f.h_path.write_text(json.dumps(f.h)); f.h_pin = sha(f.h_path.read_bytes())
    result = f.command("inspect-successor")
    if result[0] != 0 or result[1].get("valid") is not True:
        raise ValueError("SyntheticContinuationInspectionFailed")
    print(json.dumps({"root": str(root), "package": str(package), "target": str(f.f.b.f.app.parent), "profile": str(f.f.b.f.profile),
                      "gatePath": str(f.config_path), "gateSha256": f.pin, "scopeSha256": f.config["ScopeSha256"],
                      "scopePath": str(f.scope_path), "handoffPath": str(f.h_path), "handoffSha256": f.h_pin,
                      "handoffId": f.h["TransitionId"], "previousGateSha256": f.f.pin, "previousScopeSha256": f.f.config["ScopeSha256"],
                      "syntheticOnly": True, "providerCalls": 0}, separators=(",", ":")))


if __name__ == "__main__":
    main()
