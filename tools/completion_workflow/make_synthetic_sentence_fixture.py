"""Caller-owned same-window installer fixture; inert artifacts, never real data."""
import json
from pathlib import Path
import sys
from unittest.mock import patch

if len(sys.argv) not in (2,3) or (len(sys.argv)==3 and sys.argv[2] not in ('after-reboot','expired-terminal')):raise ValueError('FreshSyntheticRootRequired')
root=Path(sys.argv[1]).absolute()
if root.exists() or any(p.is_symlink() or p.is_junction() for p in root.parents):raise ValueError('FreshSyntheticRootRequired')
root.mkdir();owned=root/'fixture';owned.mkdir();sys.path.insert(0,str(Path(__file__).resolve().parent))
from test_sentence_replan_cli import SentenceCliTests
class OwnedDirectory:
    name=str(owned)
    def cleanup(self):pass
owner=SentenceCliTests()
with patch('test_gate_cli.tempfile.TemporaryDirectory',return_value=OwnedDirectory()):prepared=owner.prepare(whole_end_after=45 if len(sys.argv)==3 and sys.argv[2]=='expired-terminal' else None)
f,old,old_scope=prepared[:3];before=prepared[-2]
deadline=old_scope['endUtc']
if len(sys.argv)==3:
    from test_sentence_replan_cli import synthetic_reboot,synthetic_expired_window
    deadline=synthetic_expired_window(f,old) if sys.argv[2]=='expired-terminal' else synthetic_reboot(f,old)
print(json.dumps(dict(root=str(root),package=str(Path(f.config['CandidateApplicationDirectory']).parent),target=str(Path(f.config['ApplicationDirectory']).parent),
    profile=str(f.f.b.f.profile),gatePath=str(f.config_path),gateSha256=f.pin,scopeSha256=f.config['ScopeSha256'],handoffPath=str(f.h_path),handoffSha256=f.h_pin,
    handoffId=f.h['TransitionId'],previousGatePath=f.h['PreviousConfigPath'],previousGateSha256=f.h['PreviousGateSha256'],previousScopeSha256=f.h['PreviousScopeSha256'],
    expectedRefreshes=before['refresh'],expectedAdmissions=before['admissions'],deadlineUtc=deadline,databasePath=str(f.f.db),databaseSha256=__import__('hashlib').sha256(f.f.db.read_bytes()).hexdigest(),providerCalls=0,syntheticOnly=True)))
