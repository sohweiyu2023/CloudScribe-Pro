"""Completed local audio keeps native historical identity, never fresh dispatch authority."""
import copy
from contextlib import closing
import json
import sqlite3
import time
import unittest
import uuid
import completion_register as m
import gate_cli as g
import test_continuation_cli as fixtures


class ExactNativeJsonTests(unittest.TestCase):
    def test_raw_order_escapes_and_nested_keys_do_not_recanonicalize_native_policy(self):
        raw='{"Other":{"HistoricalPolicy":1},"HistoricalPolicy":{"z":"\\u7532+\\u002B","a":1},"tail":false}'
        exact=b'{"z":"\\u7532+\\u002B","a":1}'
        self.assertEqual(exact,g.exact_object_property(raw,'HistoricalPolicy'))
        self.assertNotEqual(g.sha(exact),m.digest(json.loads(exact)))
        for invalid in ('[]','{"HistoricalPolicy":{},"HistoricalPolicy":{}}','{"other":1}'):
            with self.subTest(raw=invalid),self.assertRaises(ValueError):g.exact_object_property(invalid,'HistoricalPolicy')


class HistoricalContextTests(unittest.TestCase):
    def test_old_context_only_for_pinned_completed_output_and_all_tampering_rejected(self):
        f=fixtures.ContinuationCliTests();f.setUp();self.addCleanup(f.doCleanups)
        accepted=copy.deepcopy(f.accepted[0]);policy=json.loads((f.f.new_folder/'pilot.json').read_text())
        profile=f.f.b.f.profile;parts={ (x['planSha256'],x['coverageOrdinal']):x for x in f.f.manifest['parts'] }
        plans={x['planSha256']:x['proposalSha256'] for x in f.f.manifest['proposals']}
        with closing(sqlite3.connect(f.f.db)) as c,c:
            c.row_factory=sqlite3.Row
            a=c.execute('SELECT * FROM chirp_article_owned_attempts WHERE AuthorizationId=?',(accepted['authorizationId'],)).fetchone()
            envelope=json.loads(a['EnvelopeJson']);rr=c.execute('SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?',(a['AuthorizationId'],)).fetchone()
            receipt=json.loads(rr['ReceiptJson']);now=int(time.time()*1000);mapping_id=uuid.uuid4().hex
            p=dict(SchemaVersion=1,MappingId=str(uuid.UUID(mapping_id)),AuthorizationId=str(uuid.UUID(a['AuthorizationId'])),
                HistoricalPolicy=policy,AppSha256=f.config['AppSha256'],InfrastructureSha256=f.config['InfrastructureSha256'],ProfileRootSha256=f.config['ProfileRootSha256'],
                SourceApprovalSha256=m.digest({'synthetic':'separate resume approval'}),ExpiresAtUtc=f.scope['endUtc'],CatalogExpiresAtUtc=f.scope['endUtc'],
                OriginalEnvelope=envelope,ExecutionEnvelope=dict(envelope,RequestRevision=envelope['RequestRevision']+1),PriorMappingSha256=None,
                MaximumExistingDispatches=1,MaximumNewReservations=0,MaximumAdditionalReservationMinorUnits=0)
            raw=json.dumps(p);pin=g.sha(raw.encode());receipt['ExecutionMappingSha256']=pin;receipt_raw=json.dumps(receipt)
            accepted.update(receiptSha256=g.sha(receipt_raw.encode()),executionMappingSha256=pin)
            c.execute('INSERT INTO chirp_reserved_catalog_mappings VALUES(?,?,?,?,?,?)',(a['AuthorizationId'],mapping_id,raw,pin,now,None))
            c.execute('INSERT INTO chirp_reserved_dispatch_mappings VALUES(?,?,?,?,?)',(a['AuthorizationId'],mapping_id,pin,a['ClaimedLocalAttemptId'],now+1))
            c.execute('UPDATE chirp_article_receipts SET ReceiptJson=?,ReceiptSha256=? WHERE AuthorizationId=?',(receipt_raw,accepted['receiptSha256'],a['AuthorizationId']))
            c.execute('UPDATE chirp_article_output_journals SET ReceiptJson=?,ReceiptSha256=? WHERE AuthorizationId=?',(receipt_raw,accepted['receiptSha256'],a['AuthorizationId']))
            current=dict(f.config,CredentialReferenceId='synthetic-repaired-reference',AppSha256=m.digest({'synthetic':'new app'}),InfrastructureSha256=m.digest({'synthetic':'new infra'}))
            with self.assertRaisesRegex(ValueError,'ContinuationAcceptedProvenance'):
                g._continuation_member(c,current,m,profile,parts,plans,accepted)
            g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)
            same_reference=dict(current,CredentialReferenceId=f.config['CredentialReferenceId'])
            with self.assertRaisesRegex(ValueError,'PilotMappingIdentity'):
                g._continuation_member(c,same_reference,m,profile,parts,plans,accepted)
            for bad in ('\x7f','\x80','\x9f','\n'):
                changed=dict(envelope,CredentialReferenceId='synthetic'+bad)
                c.execute('UPDATE chirp_article_attempts SET EnvelopeJson=? WHERE AuthorizationId=?',(json.dumps(changed),a['AuthorizationId']))
                with self.assertRaisesRegex(ValueError,'ContinuationAcceptedProvenance'):
                    g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)
            c.execute('UPDATE chirp_article_attempts SET EnvelopeJson=? WHERE AuthorizationId=?',(a['EnvelopeJson'],a['AuthorizationId']))
            c.execute('UPDATE chirp_reserved_catalog_mappings SET PolicyJson=? WHERE AuthorizationId=?',(raw+' ',a['AuthorizationId']))
            with self.assertRaisesRegex(ValueError,'PilotMappingChain'):g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)
            c.execute('UPDATE chirp_reserved_catalog_mappings SET PolicyJson=? WHERE AuthorizationId=?',(raw,a['AuthorizationId']))
            c.execute('UPDATE chirp_reserved_dispatch_mappings SET PolicySha256=? WHERE AuthorizationId=?',(m.digest({'tampered':True}),a['AuthorizationId']))
            with self.assertRaisesRegex(ValueError,'PilotMappingDispatch'):g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)
            c.execute('UPDATE chirp_reserved_dispatch_mappings SET PolicySha256=? WHERE AuthorizationId=?',(pin,a['AuthorizationId']))
            media=profile/'accepted-chirp-article-audio'/(a['AuthorizationId']+'.mp3');before=media.read_bytes();media.write_bytes(b'tamper')
            with self.assertRaisesRegex(ValueError,'ContinuationMediaPin'):g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)
            media.write_bytes(before)
            g._continuation_member(c,current,m,profile,parts,plans,accepted,historical_readback=True)


if __name__=='__main__':unittest.main()
