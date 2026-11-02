"""Creates synthetic withdrawal evidence only inside an existing caller-owned test root."""
import json
from pathlib import Path
import sys
import uuid
sys.path.insert(0,str(Path(__file__).resolve().parent))
import completion_register as m
import gate_cli

if len(sys.argv)!=6:raise ValueError('SyntheticArgumentsRequired')
root=Path(sys.argv[1]).absolute();config_path=Path(sys.argv[2]).absolute();repair_path=Path(sys.argv[3]).absolute();backup=Path(sys.argv[4]).absolute();out=Path(sys.argv[5]).absolute()
if not root.name.startswith('cloudscribe-interpreter-tests-') or any(not p.is_relative_to(root) for p in (config_path,repair_path,backup,out)):raise ValueError('SyntheticRootRequired')
config=json.loads(gate_cli.bounded(config_path,32768));repair=json.loads(gate_cli.bounded(repair_path,32768))
for key in ('RegisterPath','ApplicationDirectory','PlanManifestPath'):
    if not Path(config[key]).is_relative_to(root):raise ValueError('SyntheticPathRequired')
r=m.Register(config['RegisterPath'],config['ScopeSha256'])
with r._open() as db:state=r._read(db)
record=dict(SchemaVersion=1,WithdrawalId=str(uuid.uuid4()),WithdrawalApprovalSha256=m.digest({'synthetic':'separate terminal withdrawal approval'}),RepairTransitionId=repair['TransitionId'],RepairIdentitySha256=gate_cli.sha(repair_path.read_bytes()),ScopeSha256=r.scope_sha,ExpectedHeadSha256=state['head'],PreviousGateSha256=repair['NewGateSha256'],RestoredGateSha256=repair['PreviousGateSha256'],BackupPath=str(backup),PreviousInstallSha256=gate_cli.sha(gate_cli.bounded(backup/'previous-install.json',32768)),PreviousRequiredSha256=gate_cli.sha(gate_cli.bounded(backup/'previous-required.json',32768)))
with out.open('x',encoding='utf-8') as stream:json.dump(record,stream)
print(json.dumps(dict(Path=str(out),Sha256=gate_cli.sha(out.read_bytes()),Record=record)))
