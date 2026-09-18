"""Compare two local BeaverBuddies rolling diagnostic ZIPs (or report.json files)."""
import argparse
import json
from pathlib import Path
from zipfile import ZipFile, is_zipfile

MAX_REPORT_BYTES = 32 * 1024 * 1024


def read_report(path):
    if is_zipfile(path):
        with ZipFile(path) as archive:
            if archive.getinfo("report.json").file_size > MAX_REPORT_BYTES:
                raise ValueError("Report exceeds size limit")
            data = archive.read("report.json")
    else:
        with open(path, "rb") as source:
            data = source.read(MAX_REPORT_BYTES + 1)
    if len(data) > MAX_REPORT_BYTES:
        raise ValueError("Report exceeds size limit")
    report = json.loads(data)
    if report.get("schema") != 1:
        raise ValueError("Unsupported report schema")
    return report


def compare(left, right):
    a, b = left["metadata"], right["metadata"]
    if not a.get("snapshotSha256") or a.get("snapshotSha256") != b.get("snapshotSha256"):
        return {"status": "unrelated", "message": "Snapshot identities differ or are unavailable; use reports from the same loaded session."}
    if a.get("version") != b.get("version"):
        return {"status": "different-builds", "message": "Mod versions differ; install matching builds before comparing simulation state."}
    left_ticks = {s["tick"]: s for s in left["snapshots"]}
    right_ticks = {s["tick"]: s for s in right["snapshots"]}
    common = sorted(left_ticks.keys() & right_ticks.keys())
    if not common:
        return {"status": "no-overlap", "message": "No shared sampled ticks remain in these reports."}
    for tick in common:
        x, y = left_ticks[tick], right_ticks[tick]
        changes = [key for key in ("rng", "entityOrder", "positions", "entityCount", "waterColumnCount", "waterStride") if x.get(key) != y.get(key)]
        xe = {e["id"]: e for e in x.get("entities", [])}
        ye = {e["id"]: e for e in y.get("entities", [])}
        if x.get("entityCount") == y.get("entityCount") and x.get("entityWindowStart") is not None and x.get("entityWindowStart") == y.get("entityWindowStart"):
            if list(xe) != list(ye):
                changes.append("entitySampleIdsOrOrder")
        entity_changes = {}
        for entity in sorted(xe.keys() & ye.keys()):
            fields = [f for f in ("job", "inventory", "truncated") if xe[entity].get(f) != ye[entity].get(f)]
            if fields:
                entity_changes[entity] = fields
        xw = {s[0]: s[1:] for s in x.get("water", [])}
        yw = {s[0]: s[1:] for s in y.get("water", [])}
        water_changes = [i for i in sorted(xw.keys() & yw.keys()) if xw[i] != yw[i]]
        if changes or entity_changes or water_changes:
            return {"status": "sampled-divergence", "firstSampledTick": tick, "categories": changes,
                    "entities": entity_changes, "waterColumns": water_changes,
                    "overlap": {"entities": len(xe.keys() & ye.keys()), "water": len(xw.keys() & yw.keys())},
                    "recentCommands": {"left": nearby(left, tick), "right": nearby(right, tick)},
                    "message": "Earliest differing shared sample, not proof of the original cause. State between checkpoints and unsampled objects may differ earlier."}
    return {"status": "samples-match", "firstTick": common[0], "lastTick": common[-1], "sharedCheckpoints": len(common),
            "message": "Shared samples match. This does not rule out divergence outside the sampled objects or ticks. Network backlog is informational and excluded."}


def nearby(report, tick):
    return [event for event in report.get("events", []) if event.get("tick") is not None and tick - 20 <= event["tick"] <= tick + 20][-12:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", type=Path)
    parser.add_argument("guest", type=Path)
    args = parser.parse_args()
    print(json.dumps(compare(read_report(args.host), read_report(args.guest)), indent=2))


if __name__ == "__main__":
    main()
