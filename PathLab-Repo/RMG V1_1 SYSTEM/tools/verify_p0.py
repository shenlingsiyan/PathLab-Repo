#!/usr/bin/env python3
"""P0 verification: map-level pass semantics (metric_version p1-v4).

Modes (can be combined):
  --unit               V1: synthetic CSV cases pushed through summarize_sweep /
                       p1_terrain / first_pass (case A contradiction, case B
                       truncation, case C legacy 34-column file).
  --raw FILE           V2: per-row invariants on a real analyzer export.
  --baseline FILE      V3/V4: shared-column byte equality against the pre-change
                       export (same seeds) plus a before/after rate table.

stdlib only. Exit code 0 = all checks passed.
Pre-P0 raw files have 34 columns; p1-v4 appends 3 map-level columns (37 total).
"""
import argparse
import csv
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))

V4_HEADER = (
    "seed,status,width,height,obstacles,printed_moves,canonical_moves,printed_matches,"
    "turns,mincut_natural,choke_count,w,n,combos,tolled,switched,equal_cost,"
    "best_overlap,best_half_sep,best_shift,best_delta,best_killzone_gain,"
    "passes_other_half,cert_blockers,cert_moves,n_switch_pred,combos_expected,cap_hit,"
    "passing_combinations,metric_version,passes_adaptive,passing_adaptive_combinations,"
    "half_sep_tier,adaptive_delta_max,map_pass_other_half,map_pass_adaptive,map_pass_status"
)
LEGACY_HEADER = ",".join(V4_HEADER.split(",")[:-3])  # pre-P0: 33 columns

FAILURES = []


def check(cond, message):
    if cond:
        print(f"  PASS  {message}")
    else:
        print(f"  FAIL  {message}")
        FAILURES.append(message)


def write_csv(path, header, rows):
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(header + "\n")
        for row in rows:
            handle.write(",".join(str(v) for v in row) + "\n")


def contradiction_row(seed):
    """Best sample fails every criterion, yet 3 combinations pass the formal ones."""
    return [seed, "ok", 10, 10, 42, 20, 20, 1, 4, 2, 1, 10, 1, 10, 2, 8, 5,
            0.900, 0.300, 3, 0.500, 0.000,
            0, "", "", 2, 10, 0, 3, "p1-v4", 0, 2, "fail", 0.350,
            1, 1, "pass"]


def truncated_row(seed):
    """cap_hit with zero passing combinations: inconclusive, not a plain failure."""
    return [seed, "ok", 10, 10, 42, 20, 20, 1, 4, 2, 1, 10, 1, 4000, 40, 3960, 1000,
            0.800, 0.400, 2, 0.500, 0.000,
            0, "", "", 3, 20000, 1, 0, "p1-v4", 0, 0, "fail", 0.350,
            0, 0, "unknown_truncated"]


def legacy_row(seed):
    """Pre-P0 shape: first 34 columns of the v4 row, zero passing combinations."""
    row = contradiction_row(seed)[:34]
    row[28] = 0  # passing_combinations
    return row


def run(script, args):
    proc = subprocess.run([sys.executable, os.path.join(HERE, script)] + args,
                          capture_output=True, text=True)
    return proc


def read_csv(path):
    with open(path, encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle))


def unit_mode(tmp):
    print("== V1: unit cases (synthetic CSVs) ==")
    live = os.path.join(tmp, "live")
    os.makedirs(live, exist_ok=True)
    write_csv(os.path.join(live, "sweep_10x10.csv"), V4_HEADER,
              [contradiction_row(1), truncated_row(2)])

    proc = run("summarize_sweep.py", ["--input", live, "--outdir",
                                      os.path.join(live, "out"), "--nmax", "1"])
    check(proc.returncode == 0, f"summarize_sweep exits 0 on p1-v4 file ({proc.stderr.strip()[:120]})")
    dq_path = os.path.join(live, "out", "data_quality_report.txt")
    dq = open(dq_path, encoding="utf-8").read() if os.path.exists(dq_path) else ""
    check("hard errors (0)" in dq, "summarize data_quality_report has 0 hard errors")

    maps = {r["seed"]: r for r in read_csv(os.path.join(live, "out", "summary_by_map.csv"))}
    a = maps.get("1", {})
    check(a.get("n1_map_pass_other_half") == "1",
          "case A: n1_map_pass_other_half=1 even though n1_passes_other_half=0")
    check(a.get("n1_passes_other_half") == "0", "case A: legacy best-sample column stays 0")
    check(a.get("n1_map_pass_status") == "pass", "case A: status=pass")
    b = maps.get("2", {})
    check(b.get("n1_map_pass_status") == "unknown_truncated",
          "case B: status=unknown_truncated (not counted as plain failure)")
    n_rows = {r["n"]: r for r in read_csv(os.path.join(live, "out", "summary_by_n.csv"))}
    n1 = n_rows.get("1", {})
    check(n1.get("map_level_pass_maps") == "1" and n1.get("map_pass_maps") == "0",
          "case A+B: summary_by_n map_level_pass_maps=1 vs legacy map_pass_maps=0")
    check(n1.get("unknown_truncated_maps") == "1" and n1.get("fail_complete_maps") == "0",
          "case A+B: truncation counted separately from fail_complete")

    proc = run("p1_terrain.py", ["--input", os.path.join(live, "sweep_10x10.csv"),
                                 "--outdir", os.path.join(live, "p1")])
    check(proc.returncode == 0, f"p1_terrain exits 0 ({proc.stderr.strip()[:120]})")
    classes = {r["seed"]: r["class"]
               for r in read_csv(os.path.join(live, "p1", "p1_classification.csv"))}
    check(classes.get("1") == "pass_map_level",
          "case A: p1_terrain classifies as pass_map_level (not a failure class)")
    check(classes.get("2") == "search_truncated",
          "case B: p1_terrain classifies as search_truncated")

    proc = run("first_pass.py", ["--input", os.path.join(live, "out", "summary_by_map.csv"),
                                 "--outdir", os.path.join(live, "fp"),
                                 "--nmax", "1", "--metric", "map_pass_other_half"])
    check(proc.returncode == 0, f"first_pass --metric map_pass_other_half exits 0 ({proc.stderr.strip()[:120]})")
    rates = {r["n"]: r for r in read_csv(os.path.join(live, "fp", "pass_rate_by_n.csv"))}
    check(rates.get("1", {}).get("map_pass") == "1",
          "first_pass map-level metric: 1/2 maps pass at n=1")

    # Case C: legacy 34-column file must still parse (H3) with empty p0 columns.
    legacy_dir = os.path.join(tmp, "legacy")
    os.makedirs(legacy_dir, exist_ok=True)
    write_csv(os.path.join(legacy_dir, "sweep_10x10.csv"), LEGACY_HEADER,
              [legacy_row(3)])
    proc = run("summarize_sweep.py", ["--input", legacy_dir, "--outdir",
                                      os.path.join(legacy_dir, "out"), "--nmax", "1"])
    check(proc.returncode == 0, f"legacy 34-column file still parses (H3) ({proc.stderr.strip()[:120]})")
    cmap = read_csv(os.path.join(legacy_dir, "out", "summary_by_map.csv"))
    check(cmap and cmap[0].get("n1_map_pass_status") == "",
          "legacy file: p0 columns are empty, no invented values")

    proc = run("p1_terrain.py", ["--input", os.path.join(legacy_dir, "sweep_10x10.csv"),
                                 "--outdir", os.path.join(legacy_dir, "p1")])
    classes = {r["seed"]: r["class"]
               for r in read_csv(os.path.join(legacy_dir, "p1", "p1_classification.csv"))}
    check(classes.get("3") == "multi_criterion",
          "legacy file: p1_terrain keeps pre-P0 classification when no combo passes")


def rows_by_key(rows):
    return {(r["seed"], r["n"]): r for r in rows}


def raw_mode(raw_path, baseline_path):
    print(f"== V2: row invariants on {os.path.basename(raw_path)} ==")
    rows = read_csv(raw_path)
    header = set(rows[0].keys())
    check({"map_pass_other_half", "map_pass_adaptive", "map_pass_status"} <= header,
          "p0 columns present in raw CSV")
    ok_rows = [r for r in rows if (r["status"] or "").strip() == "ok"]
    versions = {r["metric_version"] for r in ok_rows}
    # p3-v5 = p1-v4 + 表尾 4 个分离度诊断列（P3）；判据与 p0 列语义不变。
    # p8-v6 = p3-v5 + 表尾 5 个难度诊断列（P8）；同样只读追加。
    # p9-v7 = p8-v6 + 表尾 1 个 theta_density 列（P9）；同样只读追加。
    check(versions and versions <= {"p1-v4", "p3-v5", "p8-v6", "p9-v7"},
          f"metric_version in {{p1-v4, p3-v5, p8-v6, p9-v7}} (got {versions or 'none'})")

    bad = 0
    mono_bad = 0
    status_bad = 0
    contradictions = {}
    for r in ok_rows:
        passing = int(r["passing_combinations"])
        passing_ad = int(r["passing_adaptive_combinations"] or 0)
        cap = r["cap_hit"] == "1"
        mp = r["map_pass_other_half"] == "1"
        mpa = r["map_pass_adaptive"] == "1"
        status = r["map_pass_status"]
        expected = ("pass" if passing > 0 else
                    "unknown_truncated" if cap else "fail_complete")
        if mp != (passing > 0) or mpa != (passing_ad > 0) or status != expected:
            bad += 1
        if r["passes_other_half"] == "1" and not mp:
            mono_bad += 1
        if r["passes_adaptive"] == "1" and not mpa:
            mono_bad += 1
        n = r["n"]
        if r["passes_other_half"] == "0" and passing > 0:
            contradictions[n] = contradictions.get(n, 0) + 1
    check(bad == 0, f"map_pass flags/status match passing counts on all {len(ok_rows)} rows")
    check(mono_bad == 0, "monotonicity: best-based pass implies map-level pass (strict and adaptive)")
    print(f"  INFO  contradiction rows (passes_other_half=0 but passing_combinations>0): "
          f"{ {k: v for k, v in sorted(contradictions.items())} }")

    if baseline_path:
        print(f"== V3/V4: baseline comparison vs {os.path.basename(baseline_path)} ==")
        before = rows_by_key(read_csv(baseline_path))
        after = rows_by_key(ok_rows)
        shared = sorted(set(before) & set(after))
        check(len(shared) > 0, f"shared (seed,n) keys found: {len(shared)}")
        before_header = [k for k in LEGACY_HEADER.split(",") if k != "metric_version"]
        diff_cols = set()
        for key in shared:
            b, a = before[key], after[key]
            for col in before_header:
                if (b.get(col) or "").strip() != (a.get(col) or "").strip():
                    diff_cols.add(col)
        check(not diff_cols,
              f"all {len(before_header)} shared columns byte-identical "
              f"(differences: {sorted(diff_cols) or 'none'})")

        def rate(rows_by, n, col):
            vals = [r for (s, nn), r in rows_by.items() if nn == str(n)]
            if not vals:
                return None, 0
            return sum(1 for r in vals if (r.get(col) or "") == "1"), len(vals)

        ns = sorted({int(r["n"]) for r in ok_rows})
        print(f"  {'n':<3} {'legacy(bef)':<14} {'legacy(aft)':<14} "
              f"{'map-level(aft)':<16} {'unknown':<9} {'fail_complete':<14} {'cap_hit(bef/aft)'}")
        for n in ns:
            lb, tot_b = rate(before, n, "passes_other_half")
            la, tot_a = rate(after, n, "passes_other_half")
            ml, _ = rate(after, n, "map_pass_other_half")
            unk = sum(1 for (s, nn), r in after.items()
                      if nn == str(n) and r["map_pass_status"] == "unknown_truncated")
            fc = sum(1 for (s, nn), r in after.items()
                     if nn == str(n) and r["map_pass_status"] == "fail_complete")
            capb = sum(1 for (s, nn), r in before.items() if nn == str(n) and r["cap_hit"] == "1")
            capa = sum(1 for (s, nn), r in after.items() if nn == str(n) and r["cap_hit"] == "1")
            fmt_rate = lambda c, t: f"{c}/{t} ({c / t:.1%})" if t else "-"
            print(f"  {n:<3} {fmt_rate(lb, tot_b):<14} {fmt_rate(la, tot_a):<14} "
                  f"{fmt_rate(ml, tot_a):<16} {unk:<9} {fc:<14} {capb}/{capa}")
            check(capb == capa, f"n={n}: cap_hit counts identical before/after")
            if lb is not None and ml is not None:
                check(lb <= ml, f"n={n}: legacy rate <= map-level rate (caliber monotonicity)")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--unit", action="store_true", help="run V1 synthetic cases")
    parser.add_argument("--raw", help="raw analyzer CSV for V2 invariants")
    parser.add_argument("--baseline", help="pre-change raw CSV for V3/V4 comparison")
    args = parser.parse_args()
    if not (args.unit or args.raw):
        parser.error("nothing to do: pass --unit and/or --raw")

    if args.unit:
        with tempfile.TemporaryDirectory(prefix="p0verify_") as tmp:
            unit_mode(tmp)
    if args.raw:
        raw_mode(args.raw, args.baseline)

    print()
    if FAILURES:
        print(f"verify_p0: {len(FAILURES)} check(s) FAILED")
        return 1
    print("verify_p0: all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
