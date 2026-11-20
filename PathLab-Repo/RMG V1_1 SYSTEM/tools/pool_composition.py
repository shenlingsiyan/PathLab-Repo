#!/usr/bin/env python3
"""Map-pool composition under the 2:8 acceptance rule.

Free detour (delta_zero) is accepted as an expected accident, not a defect.
The rule is a ceiling, not a target: among the acceptable pool it must stay at
or below 20%.

  acceptable pool = strategic pass + free detour (delta_zero)
  free share      = free detour / acceptable pool

Input : p1_classification.csv (from p1_terrain.py), summary_by_map.csv
Output: pool_composition.csv + console report with a Wilson 95% interval.
"""
import argparse
import csv
import math
import os


def wilson(success, total, z=1.96):
    if total == 0:
        return 0.0, 0.0, 0.0
    p = success / total
    denom = 1 + z * z / total
    center = (p + z * z / (2 * total)) / denom
    half = z * math.sqrt(p * (1 - p) / total + z * z / (4 * total * total)) / denom
    return p, max(0.0, center - half), min(1.0, center + half)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--classification", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--outdir", required=True)
    parser.add_argument("--ceiling", type=float, default=0.20)
    args = parser.parse_args()

    classes = {}
    for row in csv.DictReader(open(args.classification, encoding="utf-8-sig")):
        classes[row["seed"]] = row["class"]

    generated = 0
    for row in csv.DictReader(open(args.summary, encoding="utf-8-sig")):
        if (row.get("status") or "").strip() == "ok":
            generated += 1

    counts = {}
    for label in classes.values():
        counts[label] = counts.get(label, 0) + 1

    free = counts.get("delta_zero", 0)
    passing = counts.get("pass", 0)
    acceptable = free + passing
    share, low, high = wilson(free, acceptable)
    verdict = "within ceiling" if share <= args.ceiling else "above ceiling"
    # Informational variant (P1 decision): the entrance caliber stays best-based;
    # pass_map_level maps are only shown as what the pool would look like with them.
    map_level = counts.get("pass_map_level", 0)
    pool_with_ml = acceptable + map_level
    share_ml, low_ml, high_ml = wilson(free, pool_with_ml)

    rows = [
        {"metric": "generated_maps", "maps": generated, "share": "",
         "ci_low": "", "ci_high": "", "ceiling": "", "verdict": ""},
        {"metric": "strategic_pass", "maps": passing,
         "share": f"{passing / max(1, acceptable):.4f}",
         "ci_low": "", "ci_high": "", "ceiling": "", "verdict": ""},
        {"metric": "free_detour", "maps": free, "share": f"{share:.4f}",
         "ci_low": f"{low:.4f}", "ci_high": f"{high:.4f}",
         "ceiling": f"{args.ceiling:.2f}", "verdict": verdict},
        {"metric": "acceptable_pool", "maps": acceptable, "share": "",
         "ci_low": "", "ci_high": "", "ceiling": "", "verdict": ""},
        {"metric": "pool_incl_map_level", "maps": pool_with_ml, "share": "",
         "ci_low": "", "ci_high": "", "ceiling": "", "verdict": ""},
        {"metric": "free_detour_incl_map_level", "maps": free,
         "share": f"{share_ml:.4f}" if pool_with_ml else "",
         "ci_low": f"{low_ml:.4f}" if pool_with_ml else "",
         "ci_high": f"{high_ml:.4f}" if pool_with_ml else "",
         "ceiling": "", "verdict": ""},
    ]
    for label in ("delta_low", "delta_high", "overlap_only", "half_sep_only",
                  "multi_criterion", "no_reroute"):
        if label in counts:
            rows.append({"metric": f"rejected_{label}", "maps": counts[label],
                         "share": "", "ci_low": "", "ci_high": "",
                         "ceiling": "", "verdict": ""})
    # P0 (p1-v4): surface the new classes for visibility. Pool acceptance
    # caliber (strategic_pass/free_detour) is unchanged (P1 decision):
    # pass_map_level appears only in the informational pool variant above.
    for label in ("pass_map_level", "search_truncated"):
        if label in counts:
            rows.append({"metric": f"p0_{label}", "maps": counts[label],
                         "share": "", "ci_low": "", "ci_high": "",
                         "ceiling": "", "verdict": ""})

    os.makedirs(args.outdir, exist_ok=True)
    with open(os.path.join(args.outdir, "pool_composition.csv"), "w",
              newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    print(f"generated maps: {generated}")
    print(f"acceptable pool: {acceptable} (strategic pass {passing} + free detour {free})")
    print(f"free detour share: {share:.1%}  Wilson 95% CI [{low:.1%}, {high:.1%}]  "
          f"ceiling {args.ceiling:.0%} -> {verdict}")
    if map_level:
        print(f"info: pool incl. map-level passes: {pool_with_ml} "
              f"(free detour {share_ml:.1%}, Wilson 95% CI [{low_ml:.1%}, {high_ml:.1%}]); "
              "entrance caliber unchanged")
    rejected = [f"{label}={counts[label]}" for label in sorted(counts)
                if label not in ("pass", "delta_zero")]
    if rejected:
        print("rejected:", ", ".join(rejected))
    print("outdir:", os.path.abspath(args.outdir))


if __name__ == "__main__":
    main()
