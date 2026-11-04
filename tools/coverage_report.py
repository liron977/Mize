#!/usr/bin/env python3
"""
Summarise a coverlet (cobertura) coverage file for the service's own code, and list the lines nothing executed.

    dotnet test tests/Mize.Reservations.IntegrationTests --collect:"XPlat Code Coverage" --results-directory <dir>
    python tools/coverage_report.py <dir>                          # every class of the service, worst first
    python tools/coverage_report.py <dir> --main-flow              # only the classes the ingest/stats flow runs through
    python tools/coverage_report.py <dir> --main-flow --lines      # ... and show the uncovered source lines
    python tools/coverage_report.py <dir> --class IngestService    # one class (substring match)

Standard library only. Tests, generated code and compiler-generated closures are folded into their owning class.
"""
import argparse
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

# classes that the ingest + stats request flow passes through
MAIN_FLOW = [
    "Api/Program", "ReservationsEndpoints", "IngestRequestParser", "ExceptionHandlingMiddleware", "IngestLogger", "LogSampler",
    "InfrastructureRegistration", "StartupLog",
    "IngestService", "IngestValidator", "IngestLimits", "ReservationDecider", "SupplierKey", "StatsService", "Reservation",
    "ReservationRepository", "StatsRepository", "WriteBatcher", "SqliteStorageInitializer", "DbInitializer",
    "RedisRateLimiter", "BlockCachingRateLimiter", "ReservationMetrics",
]


def load(results_dir):
    files = sorted(glob.glob(os.path.join(results_dir, "**", "coverage.cobertura.xml"), recursive=True), key=os.path.getmtime)
    if not files:
        sys.exit(f"No coverage.cobertura.xml under {results_dir}")
    return ET.parse(files[-1]).getroot(), files[-1]


def owner(name):
    # Ns.Foo/<>c__DisplayClass, Ns.Foo/<Method>d__3 -> Ns.Foo   (compiler-generated closures belong to their owner)
    return name.split("/")[0]


def short(name):
    return owner(name).split(".")[-1]


def collect(root):
    classes = {}
    for pkg in root.iter("package"):
        for cls in pkg.iter("class"):
            fn = cls.get("filename", "")
            if "\\tests\\" in fn.lower() or "/tests/" in fn.lower() or "Tests" in pkg.get("name", ""):
                continue
            name = short(cls.get("name", ""))
            entry = classes.setdefault((pkg.get("name"), name, fn), {"lines": {}, "branches": [0, 0]})
            lines_el = cls.find("lines")
            for line in (lines_el if lines_el is not None else []):
                n = int(line.get("number"))
                entry["lines"][n] = max(entry["lines"].get(n, 0), int(line.get("hits")))
                if line.get("branch") == "True":
                    m = re.search(r"\((\d+)/(\d+)\)", line.get("condition-coverage", ""))
                    if m:
                        entry["branches"][0] += int(m.group(1))
                        entry["branches"][1] += int(m.group(2))
    return classes


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("results_dir")
    ap.add_argument("--main-flow", action="store_true")
    ap.add_argument("--class", dest="cls", default="")
    ap.add_argument("--lines", action="store_true", help="print the uncovered source lines")
    ap.add_argument("--min", type=float, default=0, help="only show classes below this line coverage %")
    args = ap.parse_args()

    root, path = load(args.results_dir)
    classes = collect(root)
    rows = []
    for (pkg, name, fn), e in classes.items():
        if not e["lines"]:
            continue
        key = f"{os.path.basename(os.path.dirname(fn))}/{name}"
        if args.main_flow and not any(m == name or m == key or m.endswith("/" + name) for m in MAIN_FLOW):
            continue
        if args.cls and args.cls.lower() not in name.lower():
            continue
        total = len(e["lines"])
        hit = sum(1 for h in e["lines"].values() if h > 0)
        pct = 100.0 * hit / total
        if args.min and pct >= args.min:   # --min N: show only classes BELOW N percent
            continue
        rows.append((pct, pkg, name, fn, hit, total, e))
    rows.sort(key=lambda r: (r[0], r[2]))

    print(f"coverage file: {path}\n")
    print(f"{'line %':>7}  {'lines':>9}  {'branches':>10}  class")
    sh = st = bh = bt = 0
    for pct, pkg, name, fn, hit, total, e in rows:
        b = e["branches"]
        bs = f"{b[0]}/{b[1]}" if b[1] else "-"
        print(f"{pct:6.1f}%  {hit:>4}/{total:<4}  {bs:>10}  {name}   ({pkg.replace('Mize.Reservations.', '')})")
        sh += hit; st += total; bh += b[0]; bt += b[1]
    if st:
        print(f"\nTOTAL: {100.0 * sh / st:.1f}% of {st} lines" + (f", {100.0 * bh / bt:.1f}% of {bt} branches" if bt else ""))

    if args.lines:
        print("\nUNCOVERED LINES")
        for pct, pkg, name, fn, hit, total, e in rows:
            missing = sorted(n for n, h in e["lines"].items() if h == 0)
            if not missing:
                continue
            print(f"\n{name}  ({os.path.basename(fn)})")
            try:
                with open(fn, encoding="utf-8") as f:
                    src = f.read().splitlines()
            except OSError:
                src = []
            for n in missing:
                text = src[n - 1].strip() if 0 < n <= len(src) else ""
                print(f"  {n:>4}: {text[:150]}")


if __name__ == "__main__":
    main()
