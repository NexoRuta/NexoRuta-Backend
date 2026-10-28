"""Run an exact, duration-balanced subset of integration test classes."""

import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ET

MANIFEST = Path(__file__).with_name("integration-shards.json")
TRX_NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def class_name(test_name):
    method = test_name.split("(", 1)[0]
    return method.rsplit(".", 1)[0]


def parse_test_list(output):
    marker = "The following Tests are available:"
    if marker not in output:
        raise ValueError("dotnet test --list-tests did not return the test inventory")
    tests = []
    for line in output.split(marker, 1)[1].splitlines():
        candidate = line.strip()
        if candidate.startswith("NexoRuta.IntegrationTests."):
            tests.append(candidate)
    if not tests:
        raise ValueError("The integration test inventory is empty")
    return tests


def make_shards(test_cases, history, shard_count):
    case_counts = {}
    for test in test_cases:
        cls = class_name(test)
        case_counts[cls] = case_counts.get(cls, 0) + 1

    historical = history.get("classes", {})
    known_per_case = [entry["duration_ms"] / entry["test_cases"]
                      for entry in historical.values() if entry.get("test_cases", 0) > 0]
    fallback = statistics.median(known_per_case) if known_per_case else 1.0
    weights = {}
    for cls, cases in case_counts.items():
        previous = historical.get(cls)
        if previous and previous.get("test_cases", 0) > 0:
            weights[cls] = max(1, round(previous["duration_ms"] * cases / previous["test_cases"]))
        else:
            # New classes still run; case-count weighting is safer than skipping them.
            weights[cls] = max(1, round(fallback * cases))

    buckets = [[] for _ in range(shard_count)]
    loads = [0] * shard_count
    for cls, weight in sorted(weights.items(), key=lambda item: (-item[1], item[0])):
        index = min(range(shard_count), key=lambda candidate: (loads[candidate], candidate))
        buckets[index].append(cls)
        loads[index] += weight

    owner = {cls: index for index, group in enumerate(buckets) for cls in group}
    actual_owners = [owner.get(class_name(test)) for test in test_cases]
    if any(index is None for index in actual_owners):
        raise ValueError("At least one discovered test case has no shard")
    if len(owner) != len(set(owner)) or len(actual_owners) != len(test_cases):
        raise ValueError("Integration test shard assignment is incomplete")
    if any(not bucket for bucket in buckets):
        raise ValueError("A configured integration test shard is empty")
    return buckets, loads, case_counts


def actual_test_counts(trx_path):
    root = ET.parse(trx_path).getroot()
    counts = {}
    for result in root.findall(".//t:UnitTestResult", TRX_NAMESPACE):
        cls = class_name(result.get("testName", ""))
        counts[cls] = counts.get(cls, 0) + 1
    return counts


def append_summary(index, bucket, loads, counts):
    rows = [
        f"### Backend integration shard {index + 1}",
        "",
        f"- Test cases discovered: {sum(counts[cls] for cls in bucket)}",
        f"- Historical duration weight: {loads[index] / 1000:.1f}s",
        f"- Test classes: {len(bucket)}",
        "",
    ]
    summary_value = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_value:
        summary = Path(summary_value)
        with summary.open("a", encoding="utf-8") as output:
            output.write("\n".join(rows))
    print("\n".join(rows), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--test-project", required=True)
    parser.add_argument("--results-directory", required=True)
    parser.add_argument("--shard-index", type=int, required=True)
    parser.add_argument("--shard-count", type=int, default=3)
    args = parser.parse_args()
    if not 0 <= args.shard_index < args.shard_count:
        parser.error("shard-index must be between zero and shard-count - 1")

    history = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if history.get("schema_version") != 1 or history.get("shard_count") != args.shard_count:
        raise ValueError("Integration shard history does not match the requested shard count")
    listed = subprocess.run(
        ["dotnet", "test", args.test_project, "-c", "Release", "--no-build", "--no-restore",
         "--list-tests", "--verbosity", "quiet"],
        check=True, text=True, capture_output=True,
    )
    cases = parse_test_list(listed.stdout)
    buckets, loads, counts = make_shards(cases, history, args.shard_count)
    bucket = buckets[args.shard_index]
    filter_expression = "|".join(f"FullyQualifiedName~{cls}." for cls in sorted(bucket))
    results = Path(args.results_directory)
    results.mkdir(parents=True, exist_ok=True)
    trx_path = results / f"integration-shard-{args.shard_index}.trx"
    subprocess.run([
        "dotnet", "test", args.test_project, "-c", "Release", "--no-build", "--no-restore",
        "--filter", filter_expression, "--logger", f"trx;LogFileName={trx_path.name}",
        "--results-directory", str(results),
    ], check=True)
    actual = actual_test_counts(trx_path)
    expected = {cls: counts[cls] for cls in bucket}
    if actual != expected:
        raise ValueError(f"Shard {args.shard_index} result differs from discovery: expected {expected}, got {actual}")
    append_summary(args.shard_index, bucket, loads, counts)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Integration shard failed safely: {error}", file=sys.stderr)
        raise SystemExit(1) from None
