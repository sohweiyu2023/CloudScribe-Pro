import json
from pathlib import Path
import tempfile
import unittest

from review_google_structure_history import review


class HistoryReviewTests(unittest.TestCase):
    def test_restart_join_preserves_structural_outcome_and_flags_without_free_text(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-history-synthetic-") as root:
            folder = Path(root)
            correlation = "a" * 32
            key = "b" * 64
            def event(eid, **fields):
                return {"EventId": eid, "TimestampUnixMilliseconds": 1000 + eid, "ApplicationVersion": "1.0.1",
                        "ModuleId": "00000000-0000-0000-0000-000000000001", "FieldsComplete": True,
                        "Message": "PRIVATE_ARTICLE_CREDENTIAL_CANARY", "SafeFields": {"Correlation": correlation, **fields}}
            first = [event(1613, IdempotencyKeySha256=key, Ordinal="25"),
                     event(1617, IdempotencyKeySha256=key, Ordinal="25", SplitDepth="1", AuthorizationId="00000000-0000-0000-0000-000000000002"),
                     event(1614, IdempotencyKeySha256=key),
                     event(1616, VoiceName="cmn-CN-Chirp3-HD-Alnilam", InputMode="Ssml", InputUtf8Bytes="819",
                           SourceScalars="275", Sentences="5", MaximumSentenceScalars="101", MaximumSentenceUtf8Bytes="303",
                           MedianSentenceScalars="71", P95SentenceScalars="101", SplitDepth="-1", Histogram="2,0,3,0,0",
                           ExplicitSentenceMarkers="true", Complete="true")]
            second = [event(1607, MessageHint="SentenceLengthReported"), event(1611, HttpStatus="400", ElapsedMilliseconds="243"),
                      event(1609, Retention="SavedPrivately", Truncated="false")]
            (folder / "first.jsonl").write_text("\n".join(map(json.dumps, first)) + "\nnot-json\n[]", encoding="utf-8")
            (folder / "second.jsonl").write_text("\n".join(map(json.dumps, second)), encoding="utf-8")
            result = review(folder)
            item = result["Requests"][0]
            self.assertEqual((item["Ordinal"], item["Sentences"], item["MaximumSentenceScalars"]), (25, 5, 101))
            self.assertEqual(item["SplitDepth"], 1)
            self.assertEqual((item["HttpStatus"], item["ErrorHint"], item["CaptureStatus"]), (400, "SentenceLengthReported", "SavedPrivately"))
            self.assertEqual(result["Coverage"]["MalformedLines"], 2)
            self.assertFalse(result["AutomaticTuning"])
            self.assertNotIn("PRIVATE_", json.dumps(result))
            self.assertEqual(result["Groups"][0]["Count"], 1)
            self.assertEqual(result["Groups"][0]["ModuleId"], "00000000-0000-0000-0000-000000000001")

    def test_unknown_structure_and_history_bounds_are_explicit(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-history-synthetic-") as root:
            folder = Path(root)
            row = {"EventId": 1611, "TimestampUnixMilliseconds": 5, "SafeFields": {"Correlation": "c" * 32, "HttpStatus": "400"}}
            for i in range(3):
                (folder / f"{i}.jsonl").write_text(json.dumps(row), encoding="utf-8")
            result = review(folder, max_files=1)
            self.assertEqual(result["Coverage"]["OmittedFiles"], 2)
            self.assertEqual(result["Coverage"]["RequestsWithoutCompleteStructure"], 1)
            self.assertEqual(result["Groups"], [])

    def test_durable_identity_join_keeps_deeper_recovery_depths(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-history-deep-synthetic-") as root:
            folder = Path(root)
            rows = []
            for index, depth in enumerate((0, 3, 8)):
                correlation, key = f"{index + 1:032x}", f"{index + 101:064x}"
                for event_id, fields in (
                    (1617, {"IdempotencyKeySha256": key, "Ordinal": str(index + 1), "SplitDepth": str(depth)}),
                    (1614, {"IdempotencyKeySha256": key}),
                    (1616, {"SplitDepth": "-1", "Complete": "true"}),
                    (1611, {"HttpStatus": "200"}),
                ):
                    rows.append({"EventId": event_id, "TimestampUnixMilliseconds": len(rows),
                                 "FieldsComplete": True, "SafeFields": {"Correlation": correlation, **fields}})
            (folder / "google-trend-history-v1.jsonl").write_text("\n".join(map(json.dumps, rows)), encoding="utf-8")
            result = review(folder)
            self.assertEqual(sorted((x["Ordinal"], x["SplitDepth"]) for x in result["Requests"]), [(1, 0), (2, 3), (3, 8)])
            self.assertIn("no automatic age/count rotation", result["Coverage"]["SourceRetention"])
            self.assertIn("bounded scan", result["Coverage"]["SourceRetention"])

    def test_production_utc_text_preserves_timeline_and_does_not_assume_naive_timezone(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-history-time-synthetic-") as root:
            folder = Path(root)
            rows = [
                {"EventId": 1607, "TimestampUtc": "1970-01-01T08:00:00.1250000+08:00", "SafeFields": {"Correlation": "a" * 32}},
                {"EventId": 1611, "TimestampUtc": "1970-01-01T00:00:01.500Z", "SafeFields": {"Correlation": "a" * 32, "HttpStatus": "400"}},
                {"EventId": 1611, "TimestampUtc": "2026-11-05T01:00:00", "SafeFields": {"Correlation": "b" * 32, "HttpStatus": "200"}},
            ]
            (folder / "google-trend-history-v1.jsonl").write_text("\n".join(map(json.dumps, rows)), encoding="utf-8")
            result = review(folder)
            timed = next(x for x in result["Requests"] if x["Correlation"] == "a" * 32)
            self.assertEqual((timed["FirstObservedUnixMilliseconds"], timed["LastObservedUnixMilliseconds"]), (125, 1500))
            self.assertEqual(result["Coverage"]["ObservedFirstUnixMilliseconds"], 125)
            self.assertEqual(result["Coverage"]["ObservedLastUnixMilliseconds"], 1500)
            self.assertEqual(result["Coverage"]["RequestsWithoutTimeline"], 1)

    def test_permanent_component_identity_separates_builds_with_the_same_version(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-history-build-synthetic-") as root:
            folder = Path(root)
            rows = []
            for index in (1, 2):
                rows.append({"EventId": 1616, "TimestampUtc": "2026-11-05T17:00:00Z",
                             "ApplicationVersion": "1.0.0.0", "ComponentBuildId": f"00000000-0000-0000-0000-{index:012x}",
                             "ModuleId": "00000000-0000-0000-0000-000000000099", "FieldsComplete": True,
                             "SafeFields": {"Correlation": f"{index:032x}", "VoiceName": "cmn-CN-Chirp3-HD-Alnilam",
                                            "InputMode": "Ssml", "MaximumSentenceScalars": "100", "Complete": "true"}})
            (folder / "google-trend-history-v1.jsonl").write_text("\n".join(map(json.dumps, rows)), encoding="utf-8")
            result = review(folder)
            self.assertEqual(len(result["Groups"]), 2)
            self.assertEqual({x["ModuleId"] for x in result["Groups"]},
                             {"00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002"})
            self.assertEqual(result["Coverage"]["RequestsWithoutBuildIdentity"], 0)


if __name__ == "__main__":
    unittest.main()
