#!/usr/bin/env python3
"""Explain why single-corridor (k=1) and multi-criterion maps never pass.

Input : raw/sweep_WxH.csv (metric_version p1-v3) + p1_classification.csv
Output: k1_evidence.csv, multi_criteria_detail.csv, criterion_pair_counts.csv

k=1 evidence: a single articulation cell controls every route, so any blocker
set that covers it forces the toll path (switched = 0) instead of a reroute.
The table shows how much of each map's combination mass ends up tolled or
equal-cost, and where the best delta lands.

multi-criterion detail: for each never-passing map, the n with the fewest
violations and the exact criteria still violated there.
"""
import argparse
import csv
import os
import statistics as st
from collections import Counter, defaultdict

DELTA_STEP = 0.05
DELTA_BASE_MAX = 0.35
DELTA_MIN = 0.08
OVERLAP_MAX = 0.35
HALF_SEP_MIN = 0.55


def num(value, default=None):
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def adaptive_max(n):
    return DELTA_BASE_MAX + DELTA_STEP * max(0, n - 1)


def violations(row):
    """Criteria violated by this n's best sample, as a sorted list."""
    failed = []
    if int(num(row.get("switched"), 0)) <= 0:
        failed.append("reroute")
    if num(row["best_overlap"], 1.0) > OVERLAP_MAX:
        failed.append("overlap")
    if num(row["best_half_sep"], 0.0) < HALF_SEP_MIN:
        failed.append("half_sep")
    delta = num(row["best_delta"], 0.0)
    n = int(row["n"])
    if not (DELTA_MIN <= delta <= adaptive_max(n)):
        failed.append("delta_high" if delta > adaptive_max(n) else
                      "delta_zero" if delta == 0 else "delta_low")
    return failed


def closest_level(rows):
    """n with the fewest violations; ties broken by the smallest total gap."""
    best = None
    for row in rows:
        failed = violations(row)
        delta = num(row["best_delta"], 0.0)
        n = int(row["n"])
        gap = (max(0.0, num(row["best_overlap"], 1.0) - OVERLAP_MAX)
               + max(0.0, HALF_SEP_MIN - num(row["best_half_sep"], 0.0))
               + max(0.0, DELTA_MIN - delta)
               + max(0.0, delta - adaptive_max(n)))
        key = (len(failed), gap)
        if best is None or key < best[0]:
            best = (key, row, failed)
    return best[1], best[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--classification", required=True)
    parser.add_argument("--outdir", required=True)
    args = parser.parse_args()

    per_map = defaultdict(dict)
    for row in csv.DictReader(open(args.input, encoding="utf-8-sig")):
        if (row.get("status") or "").strip() != "ok":
            continue
        per_map[row["seed"]][int(row["n"])] = row

    classes = {}
    for row in csv.DictReader(open(args.classification, encoding="utf-8-sig")):
        classes[row["seed"]] = row["class"]

    groups = {"k=1": [], "k>=2": []}
    multi_rows = []
    failure_rows = []
    pair_counts = Counter()

    for seed, per_n in sorted(per_map.items()):
        rows = [per_n[n] for n in sorted(per_n)]
        first = rows[0]
        k = int(num(first["mincut_natural"], 0))
        label = classes.get(seed, "?")
        combos = sum(int(num(r["combos"], 0)) for r in rows)
        tolled = sum(int(num(r["tolled"], 0)) for r in rows)
        switched = sum(int(num(r["switched"], 0)) for r in rows)
        equal = sum(int(num(r["equal_cost"], 0)) for r in rows)
        deltas = [num(r["best_delta"], 0.0) for r in rows]
        group = "k=1" if k == 1 else "k>=2"
        groups[group].append({
            "seed": seed,
            "class": label,
            "moves": int(num(first["printed_moves"], 0)),
            "combos": combos,
            "tolled_share": tolled / combos if combos else 0.0,
            "switched_share": switched / combos if combos else 0.0,
            "equal_share": equal / combos if combos else 0.0,
            "max_delta": max(deltas),
            "delta_zero": 1 if max(deltas) == 0 else 0,
        })

        if not label.startswith("pass"):
            detail = {"seed": seed, "class": label, "mincut": k,
                      "moves": int(num(first["printed_moves"], 0))}
            for r in rows:
                n = int(r["n"])
                combos_n = int(num(r["combos"], 0))
                equal_n = int(num(r["equal_cost"], 0))
                detail[f"n{n}_delta"] = r["best_delta"]
                detail[f"n{n}_equal_share"] = (f"{equal_n / combos_n:.3f}"
                                                if combos_n else "")
                detail[f"n{n}_failed"] = "+".join(violations(r)) or "ok"
            failure_rows.append(detail)

        if label != "multi_criterion":
            continue
        row, failed = closest_level(rows)
        union = sorted({("delta" if f.startswith("delta") else f)
                        for r in rows for f in violations(r)})
        pair_counts[" + ".join(union)] += 1
        multi_rows.append({
            "seed": seed,
            "mincut": k,
            "moves": int(num(first["printed_moves"], 0)),
            "closest_n": int(row["n"]),
            "failed_union": " + ".join(union) if union else "none",
            "failed_at_closest_n": " + ".join(failed) if failed else "none",
            "per_n_failed": " | ".join(
                f"n{r['n']}:" + ("+".join(violations(r)) or "ok") for r in rows),
            "overlap": row["best_overlap"],
            "half_sep": row["best_half_sep"],
            "delta": row["best_delta"],
            "delta_cap": f"{adaptive_max(int(row['n'])):.2f}",
            "switched": row["switched"],
        })

    os.makedirs(args.outdir, exist_ok=True)

    evidence_rows = []
    for name in ("k=1", "k>=2"):
        group = groups[name]
        failing = [r for r in group if not r["class"].startswith("pass")]
        evidence_rows.append({
            "group": name,
            "maps": len(group),
            "failing_maps": len(failing),
            "pass_share": f"{sum(1 for r in group if r['class'].startswith('pass')) / max(1, len(group)):.4f}",
            "median_moves": int(st.median([r["moves"] for r in group])) if group else "",
            "median_tolled_share": f"{st.median([r['tolled_share'] for r in group]):.4f}" if group else "",
            "median_switched_share": f"{st.median([r['switched_share'] for r in group]):.4f}" if group else "",
            "median_equal_share": f"{st.median([r['equal_share'] for r in group]):.4f}" if group else "",
            "median_max_delta": f"{st.median([r['max_delta'] for r in group]):.4f}" if group else "",
            "delta_zero_maps": sum(r["delta_zero"] for r in group),
        })
    with open(os.path.join(args.outdir, "k1_evidence.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(evidence_rows[0].keys()))
        writer.writeheader()
        writer.writerows(evidence_rows)

    with open(os.path.join(args.outdir, "multi_criteria_detail.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(multi_rows[0].keys()))
        writer.writeheader()
        writer.writerows(multi_rows)

    pair_rows = [{"failed_combination": key, "maps": value}
                 for key, value in pair_counts.most_common()]
    with open(os.path.join(args.outdir, "criterion_pair_counts.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=["failed_combination", "maps"])
        writer.writeheader()
        writer.writerows(pair_rows)

    if failure_rows:
        with open(os.path.join(args.outdir, "failure_detail.csv"), "w",
                  newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(failure_rows[0].keys()))
            writer.writeheader()
            writer.writerows(failure_rows)
        by_class = defaultdict(list)
        for rec in failure_rows:
            by_class[rec["class"]].append(rec)
        print()
        print("failure class | maps | median moves | median equal share (n=1..4)")
        for name in sorted(by_class, key=lambda c: -len(by_class[c])):
            group = by_class[name]
            equal = []
            for rec in group:
                for key in ("n1", "n2", "n3", "n4"):
                    value = num(rec.get(f"{key}_equal_share"))
                    if value is not None:
                        equal.append(value)
            print(f"{name} | {len(group)} | "
                  f"{int(st.median([r['moves'] for r in group]))} | "
                  f"{st.median(equal):.3f}")

    print("group | maps | failing | pass share | moves | tolled | switched | equal | max delta | delta=0")
    for row in evidence_rows:
        print(f"{row['group']} | {row['maps']} | {row['failing_maps']} | {row['pass_share']} | "
              f"{row['median_moves']} | {row['median_tolled_share']} | "
              f"{row['median_switched_share']} | {row['median_equal_share']} | "
              f"{row['median_max_delta']} | {row['delta_zero_maps']}")
    print()
    print("multi-criterion co-failures:", dict(pair_counts))
    print("outdir:", os.path.abspath(args.outdir))


if __name__ == "__main__":
    main()
