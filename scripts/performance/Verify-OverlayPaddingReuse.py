"""Independently replay the frozen no-HWND overlay-padding A/B receipts."""
import argparse
import collections
import csv
import hashlib
import json
import pathlib
import re
import statistics
import xml.etree.ElementTree as ET


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("experiment", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    require(not args.output.exists(), "Output already exists")
    root = args.experiment.resolve()
    receipt = json.loads((root / "verification.json").read_text(encoding="utf-8-sig"))
    baseline_reuses = receipt.get("BaselineReusesPadding", False)
    require(type(baseline_reuses) is bool, "Invalid baseline mode")
    runs = json.loads((root / "runs.json").read_text(encoding="utf-8-sig"))
    require(len(runs) == 10, "Ten isolated test runs required")
    require(receipt["CounterSHA256"] == sha(root / "counters.csv"), "Counter hash differs")
    require(receipt["RunsSHA256"] == sha(root / "runs.json"), "Run hash differs")
    observed = collections.Counter()
    values = {}
    prior_end = ""
    for index, run in enumerate(runs):
        pair = index // 2 + 1
        order = "AB" if pair % 2 else "BA"
        variant = order[index % 2]
        require((run["Pair"], run["Variant"]) == (pair, variant), "Process order differs")
        require(prior_end <= run["StartUtc"] < run["EndUtc"], "Runs overlap or time order differs")
        prior_end = run["EndUtc"]
        trx = root / f"pair-{pair}-{variant}" / "result.trx"
        require(sha(trx) == run["TrxSHA256"], "TRX hash differs")
        xml = ET.parse(trx)
        tests = xml.findall(".//{*}UnitTestResult")
        require(len(tests) == 1 and tests[0].get("outcome") == "Passed", "Test result differs")
        require(tests[0].get("testName") == "OverlayPaddingMaterializationCounterScenario", "Wrong test")
        parsed = {}
        for output in xml.findall(".//{*}StdOut"):
            for line in (output.text or "").splitlines():
                match = re.fullmatch(r"PERFCOUNTER overlay-padding-(1|10|50) ([a-z-]+) ([0-9]+)", line.strip())
                if not match:
                    continue
                count, metric, value = int(match[1]), match[2], int(match[3])
                require((count, metric) not in parsed, "Duplicate metric")
                parsed[count, metric] = value
                observed[pair, variant, count, metric, value] += 1
                values[pair, variant, count, metric] = value
        require(len(parsed) == 36, "Wrong metric cardinality")
        for count in (1, 10, 50):
            expected = {"iterations": 12, "warmups": 6, "settled-windows": count, "settled-models": count + 1}
            for metric, total in {"models-created": 12 * (count + 1), "windows-created": 12 * count,
                                  "tabs-created": 12 * count, "svg-created": 12 * (5 * count + 1),
                                  "subscription-adds": 72 * count}.items():
                expected[metric] = total if variant == "A" and not baseline_reuses else 0
            require(all(parsed[count, metric] == value for metric, value in expected.items()), "Mechanical counts differ")
            require(all(parsed[count, metric] > 0 for metric in ("allocated-bytes", "elapsed-ticks", "timestamp-frequency")), "Invalid measurement")
    stored = collections.Counter((int(row["Pair"]), row["Variant"], int(row["Count"]), row["Metric"], int(row["Value"])) for row in rows(root / "counters.csv"))
    require(stored == observed and sum(stored.values()) == 360, "CSV differs from raw TRX multiset")
    manifests = {}
    verified_files = 0
    for variant, key in (("A", "BaselineId"), ("B", "CandidateId")):
        snapshot = root.parent / receipt[key]
        for directory, filename in (("source", "manifest.csv"), ("binaries", "binaries.csv")):
            entries = rows(snapshot / filename)
            for entry in entries:
                require(sha(snapshot / directory / entry["Path"]) == entry["SHA256"], f"Changed frozen file: {entry['Path']}")
                verified_files += 1
            if directory == "source":
                manifests[variant] = {entry["Path"]: entry["SHA256"] for entry in entries}
    changed = sorted(path for path in manifests["A"].keys() | manifests["B"].keys()
                     if re.match(r"^(FancyWM[^/]*|winman|winman-windows|ModernWpf)/", path)
                     and manifests["A"].get(path) != manifests["B"].get(path))
    expected_changes = ["FancyWM/TilingOverlayRenderer.cs"] if baseline_reuses else ["FancyWM/TilingOverlayRenderer.cs", "FancyWM/TilingService.Private.cs", "FancyWM/TilingService.cs"]
    require(changed == expected_changes, "Unexpected production/fixture delta")
    summaries = []
    for count in (1, 10, 50):
        summary = {"Windows": count}
        for variant in "AB":
            summary[f"{variant}MedianBytesPerUpdate"] = statistics.median(values[pair, variant, count, "allocated-bytes"] / 12 for pair in range(1, 6))
            summary[f"{variant}MedianMillisecondsPerUpdate"] = statistics.median(
                values[pair, variant, count, "elapsed-ticks"] * 1000 / values[pair, variant, count, "timestamp-frequency"] / 12 for pair in range(1, 6))
        summary["AllocationWins"] = sum(values[pair, "B", count, "allocated-bytes"] < values[pair, "A", count, "allocated-bytes"] for pair in range(1, 6))
        summary["ElapsedWins"] = sum(values[pair, "B", count, "elapsed-ticks"] < values[pair, "A", count, "elapsed-ticks"] for pair in range(1, 6))
        summaries.append(summary)
    result = dict(Verdict="PASS", Runs=10, Rows=360, FrozenFilesVerified=verified_files,
                  CommonTestSources=True, ProductionDelta=changed, BaselineReusesPadding=baseline_reuses, Scenarios=summaries,
                  MeasurementReceiptSHA256=sha(root / "verification.json"), LedgerAppended=False,
                  Scope="No-HWND managed service/layout/renderer/WPF materialization; native CPU/GPU/presentation pending")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
