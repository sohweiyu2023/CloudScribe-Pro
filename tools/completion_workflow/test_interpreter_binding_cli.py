"""Actual isolated bridge repair, including partial metadata writes and replay."""
import json
from pathlib import Path
import unittest
import completion_register as m
import interpreter_test_fixture as fixture

class InterpreterCliTests(unittest.TestCase):
    def test_actual_pinned_repair_partial_readback_restart_and_tamper(self):
        f=fixture.prepare(self);t=fixture.repair(f)
        def call(verb,success=True):
            result=fixture.command(f,t,verb)
            self.assertEqual(success,result[0]==0,result)
            return result
        old_events=Path(f.f.config['RegisterPath']).read_bytes()
        call('status',False)  # old interpreter configuration is not silently trusted
        original=t['evidencePath'].read_bytes()
        t['evidencePath'].write_bytes(original+b' ');call('inspect-build',False)
        t['evidencePath'].write_bytes(original)
        call('inspect-build');call('check-build',False)
        call('claim-build');call('claim-build',False);call('check-build')
        # An actual interruption after only the config write remains owned by the
        # exact pending claim. Strict completion cannot succeed on mixed markers.
        target=Path(f.config['ApplicationDirectory']).parent
        (target/'completion-workflow-gate.json').write_bytes(f.config_path.read_bytes())
        call('check-build');call('complete-build',False)
        marker_path=target/'cloudscribe-install.json';marker=json.loads(marker_path.read_text())
        marker['CompletionWorkflowGateSha256']=f.pin;marker_path.write_text(json.dumps(marker))
        call('check-build');call('complete-build',False)
        required=Path(f.f.b.f.profile)/'.completion-workflow-required.json'
        required.write_text(json.dumps(dict(GateSha256=f.pin,ScopeSha256=f.config['ScopeSha256'])))
        call('check-build');call('complete-build');call('complete-build',False)
        result=call('status')[1]
        self.assertEqual((0,0,None),(result['refreshes'],result['totalAdmissions'],result['pendingId']))
        self.assertEqual(old_events,Path(f.f.config['RegisterPath']).read_bytes())
        # Ordinary post-repair status and ancestor validation work; changed
        # interpreter/evidence/config cannot acquire a second repair.
        call('inspect-build',False)
        with m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])._open() as c:
            state=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])._read(c)
        self.assertEqual(1,state['helperSourceRepairs'])
        self.assertEqual(f.scope['endUtc'],state['scope']['endUtc'])

if __name__=='__main__':unittest.main()
