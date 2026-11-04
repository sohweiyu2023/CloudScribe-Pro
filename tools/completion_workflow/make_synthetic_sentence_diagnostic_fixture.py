"""Caller-owned inert installer fixtures. Never a live admission producer."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

if len(sys.argv) not in (2,3) or (len(sys.argv)==3 and sys.argv[2] not in ('expired','unused')):
    raise ValueError('SyntheticRootRequired')
root=Path(sys.argv[1]).absolute()
if (root.exists() or any(p.is_symlink() or p.is_junction() for p in (root,*root.parents))
        or not root.resolve().is_relative_to(Path(tempfile.gettempdir()).resolve())):
    raise ValueError('SyntheticRootRequired')
root.mkdir();owned=root/'fixture';owned.mkdir()
sys.path.insert(0,str(Path(__file__).resolve().parent))
import test_sentence_diagnostic_cli as fixtures
class OwnedDirectory:
    name=str(owned)
    def cleanup(self):pass
owner=unittest.TestCase()
with patch('test_gate_cli.tempfile.TemporaryDirectory',return_value=OwnedDirectory()):
    if len(sys.argv)==3 and sys.argv[2]=='unused':
        import test_unused_diagnostic_renewal_cli as renewal
        f,policy_path,_,old,before=renewal.prepare_unused_renewal(owner)
    else:
        f,policy_path,_,old,before=fixtures.prepare_diagnostic(owner,expired=len(sys.argv)==3)
print(json.dumps(dict(root=str(root),package=str(Path(f.config['CandidateApplicationDirectory']).parent),
    target=str(Path(f.config['ApplicationDirectory']).parent),profile=str(f.f.b.f.profile),gatePath=str(f.config_path),gateSha256=f.pin,
    scopeSha256=f.config['ScopeSha256'],scopePath=str(f.scope_path),handoffPath=str(f.h_path),handoffSha256=f.h_pin,
    handoffId=f.h['TransitionId'],previousGatePath=f.h['PreviousConfigPath'],previousGateSha256=f.h['PreviousGateSha256'],
    previousScopeSha256=f.h['PreviousScopeSha256'],expectedRefreshes=before['refresh'],expectedAdmissions=before['admissions'],
    deadlineUtc=fixtures.m.effective_deadline(f.scope,f.h.get('ClockEpochEvidence',f.h['InheritedClockEpochEvidence'])),
    databasePath=str(f.f.db),databaseSha256=fixtures.g.sha(f.f.db.read_bytes()),policyPath=str(policy_path),policySha256=fixtures.g.sha(policy_path.read_bytes()),
    providerCalls=0,credentialReads=0,syntheticOnly=True)))
