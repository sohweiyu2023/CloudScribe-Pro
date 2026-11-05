"""Focused actual-row guard failures using in-memory synthetic SQLite only."""
import copy
from datetime import datetime, timezone
import json
import sqlite3
import unittest
import uuid

import completion_register as m
import gate_cli as g


class ReservedDiagnosticRowsTests(unittest.TestCase):
    def setUp(self):
        self.db=sqlite3.connect(':memory:');self.addCleanup(self.db.close);self.db.row_factory=sqlite3.Row
        self.db.executescript('''
            CREATE TABLE chirp_reserved_catalog_mappings(AuthorizationId TEXT,MappingId TEXT,PolicyJson TEXT,PolicySha256 TEXT,CreatedAtUnixMilliseconds INTEGER,PriorMappingSha256 TEXT);
            CREATE TABLE chirp_reserved_dispatch_mappings(AuthorizationId TEXT,MappingId TEXT,PolicySha256 TEXT,ClaimedLocalAttemptId TEXT,ClaimedAtUnixMilliseconds INTEGER);
        ''')
        self.auth=uuid.uuid4().hex
        self.child=dict(AuthorizationId=self.auth,Consumed=0,ClaimedLocalAttemptId=None,ApprovedAtUnixMilliseconds=1000,EnvelopeJson='{"synthetic":"exact envelope"}')
        self.queue=dict(SubmissionAttempted=0,UnresolvedSubmission=0,LocalAttemptId=None,DiagnosticCode=None,
            PayloadSha256=None,PayloadBytes=None,OutcomeObservedAtUnixMilliseconds=None,ProviderRequestId=None,ErrorStatus=0)
        self.r=dict(authorizationId=self.auth,ledgerEvidenceSha256=m.digest(self.child),queueEvidenceSha256=m.digest(self.queue),cleanQueueObservedAtUnixMilliseconds=None)

    def check(self,dispatch=False,policy=None,pin=None):
        return g.verify_reserved_diagnostic_rows(self.db,m,self.r,self.child,self.queue,dispatch,policy,pin,{}, {}, {})

    def claim(self):
        attempt=uuid.uuid4().hex;mapping=uuid.uuid4().hex
        stamp=lambda x:datetime.fromtimestamp(x/1000,timezone.utc).isoformat().replace('+00:00','Z')
        p=dict(MappingId=str(uuid.UUID(mapping)),OriginalApprovedAtUtc=stamp(1000),OriginalEnvelope=json.loads(self.child['EnvelopeJson']),
            ExpiresAtUtc=stamp(9000),CatalogExpiresAtUtc=stamp(10000))
        raw=json.dumps(p,separators=(',',':'));pin=g.sha(raw.encode())
        self.db.execute('INSERT INTO chirp_reserved_catalog_mappings VALUES(?,?,?,?,?,?)',(self.auth,mapping,raw,pin,2000,None))
        self.db.execute('INSERT INTO chirp_reserved_dispatch_mappings VALUES(?,?,?,?,?)',(self.auth,mapping,pin,attempt,3001))
        self.child.update(Consumed=1,ClaimedLocalAttemptId=attempt)
        self.queue.update(SubmissionAttempted=1,UnresolvedSubmission=1,LocalAttemptId=attempt,DiagnosticCode='google-dispatch-claimed',
            PayloadSha256='a'*64,PayloadBytes=618,OutcomeObservedAtUnixMilliseconds=3000)
        return p,pin

    def test_clean_reservation_and_real_timestamp_claim_are_verified(self):
        self.check()
        p,pin=self.claim();self.check(True,p,pin)
        self.assertEqual(1,self.child['Consumed'])

    def test_changed_hold_unknown_outcome_and_extra_queue_detail_fail_closed(self):
        for field,value in [('SubmissionAttempted',1),('UnresolvedSubmission',1),('ProviderRequestId','synthetic-result'),
                            ('OutcomeObservedAtUnixMilliseconds',5),('ErrorStatus',1)]:
            saved=copy.deepcopy(self.queue);self.queue[field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):self.check()
            self.queue=saved
        self.child['unreviewed']='unexpected'
        with self.assertRaises(ValueError):self.check()

    def test_reordered_timestamp_and_changed_historical_time_or_envelope_reject(self):
        p,pin=self.claim()
        for value in (None,1999,3002):
            self.queue['OutcomeObservedAtUnixMilliseconds']=value
            with self.subTest(timestamp=value),self.assertRaises(ValueError):self.check(True,p,pin)
        self.queue['OutcomeObservedAtUnixMilliseconds']=3000
        for field,value in [('ApprovedAtUnixMilliseconds',1001),('EnvelopeJson','{"synthetic":"changed"}')]:
            saved=self.child[field];self.child[field]=value
            # Re-pin ledger to isolate the historical binding guard itself.
            normalized=dict(self.child,Consumed=0,ClaimedLocalAttemptId=None);old=self.r['ledgerEvidenceSha256'];self.r['ledgerEvidenceSha256']=m.digest(normalized)
            with self.subTest(field=field),self.assertRaises(ValueError):self.check(True,p,pin)
            self.child[field]=saved;self.r['ledgerEvidenceSha256']=old

    def test_foreign_mapping_and_duplicate_claim_are_rejected(self):
        p,pin=self.claim()
        self.db.execute('UPDATE chirp_reserved_catalog_mappings SET PolicySha256=?',('b'*64,))
        with self.assertRaises(ValueError):self.check(True,p,pin)

        self.db.execute('UPDATE chirp_reserved_catalog_mappings SET PolicySha256=?',(pin,))
        self.db.execute('INSERT INTO chirp_reserved_dispatch_mappings SELECT * FROM chirp_reserved_dispatch_mappings')
        with self.assertRaises(ValueError):self.check(True,p,pin)

    def test_predecessor_admission_and_approval_must_match_retained_history(self):
        before=dict(diagnosticPolicySha256='a'*64,scope=dict(approvalSha256='b'*64))
        manifest=dict(diagnostic=dict(reservedResume=dict(historicalPolicySha256='a'*64,originalApprovalSha256='b'*64)))
        g.verify_reserved_diagnostic_provenance(before,manifest)
        for key in ('historicalPolicySha256','originalApprovalSha256'):
            changed=copy.deepcopy(manifest);changed['diagnostic']['reservedResume'][key]='c'*64
            with self.subTest(key=key),self.assertRaisesRegex(ValueError,'DiagnosticHistory'):
                g.verify_reserved_diagnostic_provenance(before,changed)


if __name__=='__main__':unittest.main()
