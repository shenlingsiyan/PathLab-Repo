#!/usr/bin/env python3
"""P1: why 40.8% of maps never pass, and what the terrain would have to change.

For every map (keyed by seed) the best sample at each n is examined across n=1..4.
A map is "rescued" by a criterion if relaxing that one criterion (and nothing
else) lets at least one n pass. That yields a per-map classification:

  pass                    already passes under the adaptive criteria (best sample)
  pass_map_level          best sample fails, but at least one combination meets
                          the formal criteria (passing_combinations > 0, P0 guard)
  search_truncated        no pass found, but enumeration was truncated for at
                          least one n: the map-level verdict is inconclusive
  overlap_only            only the overlap ceiling blocks it
  half_sep_only           only the half-separation floor blocks it
  delta_only              only the delta window blocks it
  multi_criterion         two or more criteria block it simultaneously
  no_reroute              no n produced any full reroute (switched == 0 everywhere)

Structural features of passing vs never-passing maps are compared separately so
the limitation can be tied to terrain shape rather than to the criteria.

Input : raw/sweep_WxH.csv (metric_version p1-v3)
Output: p1_classification.csv, p1_class_summary.csv, p1_feature_compare.csv
"""
import argparse
import csv
import os
import statistics as st
from collections import defaultdict

DELTA_STEP = 0.05
DELTA_BASE_MAX = 0.35
DELTA_MIN = 0.08
OVERLAP_MAX = 0.35
HALF_SEP_MIN = 0.55

FEATURES = ("obstacles", "printed_moves", "turns", "mincut_natural", "choke_count")


def num(value, default=None):
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def adaptive_max(n):
    return DELTA_BASE_MAX + DELTA_STEP * max(0, n - 1)


def passes(row, relax_overlap=False, relax_half=False, relax_delta=False):
    if int(float(row["switched"] or 0)) <= 0:
        return False
    overlap = float(row["best_overlap"])
    half = float(row["best_half_sep"])
    delta = float(row["best_delta"])
    n = int(row["n"])
    if not relax_overlap and overlap > OVERLAP_MAX:
        return False
    if not relax_half and half < HALF_SEP_MIN:
        return False
    if not relax_delta and not (DELTA_MIN <= delta <= adaptive_max(n)):
        return False
    return True


def delta_direction(rows):
    """For delta-only maps, split by which side of the window the best n falls on."""
    best = None
    for row in rows:
        if int(float(row["switched"] or 0)) <= 0:
            continue
        if float(row["best_overlap"]) > OVERLAP_MAX or float(row["best_half_sep"]) < HALF_SEP_MIN:
            continue
        delta = float(row["best_delta"])
        n = int(row["n"])
        if best is None or abs(delta - 0.2) < abs(best[1] - 0.2):
            best = (n, delta, adaptive_max(n))
    if best is None:
        return None
    _, delta, cap = best
    if delta == 0:
        return "delta_zero"
    if delta < DELTA_MIN:
        return "delta_low"
    if delta > cap:
        return "delta_high"
    return None


def _int_field(row, key):
    """Parse an integer-ish CSV field; None when absent or empty (legacy files)."""
    raw = (row.get(key) or "").strip()
    if raw == "":
        return None
    try:
        return int(float(raw))
    except ValueError:
        return None


def map_passes_formal(row):
    """P0 guard: map-level evidence beats best-sample heuristics.

    passing_combinations > 0 proves at least one combination meets the formal
    criteria, even when the best-scoring sample fails them. Legacy files without
    the column return None so the old behavior is preserved."""
    value = _int_field(row, "passing_combinations")
    return None if value is None else value > 0


def search_truncated(row):
    """cap_hit with zero passing combinations: inconclusive, never a plain failure."""
    if (row.get("cap_hit") or "").strip() != "1":
        return False
    value = _int_field(row, "passing_combinations")
    return value == 0


def classify(rows):
    if any(passes(r) for r in rows):
        return "pass"
    # P0 guards: a map with an actually-passing combination is not a failure,
    # and a truncated search without a pass found is unknown, not a failure.
    if any(map_passes_formal(r) for r in rows):
        return "pass_map_level"
    if any(search_truncated(r) for r in rows):
        return "search_truncated"
    if all(int(float(r["switched"] or 0)) <= 0 for r in rows):
        return "no_reroute"

    rescued = set()
    if any(passes(r, relax_overlap=True) for r in rows):
        rescued.add("overlap")
    if any(passes(r, relax_half=True) for r in rows):
        rescued.add("half_sep")
    if any(passes(r, relax_delta=True) for r in rows):
        rescued.add("delta")

    if len(rescued) == 1:
        if rescued == {"delta"}:
            return delta_direction(rows) or "delta_only"
        return {"overlap": "overlap_only", "half_sep": "half_sep_only"}[next(iter(rescued))]
    return "multi_criterion"


def summarise(values):
    values = [v for v in values if v is not None]
    if not values:
        return (None,) * 4
    return (st.mean(values), st.median(values), min(values), max(values))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--outdir", required=True)
    args = parser.parse_args()

    maps = defaultdict(list)
    for row in csv.DictReader(open(args.input, encoding="utf-8-sig")):
        if (row.get("status") or "").strip() != "ok":
            continue
        maps[(row["seed"], int(row["n"]))] = row

    by_seed = defaultdict(dict)
    for (seed, n), row in maps.items():
        by_seed[seed][n] = row

    records = []
    for seed, per_n in sorted(by_seed.items()):
        rows = [per_n[n] for n in sorted(per_n)]
        label = classify(rows)
        overlaps = [float(r["best_overlap"]) for r in rows if r["best_overlap"]]
        halves = [float(r["best_half_sep"]) for r in rows if r["best_half_sep"]]
        deltas = [float(r["best_delta"]) for r in rows if r["best_delta"]]
        switched_total = sum(int(float(r["switched"] or 0)) for r in rows)
        records.append({
            "seed": seed,
            "class": label,
            "n_levels": len(rows),
            "min_overlap_across_n": f"{min(overlaps):.4f}" if overlaps else "",
            "max_half_sep_across_n": f"{max(halves):.4f}" if halves else "",
            "min_delta_across_n": f"{min(deltas):.4f}" if deltas else "",
            "max_delta_across_n": f"{max(deltas):.4f}" if deltas else "",
            "switched_total": switched_total,
            **{f: rows[0].get(f, "") for f in FEATURES},
        })

    os.makedirs(args.outdir, exist_ok=True)
    with open(os.path.join(args.outdir, "p1_classification.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(records[0].keys()))
        writer.writeheader()
        writer.writerows(records)

    classes = defaultdict(list)
    for rec in records:
        classes[rec["class"]].append(rec)
    order = ("pass", "pass_map_level", "search_truncated", "delta_zero", "delta_low",
             "delta_high", "overlap_only", "half_sep_only", "delta_only",
             "multi_criterion", "no_reroute")
    total = len(records)

    summary_rows = []
    for name in order:
        group = classes.get(name, [])
        if not group:
            continue
        k1 = sum(1 for r in group if num(r["mincut_natural"], 0) == 1)
        summary_rows.append({
            "class": name,
            "maps": len(group),
            "share": f"{len(group) / total:.4f}",
            "mincut_k1_maps": k1,
            "mincut_k1_share": f"{k1 / len(group):.4f}",
            "median_moves": int(st.median([num(r["printed_moves"], 0) for r in group])),
            "median_min_overlap": f"{st.median([float(r['min_overlap_across_n']) for r in group]):.4f}",
            "median_max_half_sep": f"{st.median([float(r['max_half_sep_across_n']) for r in group]):.4f}",
            "median_switched_total": int(st.median([r["switched_total"] for r in group])),
        })
    with open(os.path.join(args.outdir, "p1_class_summary.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(summary_rows[0].keys()))
        writer.writeheader()
        writer.writerows(summary_rows)

    feature_rows = []
    passing = [r for r in records if r["class"].startswith("pass")]
    failing = [r for r in records if not r["class"].startswith("pass")]
    for feature in FEATURES:
        for label, group in (("pass", passing), ("fail", failing)):
            mean, median, low, high = summarise([num(r[feature]) for r in group])
            feature_rows.append({
                "feature": feature, "group": label, "maps": len(group),
                "mean": "" if mean is None else f"{mean:.2f}",
                "median": "" if median is None else f"{median:.2f}",
                "min": "" if low is None else f"{low:.2f}",
                "max": "" if high is None else f"{high:.2f}",
            })
    with open(os.path.join(args.outdir, "p1_feature_compare.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(feature_rows[0].keys()))
        writer.writeheader()
        writer.writerows(feature_rows)

    print(f"maps={total}  pass={len(passing)} ({len(passing)/total:.1%})  "
          f"fail={len(failing)} ({len(failing)/total:.1%})")
    print()
    print("class | maps | share | k=1 share | median moves | min_overlap | max_half_sep | switched")
    for row in summary_rows:
        print(f"{row['class']} | {row['maps']} | {float(row['share']):.1%} | "
              f"{float(row['mincut_k1_share']):.0%} | {row['median_moves']} | "
              f"{row['median_min_overlap']} | {row['median_max_half_sep']} | "
              f"{row['median_switched_total']}")
    print()
    print("feature | pass(mean/median) | fail(mean/median)")
    for feature in FEATURES:
        p = next(r for r in feature_rows if r["feature"] == feature and r["group"] == "pass")
        f = next(r for r in feature_rows if r["feature"] == feature and r["group"] == "fail")
        print(f"{feature}: {p['mean']}/{p['median']} vs {f['mean']}/{f['median']}")
    print("outdir:", os.path.abspath(args.outdir))


if __name__ == "__main__":
    main()
