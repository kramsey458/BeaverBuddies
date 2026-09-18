import copy
import unittest
from compare_rolling_diagnostics import compare


class RollingComparisonTests(unittest.TestCase):
    def report(self):
        return {"schema": 1, "metadata": {"version": "11", "snapshotSha256": "same"},
                "snapshots": [{"tick": 20, "rng": [1, 2, 3, 4], "entities": [{"id": "beaver", "job": "job", "inventory": "stock"}],
                               "water": [[5, True, 100, 0, 0, 0, 1]], "network": {"hash": 1}}], "events": []}

    def test_equal_samples_ignore_network_backlog(self):
        a = self.report(); b = copy.deepcopy(a); b["snapshots"][0]["network"]["hash"] = 99
        self.assertEqual(compare(a, b)["status"], "samples-match")

    def test_first_rng_divergence(self):
        a = self.report(); a["snapshots"].append(dict(a["snapshots"][0], tick=40)); b = copy.deepcopy(a)
        b["snapshots"][1]["rng"] = [4, 3, 2, 1]
        result = compare(a, b)
        self.assertEqual(result["firstSampledTick"], 40)
        self.assertEqual(result["categories"], ["rng"])

    def test_jobs_inventory_and_water_differences(self):
        a = self.report(); b = copy.deepcopy(a)
        b["snapshots"][0]["entities"][0].update(job="other", inventory="other")
        b["snapshots"][0]["water"][0][3] = 20
        result = compare(a, b)
        self.assertEqual(result["entities"], {"beaver": ["job", "inventory"]})
        self.assertEqual(result["waterColumns"], [5])

    def test_unrelated_and_nonoverlapping_reports(self):
        a = self.report(); b = copy.deepcopy(a); b["metadata"]["snapshotSha256"] = "different"
        self.assertEqual(compare(a, b)["status"], "unrelated")
        b = copy.deepcopy(a); b["snapshots"][0]["tick"] = 60
        self.assertEqual(compare(a, b)["status"], "no-overlap")

    def test_disjoint_samples_are_not_false_differences(self):
        a = self.report(); b = copy.deepcopy(a)
        b["snapshots"][0]["water"][0][0] = 50
        b["snapshots"][0]["entities"][0]["id"] = "other-beaver"
        self.assertEqual(compare(a, b)["status"], "samples-match")


if __name__ == "__main__":
    unittest.main()
