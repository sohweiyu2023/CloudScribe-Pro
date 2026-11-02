"""Caller-owned synthetic installer repair fixture, no live paths or providers."""
import json
from pathlib import Path
import sys
from unittest import TestCase
from unittest.mock import patch


def main():
    if len(sys.argv)!=2:raise ValueError('FreshSyntheticRootRequired')
    root=Path(sys.argv[1]).absolute()
    if root.exists() or any(p.is_symlink() or p.is_junction() for p in root.parents):raise ValueError('FreshSyntheticRootRequired')
    root.mkdir();owned=root/'fixture';owned.mkdir()
    sys.path.insert(0,str(Path(__file__).resolve().parent))
    import interpreter_test_fixture as fixture
    class OwnedDirectory:
        name=str(owned)
        def cleanup(self):pass
    owner=TestCase()
    with patch('test_gate_cli.tempfile.TemporaryDirectory',return_value=OwnedDirectory()):
        f=fixture.prepare(owner)
    t=fixture.repair(f)
    print(json.dumps(dict(root=str(root),package=str(Path(f.config['CandidateApplicationDirectory']).parent),target=str(Path(f.config['ApplicationDirectory']).parent),profile=str(f.f.b.f.profile),gatePath=str(f.config_path),gateSha256=f.pin,scopeSha256=f.config['ScopeSha256'],transitionPath=str(t['path']),transitionSha256=t['pin'],transitionId=t['record']['TransitionId'],previousGateSha256=t['oldPin'],evidencePath=str(t['evidencePath']),syntheticOnly=True,providerCalls=0)))

if __name__=='__main__':main()
