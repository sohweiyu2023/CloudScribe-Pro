"""Safe H8 failure location and code, without private exception text."""
import unittest
import completion_register as m
import gate_cli as g
import test_released_diagnostic_successor as fixture


class ReleasedFailureEvidenceTests(unittest.TestCase):
    def test_extension_failure_preserves_safe_component_and_known_code(self):
        case = fixture.ReleasedDiagnosticSuccessorTests(); case.setUp(); self.addCleanup(case.doCleanups)
        try:
            m._successor('released_baseline', dict(case.before, retired=False), case.h)
        except m.Rejected as error:
            packet = g.rejection_packet(error, {'ReleasedDiagnosticState': 'SuccessorScopeMismatch'})
        self.assertEqual('successor', packet['failureComponent'])
        self.assertEqual('ReleasedDiagnosticState', packet['code'])
        self.assertTrue(1 <= packet['failureSourceLine'] <= 10000)
        self.assertNotIn(str(__file__), str(packet))
