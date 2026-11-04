"""Real staged file replacement plus inert sealed journals; no live profile or provider."""
from pathlib import Path
import hashlib
import tempfile
import unittest
import pending_install_files as files
import test_successor_withdrawal as core
import completion_register as m


class Interruption(RuntimeError):
    pass


class PendingInstallationTests(unittest.TestCase):
    def setUp(self):
        self.f=core.SuccessorWithdrawalTests();self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.target=self.root/'installed';self.backup=self.root/'retained-predecessor'
        for root,tag in ((self.target,b'new'),(self.backup,b'old')):
            (root/'app').mkdir(parents=True)
            (root/'app/CloudScribe.dll').write_bytes(tag+b'app')
            (root/'app/CloudScribe.Infrastructure.dll').write_bytes(tag+b'infrastructure')
            (root/'completion-workflow-gate.json').write_bytes(tag+b'gate')
            (root/'cloudscribe-install.json').write_bytes(tag+b'marker')
        profile=self.root/'profile';profile.mkdir();self.required=profile/'.completion-workflow-required.json'
        self.required.write_bytes(b'new required');self.saved=self.root/'old-required.json';self.saved.write_bytes(b'old required')
        self.database=profile/'database.inert';self.database.write_bytes(b'synthetic retained 174 cents and 23 media records')
        self.database_pin=files.sha(self.database);self.old=files.inventory(self.backup);self.new=files.inventory(self.target)
        self.calls=[]

    def evidence(self):
        if files.sha(self.database)!=self.database_pin:raise ValueError('RecoveryFinancialEvidence')

    def journal(self,phase):
        self.calls.append(phase)
        return self.f.action(phase)

    def recovery(self,fault=lambda _:None):
        return files.Restoration(self.target,self.backup,self.root/'stage',self.root/'retained-incomplete',
            self.required,self.saved,self.old,self.new,files.sha(self.saved),hashlib.sha256(b'new required').hexdigest(),
            self.f.record['WithdrawalId'],self.journal,self.evidence,fault)

    def test_exact_files_restored_only_after_claim_and_financial_history_unchanged(self):
        before=self.f.child
        result=self.recovery().restore()
        self.assertFalse(result['authority']);self.assertEqual(0,result['providerRequests'])
        self.assertEqual('claim',self.calls[0]);self.assertEqual('complete',self.calls[-1])
        self.assertEqual(self.old,files.inventory(self.target));self.assertEqual(self.old,files.inventory(self.backup))
        self.assertEqual(self.new,files.inventory(self.root/'retained-incomplete'))
        self.evidence()
        with self.f.new._open() as db:self.assertEqual(before,self.f.new._read(db))
        with self.f.old._open() as db:s=self.f.old._read(db)
        self.assertIsNone(s['pending']);self.assertTrue(s['stopped']);self.assertFalse(s['retired'])

    def test_every_interruption_restarts_only_same_claim_without_duplicate_effects(self):
        # Each distinct filesystem boundary is exercised with a fresh sealed synthetic journal.
        for boundary in ('claimed','staged','displaced','restored-installation','restored-required'):
            with self.subTest(boundary=boundary):
                case=PendingInstallationTests();case.setUp()
                try:
                    def fault(stage):
                        if stage==boundary:raise Interruption(stage)
                    with self.assertRaises(Interruption):case.recovery(fault).restore()
                    with self.assertRaises((m.Rejected,ValueError)):case.recovery().restore() # No second claim.
                    case.recovery().restore(resume=True)
                    self.assertEqual(2 if boundary=='claimed' else 1,case.calls.count('claim'))
                    self.assertEqual(case.old,files.inventory(case.target));case.evidence()
                finally:case.doCleanups()

    def test_changed_ledger_or_backup_or_identity_never_claims(self):
        self.database.write_bytes(b'changed financial state')
        with self.assertRaisesRegex(ValueError,'RecoveryFinancialEvidence'):self.recovery().restore()
        self.assertEqual([],self.calls)
        self.database.write_bytes(b'synthetic retained 174 cents and 23 media records')
        (self.backup/'app/CloudScribe.dll').write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError,'RecoveryBackupChanged'):self.recovery().restore()
        self.assertEqual([],self.calls)

    def test_old_controls_with_new_binary_cannot_complete_readback(self):
        def fault(stage):
            if stage=='restored-installation':
                (self.target/'app/CloudScribe.dll').write_bytes(b'newapp')
        with self.assertRaisesRegex(ValueError,'RecoveryReadback'):self.recovery(fault).restore()
        self.assertNotIn('complete',self.calls)
        with self.f.old._open() as db:self.assertEqual('successor-withdraw-claim',self.f.old._read(db)['pending']['type'])

    def test_path_overlap_traversal_and_unreviewed_files_reject(self):
        with self.assertRaisesRegex(ValueError,'RecoveryInventory'):files.validate_inventory({'../escape':'a'*64})
        (self.target/'unreviewed').write_bytes(b'unknown')
        with self.assertRaisesRegex(ValueError,'RecoveryInitialState'):self.recovery().restore()
        self.assertEqual([],self.calls)

    def test_missing_or_false_claim_confirmation_has_no_filesystem_effect(self):
        for result in (None,False,{}, {'outcome':'Succeeded','authority':True}, {'outcome':'Failed','authority':False}):
            with self.subTest(result=result):
                r=self.recovery();r.journal=lambda _:result
                with self.assertRaisesRegex(ValueError,'RecoveryJournalNotConfirmed'):r.restore()
                self.assertEqual(self.new,files.inventory(self.target))
                self.assertFalse(r.stage.exists());self.assertFalse(r.displaced.exists())

    def test_false_check_stops_before_swap_and_unconfirmed_complete_never_reports_success(self):
        r=self.recovery()
        def reject_check(phase):
            return {'valid':False,'authority':False} if phase=='check' else self.journal(phase)
        r.journal=reject_check
        with self.assertRaisesRegex(ValueError,'RecoveryJournalNotConfirmed'):r.restore()
        self.assertEqual(self.new,files.inventory(self.target));self.assertFalse(r.displaced.exists())
        r=self.recovery()
        def reject_complete(phase):
            return None if phase=='complete' else self.journal(phase)
        r.journal=reject_complete
        with self.assertRaisesRegex(ValueError,'RecoveryJournalNotConfirmed'):r.restore(resume=True)
        with self.f.old._open() as db:self.assertEqual('successor-withdraw-claim',self.f.old._read(db)['pending']['type'])

    def test_financial_change_after_claim_preserves_pending_and_blocks_swap(self):
        def fault(stage):
            if stage=='staged':self.database.write_bytes(b'changed after claim')
        with self.assertRaisesRegex(ValueError,'RecoveryFinancialEvidence'):self.recovery(fault).restore()
        self.assertEqual(self.new,files.inventory(self.target));self.assertNotIn('complete',self.calls)


if __name__=='__main__':unittest.main()
