"""Caller-owned fresh-window/interpreter successor fixture. No real profile or provider."""
import json
from pathlib import Path
import sys
from unittest import TestCase
from unittest.mock import patch

if len(sys.argv)!=2:raise ValueError('FreshSyntheticRootRequired')
root=Path(sys.argv[1]).absolute()
if root.exists() or any(p.is_symlink() or p.is_junction() for p in root.parents):raise ValueError('FreshSyntheticRootRequired')
root.mkdir();owned=root/'fixture';owned.mkdir();sys.path.insert(0,str(Path(__file__).resolve().parent))
from test_expired_interpreter_successor_cli import prepare_successor
class OwnedDirectory:
    name=str(owned)
    def cleanup(self):pass
owner=TestCase()
with patch('test_gate_cli.tempfile.TemporaryDirectory',return_value=OwnedDirectory()):f,old,old_config,grant,evidence=prepare_successor(owner)
print(json.dumps(dict(root=str(root),package=str(Path(f.config['CandidateApplicationDirectory']).parent),target=str(Path(f.config['ApplicationDirectory']).parent),profile=str(f.f.b.f.profile),gatePath=str(f.config_path),gateSha256=f.pin,scopeSha256=f.config['ScopeSha256'],handoffPath=str(f.h_path),handoffSha256=f.h_pin,handoffId=f.h['TransitionId'],previousGateSha256=f.h['PreviousGateSha256'],previousScopeSha256=f.h['PreviousScopeSha256'],providerCalls=0,syntheticOnly=True)))
