"""Explicit terminal-mode exit: sealed synthetic journal, files, no provider."""
import json
from pathlib import Path
import tempfile
import unittest
import uuid
import completion_register as m
import maintenance_operator as operator
import pending_install_files as files
import pending_install_operator as parser
import test_stopped_maintenance as fixture


class MaintenanceReleaseTests(unittest.TestCase):
    def test_each_release_interruption_remains_blocked_until_durable_retirement(self):
        for boundary in ('release-claimed', 'release-marker', 'release-retired', 'release-detached'):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); f = fixture.StoppedMaintenanceTests(); f.setUp()
                try:
                    f.action('claim'); f.action('complete'); state = f.state()
                    target = root / 'installed'; prepared = root / 'prepared'; profile = root / 'profile'
                    for path in (target, prepared, profile): path.mkdir()
                    marker = {'Product': 'CloudScribe Pro', 'Profile': str(profile), 'AppSha256': 'a' * 64,
                        'CompletionWorkflowGateSha256': 'b' * 64, 'CompletionGateProtocolVersion': 1,
                        'ChirpArticlePolicySha256': 'c' * 64}
                    for path in (target, prepared):
                        (path / 'cloudscribe-install.json').write_text(json.dumps(marker)); (path / 'app.dll').write_bytes(b'synthetic unchanged binary')
                    required = profile / '.completion-workflow-required.json'; required.write_text(json.dumps({'GateSha256': 'b' * 64, 'ScopeSha256': f.r.scope_sha}))
                    release_id = str(uuid.uuid4()); archive = required.with_name(required.name + '.' + release_id + '.retired')
                    manual = {key: value for key, value in marker.items() if key not in ('CompletionWorkflowGateSha256', 'CompletionGateProtocolVersion')}
                    manual_path = root / 'reviewed-manual-marker.json'; manual_path.write_text(json.dumps(manual))
                    inventory = files.inventory(target); inventory['cloudscribe-install.json'] = files.sha(manual_path)
                    record = dict(SchemaVersion=1, ReleaseId=release_id, ReleaseApprovalSha256=m.digest({'synthetic': 'fresh explicit manual-mode release'}),
                        MaintenanceIdentitySha256=f.pin, ScopeSha256=f.r.scope_sha, ExpectedHeadSha256=state['head'],
                        ManualInventorySha256=m.digest(inventory), RequiredArchivePathSha256=m.path_digest(archive), AdministrativeOnly=True, PaidAuthority=False)
                    record_path = root / 'reviewed-release.json'; record_path.write_bytes(m.canonical(record).encode())
                    ref = lambda path: {'Path': str(path), 'Sha256': files.sha(path)}
                    plan = dict(Release=ref(record_path), Target=str(target), Required=str(required), RequiredArchive=str(archive),
                        ManualMarker=ref(manual_path), ManualInventory=inventory, PreparedRoot=str(prepared), NewInventory=files.inventory(target),
                        NewRequiredSha256=files.sha(required))
                    modules = {'completion_register.py': m, 'pending_install_operator.py': parser, 'pending_install_files.py': files}
                    def fail(stage):
                        if stage == boundary: raise RuntimeError('synthetic interruption')
                    proof_count = []
                    evidence = lambda: proof_count.append(True)
                    self.assertTrue(operator.release_files(plan, modules, f.r, evidence, 'inspect-release')['valid'])
                    with self.assertRaises(RuntimeError): operator.release_files(plan, modules, f.r, evidence, 'release', fail)
                    intermediate = f.state()
                    if not intermediate.get('maintenanceRelease', {}).get('completed'):
                        self.assertTrue(required.exists(), 'Required marker must block startup before retirement')
                    result = operator.release_files(plan, modules, f.r, evidence, 'resume-release')
                    self.assertTrue(result['manualMode']); self.assertFalse(result['authority']); self.assertEqual(0, result['providerRequests'])
                    self.assertFalse(required.exists()); self.assertTrue(archive.exists()); self.assertEqual(inventory, files.inventory(target))
                    self.assertEqual('c' * 64, json.loads((target / 'cloudscribe-install.json').read_bytes())['ChirpArticlePolicySha256'])
                    final = f.state(); self.assertTrue(final['retired']); self.assertTrue(final['stopped']); self.assertTrue(proof_count)
                    for key in ('scope', 'refresh', 'admissions', 'resume-admission', 'lastGoodUtc', 'diagnosticDispatch'):
                        self.assertEqual(f.before[key], final[key], key)
                    with self.assertRaises(m.Rejected): f.r.status()
                    with self.assertRaises(ValueError): operator.release_files(plan, modules, f.r, evidence, 'release')
                finally: f.doCleanups()


if __name__ == '__main__': unittest.main()
