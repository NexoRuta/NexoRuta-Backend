"""Unit tests for the integration shard planner."""

import importlib.util
import json
from pathlib import Path
import unittest

MODULE_PATH = Path(__file__).with_name("integration_shards.py")
spec = importlib.util.spec_from_file_location("integration_shards", MODULE_PATH)
shards = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shards)


class IntegrationShardTests(unittest.TestCase):
    def test_duration_history_manifest_is_available_and_matches_three_shards(self):
        self.assertTrue(shards.MANIFEST.is_file())
        history = json.loads(shards.MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(history["schema_version"], 1)
        self.assertEqual(history["shard_count"], 3)
        self.assertTrue(history["classes"])

    def test_discovered_cases_are_assigned_once_and_new_classes_are_included(self):
        cases = [
            "NexoRuta.IntegrationTests.FastTests.ReadsData",
            "NexoRuta.IntegrationTests.FastTests.WritesData",
            "NexoRuta.IntegrationTests.SlowTests.UsesTerritory",
            "NexoRuta.IntegrationTests.NewTests.AddedAfterBaseline",
        ]
        history = {"classes": {
            "NexoRuta.IntegrationTests.FastTests": {"duration_ms": 20, "test_cases": 2},
            "NexoRuta.IntegrationTests.SlowTests": {"duration_ms": 100, "test_cases": 1},
        }}
        buckets, _, counts = shards.make_shards(cases, history, 2)
        owner = {name: index for index, bucket in enumerate(buckets) for name in bucket}
        self.assertEqual(len(owner), 3)
        self.assertEqual(set(owner), set(counts))
        self.assertEqual(owner["NexoRuta.IntegrationTests.SlowTests"], 0)
        self.assertEqual(owner["NexoRuta.IntegrationTests.NewTests"], 1)

    def test_test_list_parser_requires_the_discovery_marker(self):
        self.assertEqual(shards.parse_test_list("The following Tests are available:\n    NexoRuta.IntegrationTests.A.B\n"),
                         ["NexoRuta.IntegrationTests.A.B"])
        with self.assertRaises(ValueError):
            shards.parse_test_list("No tests found")


if __name__ == "__main__":
    unittest.main()
