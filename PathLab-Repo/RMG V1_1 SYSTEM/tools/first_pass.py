#!/usr/bin/env python3
"""First-pass and per-n pass rates for the n=1..4 sweep (stdlib only).

Records, per map, the smallest blocker count at which a strategic pass first
appears (`first_pass_n`), plus whether it never appears. No monotonicity is
assumed anywhere: the per-n tables report raw counts and rates side by side so a
non-monotone pattern is visible rather than smoothed away.

Input : summary_by_map.csv (nmax must match --nmax)
Output: first_pass_by_map.csv, first_pass_distribution.csv,
        pass_rate_by_n.csv, pass_rate_curve.csv
"""
import argparse
import csv
import os
from collections import Counter

OK = "ok"


def num(value, default=None):
    text = "" if value is None else str(value).strip()
    if text == "":
        return default
    try:
        return int(text)
    except ValueError:
        try:
            return float(text)
        except ValueError:
            return default


def fmt(value, spec=".4f"):
    return "" if value is None else format(value, spec)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, help="summary_by_map.csv")
    parser.add_argument("--outdir", required=True)
    parser.add_argument("--nmax", type=int, default=4)
    parser.add_argument("--metric", default="map_pass_adaptive",
                        choices=("passes_other_half", "passes_adaptive",
                                 "map_pass_other_half", "map_pass_adaptive"),
                        help="pass column to use. map_pass_* (p1-v4, default): map-level, "
                             "pass iff at least one combination meets the criterion; "
                             "passes_* = best-sample (legacy caliber, kept for comparison)")
    args = parser.parse_args()

    rows = [r for r in csv.DictReader(open(args.input, encoding="utf-8-sig"))
            if (r.get("status") or "").strip() == OK]
    if not rows:
        raise SystemExit("no ok rows")
    os.makedirs(args.outdir, exist_ok=True)

    per_map = []
    for row in rows:
        entry = {
            "size": row.get("size", ""),
            "seed": row.get("seed", ""),
            "mincut_natural": num(row.get("mincut_natural"), ""),
            "printed_moves": num(row.get("printed_moves"), ""),
            "n_switch_pred": num(row.get("n_switch_pred"), ""),
            "first_pass_n": "none",
            "pass_count": 0,
        }
        for n in range(1, args.nmax + 1):
            passed = num(row.get(f"n{n}_{args.metric}"), 0) == 1
            combos = num(row.get(f"n{n}_combos"), 0)
            passing_column = ("passing_adaptive_combinations"
                              if args.metric in ("passes_adaptive", "map_pass_adaptive")
                              else "passing_combinations")
            passing = num(row.get(f"n{n}_{passing_column}"))
            entry[f"n{n}_combos"] = combos
            entry[f"n{n}_passing"] = passing if passing is not None else ""
            entry[f"n{n}_combo_pass_rate"] = fmt(passing / combos if passing is not None and combos else None)
            entry[f"n{n}_map_pass"] = 1 if passed else 0
            if passed:
                entry["pass_count"] += 1
                if entry["first_pass_n"] == "none":
                    entry["first_pass_n"] = n
        per_map.append(entry)

    dist = Counter(str(e["first_pass_n"]) for e in per_map)
    first_rows = []
    for key in sorted(dist, key=lambda k: (k == "none", int(k) if k != "none" else 0)):
        first_rows.append({
            "first_pass_n": key,
            "maps": dist[key],
            "share": fmt(dist[key] / len(per_map)),
        })

    rate_rows = []
    for n in range(1, args.nmax + 1):
        combos = sum(e[f"n{n}_combos"] for e in per_map)
        passing = sum(e[f"n{n}_passing"] for e in per_map if e[f"n{n}_passing"] != "")
        map_pass = sum(e[f"n{n}_map_pass"] for e in per_map)
        rate_rows.append({
            "n": n,
            "maps": len(per_map),
            "combos_total": combos,
            "passing_combinations": passing,
            "combo_pass_rate": fmt(passing / combos if combos else None),
            "map_pass": map_pass,
            "map_pass_rate": fmt(map_pass / len(per_map)),
        })

    # curve: rate at each n, plus the deduplicated "passed at some n <= N"
    curve_rows = []
    seen = set()
    for row in rate_rows:
        n = row["n"]
        for index, entry in enumerate(per_map):
            if entry[f"n{n}_map_pass"] == 1:
                seen.add(index)
        curve_rows.append({
            "n": n,
            "combo_pass_rate": row["combo_pass_rate"],
            "map_pass_rate": row["map_pass_rate"],
            "maps_passed_at_this_n": row["map_pass"],
            "maps_passed_any_le_n": len(seen),
            "maps_passed_any_le_n_rate": fmt(len(seen) / len(per_map)),
        })

    def write(name, data):
        with open(os.path.join(args.outdir, name), "w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(data[0].keys()))
            writer.writeheader()
            writer.writerows(data)

    write("first_pass_by_map.csv", per_map)
    write("first_pass_distribution.csv", first_rows)
    write("pass_rate_by_n.csv", rate_rows)
    write("pass_rate_curve.csv", curve_rows)

    print(f"== first strategic pass at (maps={len(per_map)}) ==")
    for row in first_rows:
        print(f"n={row['first_pass_n']}: {row['maps']} ({float(row['share']):.1%})")
    print()
    print("== per-n rates (no monotonicity assumed) ==")
    for row in rate_rows:
        print(f"n={row['n']}: combos {row['combos_total']}, passing {row['passing_combinations']}, "
              f"combo {float(row['combo_pass_rate']):.2%}, map {row['map_pass']}/{row['maps']} "
              f"({float(row['map_pass_rate']):.1%})")
    print()
    print("== cumulative (distinct maps passed at some n <= N) ==")
    for row in curve_rows:
        print(f"<=n={row['n']}: this_n={row['maps_passed_at_this_n']} "
              f"any={row['maps_passed_any_le_n']} "
              f"({float(row['maps_passed_any_le_n_rate']):.1%})")
    print("outdir:", os.path.abspath(args.outdir))


if __name__ == "__main__":
    main()
