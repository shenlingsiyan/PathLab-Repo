#!/usr/bin/env python3
"""Stage-1 sweep summarizer: strict parsing, split denominators, auditable outputs.

Input : raw/sweep_<W>x<H>.csv produced by `MapGeneratorV4_6.exe sweep`.
Output: summary_by_size.csv, summary_by_n.csv, summary_by_map.csv,
        summary_by_map_n.csv, data_quality_report.txt

Data-quality hard errors block the formal summaries (exit code 2) and are
written to data_quality_report.txt only.

Known semantics (confirmed from src/MapAnalyzer.cs, stage 2 pending naming):
  equal_cost      per-combination count of `nextCost == baseCost`
                  (blocked global shortest-path cost equals the original cost).
  n_switch_pred   map-level estimate ceil((adjusted alt corridor cost - base cost)
                  / (W - 1)); -1 means no genuinely independent alternative corridor.
                  It is not an enumerated minimum blocker count.
"""
import argparse
import csv
import glob
import os
import re
import sys
from collections import Counter, defaultdict

OK_STATUS = "ok"
FAILED_STATUSES = ("generation_failed", "unreachable")

OK_COUNT_FIELDS = ("combos", "tolled", "switched", "equal_cost")
MAP_INT_FIELDS = ("width", "height", "obstacles", "printed_moves",
                  "canonical_moves", "printed_matches", "turns",
                  "mincut_natural", "choke_count", "n_switch_pred")
BEST_FLOAT_FIELDS = ("best_overlap", "best_half_sep", "best_delta",
                     "best_shift", "best_killzone_gain")
EXTENDED_FIELDS = {"combos_expected", "cap_hit", "passing_combinations", "metric_version"}
# P2 (p1-v3) columns; optional, older raw files do not carry them.
P2_FIELDS = {"passes_adaptive", "passing_adaptive_combinations",
             "half_sep_tier", "adaptive_delta_max"}
P2_TIERS = ("premium", "usable", "fail")
# P0 (p1-v4) columns: map-level pass status, three states.
# unknown_truncated must never be merged into the plain-failure denominator.
P0_FIELDS = {"map_pass_other_half", "map_pass_adaptive", "map_pass_status"}
MAP_PASS_STATUSES = ("pass", "fail_complete", "unknown_truncated")
SATURATED_COMBOS = 2 ** 63 - 1


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []
        self.infos = []
        self.processed = []
        self.skipped = []

    def error(self, where, message):
        self.errors.append(f"{where}: {message}")

    def warn(self, where, message):
        self.warnings.append(f"{where}: {message}")

    def info(self, message):
        self.infos.append(message)


def as_int(value):
    text = "" if value is None else str(value).strip()
    if text == "":
        return None
    try:
        return int(text)
    except ValueError:
        return None


def as_float(value):
    text = "" if value is None else str(value).strip()
    if text == "":
        return None
    try:
        return float(text)
    except ValueError:
        return None


def cell(value, spec=".4f"):
    """CSV formatting: None becomes an empty field (never a silent zero)."""
    if value is None:
        return ""
    return format(value, spec)


def display(value, spec=".4f"):
    return "N/A" if value is None else format(value, spec)


def opt_text(optional, key):
    """Render an optional P2 field for CSV; absent fields stay empty."""
    if not optional or key not in optional:
        return ""
    value = optional[key]
    if isinstance(value, bool):
        return "1" if value else "0"
    return value


def parse_size_from_name(path):
    match = re.search(r"sweep_(\d+)x(\d+)\.csv$", os.path.basename(path), re.I)
    if not match:
        return None
    return int(match.group(1)), int(match.group(2))


def load_file(path, file_w, file_h, nmax, expect_maps, combo_cap, report):
    """Parse one raw CSV. Returns a per-file structure even when hard errors occur."""
    name = os.path.basename(path)
    maps = {}
    line_no = 1
    stats = {"rows": 0, "status": Counter(), "extended": False, "p2": False,
             "p0": False, "versions": Counter()}

    with open(path, newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        header = set(reader.fieldnames or [])
        extended = EXTENDED_FIELDS <= header
        stats["extended"] = extended
        has_p2 = P2_FIELDS <= header
        stats["p2"] = has_p2
        has_p0 = P0_FIELDS <= header
        stats["p0"] = has_p0
        for row in reader:
            line_no += 1
            where = f"{name}:{line_no}"
            stats["rows"] += 1

            status = (row.get("status") or "").strip()
            if status not in (OK_STATUS,) + FAILED_STATUSES:
                report.error(where, f"status missing or unknown: {status!r}")
                continue
            stats["status"][status] += 1

            seed = as_int(row.get("seed"))
            if seed is None:
                report.error(where, "seed missing or not an integer")
                continue

            entry = maps.get(seed)
            if entry is None:
                entry = {"status": status, "map": None, "ns": {}}
                maps[seed] = entry
            elif entry["status"] != status:
                report.error(where, f"seed {seed} mixes status {entry['status']!r} and {status!r}")
                continue

            if status != OK_STATUS:
                continue

            values = {}
            broken = False
            for field in ("width", "height") + MAP_INT_FIELDS:
                value = as_int(row.get(field))
                if value is None:
                    report.error(where, f"{field} missing or not an integer")
                    broken = True
                else:
                    values[field] = value
            for field in ("n",) + OK_COUNT_FIELDS:
                value = as_int(row.get(field))
                if value is None:
                    report.error(where, f"{field} missing or not an integer")
                    broken = True
                else:
                    values[field] = value
            pass_flag = (row.get("passes_other_half") or "").strip()
            if pass_flag not in ("0", "1"):
                report.error(where, f"passes_other_half must be 0 or 1, got {pass_flag!r}")
                broken = True

            optional = None
            if extended:
                expected = as_int(row.get("combos_expected"))
                passing = as_int(row.get("passing_combinations"))
                cap_raw = (row.get("cap_hit") or "").strip()
                version = (row.get("metric_version") or "").strip()
                if expected is None or expected < 0:
                    report.error(where, f"combos_expected invalid: {row.get('combos_expected')!r}")
                    broken = True
                if passing is None or passing < 0:
                    report.error(where, f"passing_combinations invalid: {row.get('passing_combinations')!r}")
                    broken = True
                if cap_raw not in ("0", "1"):
                    report.error(where, f"cap_hit must be 0 or 1, got {cap_raw!r}")
                    broken = True
                if not version:
                    report.error(where, "metric_version missing")
                    broken = True
                if not broken:
                    optional = {
                        "combos_expected": expected,
                        "passing_combinations": passing,
                        "cap_hit": cap_raw == "1",
                        "metric_version": version,
                    }

            if has_p2 and not broken:
                adaptive_flag = (row.get("passes_adaptive") or "").strip()
                adaptive_passing = as_int(row.get("passing_adaptive_combinations"))
                tier = (row.get("half_sep_tier") or "").strip()
                adaptive_delta_max = as_float(row.get("adaptive_delta_max"))
                if adaptive_flag not in ("0", "1"):
                    report.error(where, f"passes_adaptive must be 0 or 1, got {adaptive_flag!r}")
                    broken = True
                if adaptive_passing is None or adaptive_passing < 0:
                    report.error(where, "passing_adaptive_combinations invalid")
                    broken = True
                if tier not in P2_TIERS:
                    report.error(where, f"half_sep_tier unknown: {tier!r}")
                    broken = True
                if adaptive_delta_max is None:
                    report.error(where, "adaptive_delta_max invalid")
                    broken = True
                if not broken:
                    optional = optional or {}
                    optional.update({
                        "passes_adaptive": adaptive_flag == "1",
                        "passing_adaptive_combinations": adaptive_passing,
                        "half_sep_tier": tier,
                        "adaptive_delta_max": adaptive_delta_max,
                    })

            if has_p0 and not broken:
                map_flag = (row.get("map_pass_other_half") or "").strip()
                map_adaptive_flag = (row.get("map_pass_adaptive") or "").strip()
                map_status = (row.get("map_pass_status") or "").strip()
                if map_flag not in ("0", "1"):
                    report.error(where, f"map_pass_other_half must be 0 or 1, got {map_flag!r}")
                    broken = True
                if map_adaptive_flag not in ("0", "1"):
                    report.error(where, f"map_pass_adaptive must be 0 or 1, got {map_adaptive_flag!r}")
                    broken = True
                if map_status not in MAP_PASS_STATUSES:
                    report.error(where, f"map_pass_status unknown: {map_status!r}")
                    broken = True
                if not broken:
                    optional = optional or {}
                    optional.update({
                        "map_pass": map_flag == "1",
                        "map_pass_adaptive": map_adaptive_flag == "1",
                        "map_pass_status": map_status,
                    })

            if broken:
                continue

            if values["width"] != file_w or values["height"] != file_h:
                report.error(where, f"row size {values['width']}x{values['height']} "
                                    f"does not match file name {file_w}x{file_h}")

            n = values["n"]
            if n < 1 or n > nmax:
                report.error(where, f"n={n} outside configured range 1..{nmax}")
                continue

            for field in OK_COUNT_FIELDS:
                if values[field] < 0:
                    report.error(where, f"{field} is negative: {values[field]}")
            for field in ("switched", "tolled", "equal_cost"):
                if values[field] > values["combos"]:
                    report.error(where, f"{field}={values[field]} exceeds combos={values['combos']}")
            if values["switched"] + values["tolled"] != values["combos"]:
                report.warn(where, f"switched+tolled={values['switched'] + values['tolled']} "
                                   f"!= combos={values['combos']}")

            if optional is not None:
                if optional["passing_combinations"] > values["combos"]:
                    report.error(where, "passing_combinations exceeds combos")
                if (optional["combos_expected"] != SATURATED_COMBOS
                        and values["combos"] > optional["combos_expected"]):
                    report.error(where, "combos exceeds combos_expected")
                if optional["cap_hit"] != (optional["combos_expected"] > combo_cap):
                    report.warn(where, "cap_hit disagrees with combos_expected vs combo cap")
                # P0 cross-checks: map-level flags must agree with the counts they derive from.
                if "map_pass" in optional:
                    if optional["map_pass"] != (optional["passing_combinations"] > 0):
                        report.error(where, "map_pass_other_half disagrees with passing_combinations")
                    expected_status = ("pass" if optional["passing_combinations"] > 0
                                       else "unknown_truncated" if optional["cap_hit"]
                                       else "fail_complete")
                    if optional["map_pass_status"] != expected_status:
                        report.error(where, f"map_pass_status {optional['map_pass_status']!r} is "
                                            f"inconsistent with passing_combinations/cap_hit")
                    if "passing_adaptive_combinations" in optional:
                        if optional["map_pass_adaptive"] != (
                                optional["passing_adaptive_combinations"] > 0):
                            report.error(where, "map_pass_adaptive disagrees with "
                                                "passing_adaptive_combinations")

            best = {}
            for field in BEST_FLOAT_FIELDS:
                raw = row.get(field)
                if raw is None or str(raw).strip() == "":
                    best[field] = None
                else:
                    value = as_float(raw)
                    if value is None:
                        report.error(where, f"{field} is not numeric: {raw!r}")
                    best[field] = value
            found = best["best_overlap"] is not None
            if found and any(value is None for value in best.values()):
                report.warn(where, "best sample is only partially populated")

            map_values = {field: values[field] for field in MAP_INT_FIELDS}
            if entry["map"] is None:
                entry["map"] = map_values
            elif entry["map"] != map_values:
                report.error(where, f"seed {seed} has inconsistent map-level fields across n rows")

            if n in entry["ns"]:
                report.error(where, f"duplicate row for seed {seed}, n={n}")
                continue
            entry["ns"][n] = {
                "values": values,
                "pass": pass_flag == "1",
                "best": best,
                "best_found": found,
                "optional": optional,
            }
            if optional is not None and n == 1:
                stats["versions"][optional["metric_version"]] += 1

    ok_maps = [seed for seed, entry in maps.items() if entry["status"] == OK_STATUS]
    failed = stats["status"].get("generation_failed", 0)
    unreachable = stats["status"].get("unreachable", 0)

    if expect_maps and len(maps) != expect_maps:
        report.error(name, f"map count {len(maps)} != expected {expect_maps}")
    if len(maps) != len(ok_maps) + failed + unreachable:
        report.error(name, "seed set and status counts disagree")

    expected_ns = set(range(1, nmax + 1))
    for seed in ok_maps:
        missing = expected_ns - set(maps[seed]["ns"])
        if missing:
            report.error(name, f"seed {seed} is missing n rows: {sorted(missing)}")

    if len(stats["versions"]) > 1:
        report.warn(name, f"mixed metric_version values {dict(stats['versions'])}: "
                          "pre/post P0 calibers must not be merged into one rate table")

    return {
        "name": name,
        "width": file_w,
        "height": file_h,
        "maps": maps,
        "ok_maps": ok_maps,
        "failed": failed,
        "unreachable": unreachable,
        "stats": stats,
    }


def aggregate(parsed, nmax, combo_cap):
    """Aggregate one size: map-level metrics plus per-n blocking metrics."""
    ok = [parsed["maps"][seed] for seed in parsed["ok_maps"]]
    analysable = len(ok)

    sums = defaultdict(float)
    for entry in ok:
        for field in ("obstacles", "printed_moves", "canonical_moves", "turns", "choke_count"):
            sums[field] += entry["map"][field]
        sums["printed_matches"] += entry["map"]["printed_matches"]

    mincut_hist = Counter(entry["map"]["mincut_natural"] for entry in ok)
    predictions = Counter()
    pred_no_alternative = 0
    pred_invalid = 0
    for entry in ok:
        value = entry["map"]["n_switch_pred"]
        if value >= 0:
            predictions[value] += 1
        elif value == -1:
            pred_no_alternative += 1
        else:
            pred_invalid += 1

    per_n = {}
    for n in range(1, nmax + 1):
        rows = [entry["ns"][n] for entry in ok if n in entry["ns"]]
        agg = {
            "rows": len(rows),
            "best_found": sum(1 for row in rows if row["best_found"]),
            "combos": sum(row["values"]["combos"] for row in rows),
            "switched": sum(row["values"]["switched"] for row in rows),
            "tolled": sum(row["values"]["tolled"] for row in rows),
            "equal_cost": sum(row["values"]["equal_cost"] for row in rows),
            "pass_maps": sum(1 for row in rows if row["pass"]),
        }
        rate_rows = [row for row in rows if row["values"]["combos"] > 0]
        agg["rate_maps"] = len(rate_rows)
        agg["switched_map_mean"] = (
            sum(row["values"]["switched"] / row["values"]["combos"] for row in rate_rows)
            / len(rate_rows)) if rate_rows else None
        agg["tolled_map_mean"] = (
            sum(row["values"]["tolled"] / row["values"]["combos"] for row in rate_rows)
            / len(rate_rows)) if rate_rows else None
        agg["equal_map_mean"] = (
            sum(row["values"]["equal_cost"] / row["values"]["combos"] for row in rate_rows)
            / len(rate_rows)) if rate_rows else None

        agg["combo_weighted_switched"] = agg["switched"] / agg["combos"] if agg["combos"] else None
        agg["combo_weighted_tolled"] = agg["tolled"] / agg["combos"] if agg["combos"] else None
        agg["combo_weighted_equal"] = agg["equal_cost"] / agg["combos"] if agg["combos"] else None

        best_valid = {}
        best_sum = {}
        for field in BEST_FLOAT_FIELDS:
            values = [row["best"][field] for row in rows if row["best"][field] is not None]
            best_valid[field] = len(values)
            best_sum[field] = sum(values)
        agg["best_valid"] = best_valid
        agg["best_among"] = {
            field: (best_sum[field] / best_valid[field] if best_valid[field] else None)
            for field in BEST_FLOAT_FIELDS
        }
        agg["best_per_map"] = {
            field: (best_sum[field] / analysable if analysable else None)
            for field in BEST_FLOAT_FIELDS
        }
        agg["best_coverage"] = (
            sum(1 for row in rows if row["best_found"]) / len(rows)) if rows else None

        combos_values = [row["values"]["combos"] for row in rows]
        agg["max_combos"] = max(combos_values) if combos_values else None
        agg["cap_rows"] = sum(1 for value in combos_values if value >= combo_cap)
        agg["extended"] = bool(rows) and all(row["optional"] is not None for row in rows)
        if agg["extended"]:
            options = [row["optional"] for row in rows]
            agg["passing"] = sum(item["passing_combinations"] for item in options)
            agg["combos_expected"] = sum(item["combos_expected"] for item in options)
            agg["expected_saturated_maps"] = sum(
                1 for item in options if item["combos_expected"] >= SATURATED_COMBOS)
            agg["cap_hit_maps"] = sum(1 for item in options if item["cap_hit"])
            agg["versions"] = Counter(item["metric_version"] for item in options)
            agg["map_pass_maps"] = sum(1 for item in options if item.get("map_pass"))
            agg["map_pass_adaptive_maps"] = sum(
                1 for item in options if item.get("map_pass_adaptive"))
            agg["unknown_truncated_maps"] = sum(
                1 for item in options if item.get("map_pass_status") == "unknown_truncated")
            agg["fail_complete_maps"] = sum(
                1 for item in options if item.get("map_pass_status") == "fail_complete")
        per_n[n] = agg

    return {
        "analysable": analysable,
        "sums": sums,
        "mincut_hist": mincut_hist,
        "prediction_hist": predictions,
        "pred_no_alternative": pred_no_alternative,
        "pred_invalid": pred_invalid,
        "per_n": per_n,
    }


def build_size_row(parsed, agg):
    analysable = agg["analysable"]
    sums = agg["sums"]
    divisor = analysable if analysable else None

    def avg(total):
        return total / divisor if divisor else None

    prediction_valid = sum(agg["prediction_hist"].values())
    prediction_total = prediction_valid + agg["pred_no_alternative"] + agg["pred_invalid"]
    area = parsed["width"] * parsed["height"]
    avg_obstacles = avg(sums["obstacles"])
    return {
        "size": f"{parsed['width']}x{parsed['height']}",
        "width": parsed["width"],
        "height": parsed["height"],
        "seed_count": len(parsed["maps"]),
        "generated_maps": analysable,
        "generation_failures": parsed["failed"],
        "unreachable_maps": parsed["unreachable"],
        "avg_obstacles": cell(avg_obstacles, ".2f"),
        "avg_obstacle_ratio": cell(avg_obstacles / area if avg_obstacles is not None else None, ".4f"),
        "avg_printed_moves": cell(avg(sums["printed_moves"]), ".2f"),
        "avg_canonical_moves": cell(avg(sums["canonical_moves"]), ".2f"),
        "avg_turns": cell(avg(sums["turns"]), ".2f"),
        "avg_choke_count": cell(avg(sums["choke_count"]), ".2f"),
        "printed_match_maps": int(sums["printed_matches"]),
        "printed_match_rate": cell(avg(sums["printed_matches"]), ".4f"),
        "mincut_distribution": "|".join(f"{key}:{value}" for key, value in sorted(agg["mincut_hist"].items())),
        "prediction_valid": prediction_valid,
        "prediction_no_alternative": agg["pred_no_alternative"],
        "prediction_invalid": agg["pred_invalid"],
        "prediction_coverage": cell(
            prediction_valid / prediction_total if prediction_total else None, ".4f"),
        "prediction_distribution": "|".join(
            f"{key}:{value}" for key, value in sorted(agg["prediction_hist"].items())),
        "extended_columns": "1" if agg["per_n"][1]["extended"] else "0",
        "metric_versions": "|".join(
            f"{key}:{value}" for key, value in sorted(
                agg["per_n"][1].get("versions", {}).items())),
    }


def build_n_row(parsed, agg, n):
    data = agg["per_n"][n]
    analysable = agg["analysable"]
    best = data["best_among"]
    per_map = data["best_per_map"]
    cap_state = "no_combinations" if data["max_combos"] == 0 else (
        "possible_truncation" if data["cap_rows"] else "not_reached")
    extended = data["extended"]
    if extended:
        cap_state = "truncated" if data["cap_hit_maps"] else "complete"
    passing = data.get("passing") if extended else None
    return {
        "size": f"{parsed['width']}x{parsed['height']}",
        "n": n,
        "analysable_maps": analysable,
        "best_sample_found": data["best_found"],
        "best_sample_coverage": cell(data["best_coverage"], ".4f"),
        "combos_total": data["combos"],
        "combos_expected_total": data.get("combos_expected", "") if extended else "",
        "expected_saturated_maps": data.get("expected_saturated_maps", "") if extended else "",
        "cap_hit_maps": data.get("cap_hit_maps", "") if extended else "",
        "passing_combinations": passing if passing is not None else "",
        "combo_pass_rate": cell(passing / data["combos"]
                                if passing is not None and data["combos"] else None, ".4f"),
        "combos_per_map": cell(data["combos"] / analysable if analysable else None, ".2f"),
        "switched": data["switched"],
        "tolled": data["tolled"],
        "equal_cost": data["equal_cost"],
        "switched_rate_combo_weighted": cell(data["combo_weighted_switched"], ".4f"),
        "switched_rate_map_mean": cell(data["switched_map_mean"], ".4f"),
        "tolled_rate_combo_weighted": cell(data["combo_weighted_tolled"], ".4f"),
        "tolled_rate_map_mean": cell(data["tolled_map_mean"], ".4f"),
        "equal_cost_rate_combo_weighted": cell(data["combo_weighted_equal"], ".4f"),
        "equal_cost_rate_map_mean": cell(data["equal_map_mean"], ".4f"),
        "rate_denominator_maps": data["rate_maps"],
        "map_pass_maps": data["pass_maps"],
        "map_pass_rate": cell(data["pass_maps"] / analysable if analysable else None, ".4f"),
        "map_level_pass_maps": data.get("map_pass_maps", "") if extended else "",
        "map_level_pass_rate": cell(
            data["map_pass_maps"] / analysable if extended and analysable else None, ".4f"),
        "map_pass_adaptive_maps": data.get("map_pass_adaptive_maps", "") if extended else "",
        "unknown_truncated_maps": data.get("unknown_truncated_maps", "") if extended else "",
        "fail_complete_maps": data.get("fail_complete_maps", "") if extended else "",
        "avg_overlap_among_best": cell(best["best_overlap"], ".4f"),
        "avg_half_sep_among_best": cell(best["best_half_sep"], ".4f"),
        "avg_delta_among_best": cell(best["best_delta"], ".4f"),
        "avg_shift_among_best": cell(best["best_shift"], ".4f"),
        "avg_killzone_gain_among_best": cell(best["best_killzone_gain"], ".4f"),
        "avg_overlap_per_map": cell(per_map["best_overlap"], ".4f"),
        "avg_half_sep_per_map": cell(per_map["best_half_sep"], ".4f"),
        "avg_delta_per_map": cell(per_map["best_delta"], ".4f"),
        "avg_shift_per_map": cell(per_map["best_shift"], ".4f"),
        "avg_killzone_gain_per_map": cell(per_map["best_killzone_gain"], ".4f"),
        "combo_cap_reached_rows": data["cap_rows"],
        "max_combos": data["max_combos"] if data["max_combos"] is not None else "",
        "combo_cap_probe": cap_state,
        "metric_versions": "|".join(
            f"{key}:{value}" for key, value in sorted(data.get("versions", {}).items())),
    }


def build_map_rows(parsed, nmax):
    rows = []
    for seed, entry in parsed["maps"].items():
        base = {
            "size": f"{parsed['width']}x{parsed['height']}",
            "seed": seed,
            "status": entry["status"],
        }
        for field in MAP_INT_FIELDS:
            base[field] = ""
        base["prediction_status"] = ""
        base["metric_version"] = ""
        if entry["map"] is not None:
            for field in MAP_INT_FIELDS:
                base[field] = entry["map"][field]
            pred = entry["map"]["n_switch_pred"]
            base["prediction_status"] = (
                "valid" if pred >= 0 else "no_alternative" if pred == -1 else "absent")
            first = entry["ns"].get(1)
            if first and first["optional"] is not None:
                base["metric_version"] = first["optional"]["metric_version"]
        for n in range(1, nmax + 1):
            row = entry["ns"].get(n)
            prefix = f"n{n}_"
            optional = row["optional"] if row else None
            base[prefix + "combos"] = row["values"]["combos"] if row else ""
            base[prefix + "switched"] = row["values"]["switched"] if row else ""
            base[prefix + "tolled"] = row["values"]["tolled"] if row else ""
            base[prefix + "equal_cost"] = row["values"]["equal_cost"] if row else ""
            base[prefix + "combos_expected"] = (optional["combos_expected"] if optional else "")
            base[prefix + "cap_hit"] = (
                ("1" if optional["cap_hit"] else "0") if optional else "")
            base[prefix + "passing_combinations"] = (
                optional["passing_combinations"] if optional else "")
            base[prefix + "passes_other_half"] = (("1" if row["pass"] else "0") if row else "")
            base[prefix + "map_pass_other_half"] = (opt_text(optional, "map_pass") if row else "")
            base[prefix + "map_pass_adaptive"] = (opt_text(optional, "map_pass_adaptive") if row else "")
            base[prefix + "map_pass_status"] = (opt_text(optional, "map_pass_status") if row else "")
            base[prefix + "passes_adaptive"] = (opt_text(optional, "passes_adaptive") if row else "")
            base[prefix + "passing_adaptive_combinations"] = (
                opt_text(optional, "passing_adaptive_combinations") if row else "")
            base[prefix + "half_sep_tier"] = (opt_text(optional, "half_sep_tier") if row else "")
            base[prefix + "adaptive_delta_max"] = (
                cell(optional["adaptive_delta_max"], ".3f")
                if optional and "adaptive_delta_max" in optional else "")
            base[prefix + "best_sample_found"] = (("1" if row["best_found"] else "0") if row else "")
            for field in BEST_FLOAT_FIELDS:
                value = row["best"][field] if row else None
                base[prefix + field] = cell(value, ".3f" if field != "best_shift" else ".2f")
        rows.append(base)
    return rows


def build_map_n_rows(parsed):
    rows = []
    for seed, entry in parsed["maps"].items():
        if entry["map"] is None:
            continue
        for n, row in sorted(entry["ns"].items()):
            out = {
                "size": f"{parsed['width']}x{parsed['height']}",
                "seed": seed,
                "status": entry["status"],
            }
            out.update(entry["map"])
            out["n"] = n
            out.update(row["values"])
            out["passes_other_half"] = "1" if row["pass"] else "0"
            optional = row["optional"]
            out["map_pass_other_half"] = opt_text(optional, "map_pass")
            out["map_pass_adaptive"] = opt_text(optional, "map_pass_adaptive")
            out["map_pass_status"] = opt_text(optional, "map_pass_status")
            out["best_sample_found"] = "1" if row["best_found"] else "0"
            out["combos_expected"] = optional["combos_expected"] if optional else ""
            out["cap_hit"] = ("1" if optional["cap_hit"] else "0") if optional else ""
            out["passing_combinations"] = optional["passing_combinations"] if optional else ""
            out["metric_version"] = optional["metric_version"] if optional else ""
            for field in BEST_FLOAT_FIELDS:
                out[field] = cell(row["best"][field], ".3f" if field != "best_shift" else ".2f")
            rows.append(out)
    return rows


def write_csv(path, rows):
    if not rows:
        return
    fields = list(rows[0].keys())
    with open(path, "w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def write_report(path, report, per_file, args):
    lines = [
        "Sweep data quality report (stage 2)",
        f"input: {os.path.abspath(args.input)}",
        f"nmax: {args.nmax} | expect_maps: {args.expect_maps or 'not enforced'} | combo_cap: {args.combo_cap}",
        "",
        "== files ==",
    ]
    for name in report.processed:
        lines.append(f"processed: {name}")
    for name in report.skipped:
        lines.append(f"skipped:   {name}")
    lines.append("")
    lines.append("== per file ==")
    for name, stats in per_file.items():
        lines.append(
            f"{name}: rows={stats['rows']} maps={stats['maps']} ok={stats['ok']} "
            f"generation_failed={stats['failed']} unreachable={stats['unreachable']} "
            f"best_sample_coverage={display(stats['coverage'], '.1%')} "
            f"prediction_coverage={display(stats['prediction_coverage'], '.1%')} "
            f"extended={'yes' if stats['extended'] else 'no'} "
            f"p0_map_level={'yes' if stats.get('p0') else 'no'} "
            f"metric_versions={stats['versions'] or 'none'}")
    lines.append("")
    lines.append(f"== hard errors ({len(report.errors)}) ==")
    lines.extend(report.errors)
    lines.append("")
    lines.append(f"== warnings ({len(report.warnings)}) ==")
    lines.extend(report.warnings)
    lines.append("")
    lines.append("== notes ==")
    lines.append("denominators: *_among_best divides by valid samples of that field; "
                 "*_per_map divides by analysable maps (missing best contributes 0).")
    lines.append("rates: *_combo_weighted = sum(event)/sum(combos); "
                 "*_map_mean = mean over maps of (event_i/combos_i), combos_i > 0 only.")
    lines.append("equal_cost: per-combination count where the blocked shortest-path cost "
                 "equals the original cost (src/MapAnalyzer.cs L422).")
    lines.append("n_switch_pred: map-level ceil((adjusted alternative corridor cost - base cost) "
                 "/(W-1)); -1 = no independent alternative corridor. "
                 "It is not an enumerated minimum blocker count (src/MapAnalyzer.cs L493-498).")
    lines.append("combo_cap_probe: not_reached / possible_truncation / no_combinations; "
                 "only a line-level probe, not proof of full enumeration.")
    lines.append("extended columns (stage 2): combos_expected/cap_hit/passing_combinations/"
                 "metric_version make combo_cap_probe exact (complete|truncated) and add "
                 "combo_pass_rate = passing_combinations/combos.")
    lines.append("p0 map-level columns (p1-v4): map_pass_other_half = passing_combinations > 0; "
                 "map_pass_status in {pass, fail_complete, unknown_truncated}. "
                 "map_pass_rate counts best-sample passes (legacy); map_level_pass_rate counts "
                 "map-level passes; unknown_truncated is inconclusive and never a plain failure. "
                 "passes_other_half/passes_adaptive remain best-sample display columns.")
    lines.append("combos_expected is C(candidates, n) saturated at 2^63-1; "
                 "expected_saturated_maps counts saturated rows.")
    lines.extend(report.infos)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--outdir", required=True)
    parser.add_argument("--nmax", type=int, default=2)
    parser.add_argument("--expect-maps", type=int, default=0,
                        help="expected maps per size; 0 disables the check")
    parser.add_argument("--combo-cap", type=int, default=4000)
    args = parser.parse_args()

    if args.nmax < 1:
        print("--nmax must be at least 1", file=sys.stderr)
        return 1

    report = Report()
    per_file = {}
    parsed_files = []

    candidates = sorted(glob.glob(os.path.join(args.input, "*.csv")))
    for path in candidates:
        size = parse_size_from_name(path)
        if size is None:
            report.skipped.append(os.path.basename(path))
            continue
        parsed = load_file(path, size[0], size[1], args.nmax, args.expect_maps,
                           args.combo_cap, report)
        report.processed.append(parsed["name"])
        parsed_files.append(parsed)
        agg = aggregate(parsed, args.nmax, args.combo_cap)
        parsed_files[-1]["agg"] = agg
        per_file[parsed["name"]] = {
            "rows": parsed["stats"]["rows"],
            "maps": len(parsed["maps"]),
            "ok": len(parsed["ok_maps"]),
            "failed": parsed["failed"],
            "unreachable": parsed["unreachable"],
            "extended": parsed["stats"]["extended"],
            "p0": parsed["stats"]["p0"],
            "versions": dict(parsed["stats"]["versions"]),
            "coverage": (sum(agg["per_n"][n]["best_found"] for n in agg["per_n"])
                         / max(1, sum(agg["per_n"][n]["rows"] for n in agg["per_n"]))),
            "prediction_coverage": (
                sum(agg["prediction_hist"].values())
                / max(1, len(parsed["ok_maps"]))),
        }

    os.makedirs(args.outdir, exist_ok=True)
    report_path = os.path.join(args.outdir, "data_quality_report.txt")

    if not parsed_files:
        report.error("input", "no sweep_<N>x<N>.csv files found")
    if report.errors:
        write_report(report_path, report, per_file, args)
        print(f"Data quality hard errors: {len(report.errors)}. Formal summaries were not written.")
        for line in report.errors[:20]:
            print("  " + line)
        print(f"report: {os.path.abspath(report_path)}")
        return 2

    size_rows = []
    n_rows = []
    map_rows = []
    map_n_rows = []
    for parsed in parsed_files:
        agg = parsed["agg"]
        size_rows.append(build_size_row(parsed, agg))
        for n in range(1, args.nmax + 1):
            n_rows.append(build_n_row(parsed, agg, n))
        map_rows.extend(build_map_rows(parsed, args.nmax))
        map_n_rows.extend(build_map_n_rows(parsed))

    write_csv(os.path.join(args.outdir, "summary_by_size.csv"), size_rows)
    write_csv(os.path.join(args.outdir, "summary_by_n.csv"), n_rows)
    write_csv(os.path.join(args.outdir, "summary_by_map.csv"), map_rows)
    write_csv(os.path.join(args.outdir, "summary_by_map_n.csv"), map_n_rows)
    write_report(report_path, report, per_file, args)

    print("size | maps | ok | fail | obst% | moves | turns | choke | match | pred_cov")
    for row in size_rows:
        print("{size} | {seed_count} | {generated_maps} | {generation_failures} | "
              "{ratio} | {moves} | {turns} | {choke} | {match} | {cov}".format(
                  size=row["size"], seed_count=row["seed_count"],
                  generated_maps=row["generated_maps"],
                  generation_failures=row["generation_failures"],
                  ratio=display(float(row["avg_obstacle_ratio"]) if row["avg_obstacle_ratio"] else None, ".2%"),
                  moves=display(float(row["avg_printed_moves"]) if row["avg_printed_moves"] else None, ".2f"),
                  turns=display(float(row["avg_turns"]) if row["avg_turns"] else None, ".2f"),
                  choke=display(float(row["avg_choke_count"]) if row["avg_choke_count"] else None, ".2f"),
                  match=display(float(row["printed_match_rate"]) if row["printed_match_rate"] else None, ".1%"),
                  cov=display(float(row["prediction_coverage"]) if row["prediction_coverage"] else None, ".1%")))

    print()
    print("size | n | combos | best_cov | pass%(legacy) | maplvl_pass% | unknown | fail_complete | "
          "ovl_best | ovl_map | half_best | delta_best | tol% | swi% | swi_map% | eq% | eq_map%")
    for row in n_rows:
        def num(key, default=None):
            return float(row[key]) if row[key] != "" else default
        print("{size} | {n} | {combos} | {cov} | {passr} | {mlvl} | {unk} | {fc} | {ob} | {om} | {hb} | "
              "{db} | {tol} | {swi} | {swm} | {eq} | {eqm}".format(
                  size=row["size"], n=row["n"], combos=row["combos_total"],
                  cov=display(num("best_sample_coverage"), ".1%"),
                  passr=display(num("map_pass_rate"), ".1%"),
                  mlvl=display(num("map_level_pass_rate"), ".1%"),
                  unk=row["unknown_truncated_maps"],
                  fc=row["fail_complete_maps"],
                  ob=display(num("avg_overlap_among_best"), ".3f"),
                  om=display(num("avg_overlap_per_map"), ".3f"),
                  hb=display(num("avg_half_sep_among_best"), ".3f"),
                  db=display(num("avg_delta_among_best"), ".1%"),
                  tol=display(num("tolled_rate_combo_weighted"), ".1%"),
                  swi=display(num("switched_rate_combo_weighted"), ".1%"),
                  swm=display(num("switched_rate_map_mean"), ".1%"),
                  eq=display(num("equal_cost_rate_combo_weighted"), ".1%"),
                  eqm=display(num("equal_cost_rate_map_mean"), ".1%")))

    print()
    print(f"warnings: {len(report.warnings)}")
    print("Wrote: summary_by_size.csv, summary_by_n.csv, summary_by_map.csv, "
          "summary_by_map_n.csv, data_quality_report.txt")
    print("outdir:", os.path.abspath(args.outdir))
    return 0


if __name__ == "__main__":
    sys.exit(main())
