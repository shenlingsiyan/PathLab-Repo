#!/usr/bin/env python3
"""等价性校验（P8 引入，P9 泛化）：新 CSV 与基线按列名对齐逐字节一致（追加列除外）。

追加列 = 新表头中基线没有的列，按表尾顺序自动推断并要求「基线列 + 追加列 = 新表头」。
P9 语义：diff_* 5 列照旧；表尾最后一列（theta_flank）仅 θ 行填写（--theta-col filled）或全空（empty）。

用法: python tools/check_p8_columns.py <new.csv> <baseline.csv>
      [--mv p9-v7] [--theta-col filled|empty] [--theta-no-u]
"""
import csv
import sys

DIFF_COLS = ["diff_d1", "diff_d2_open", "diff_d3_ucount", "diff_d3_usize", "diff_score"]


def main() -> int:
    args = sys.argv[1:]
    new_path, base_path = args[0], args[1]
    theta_no_u = "--theta-no-u" in args
    expected_mv = None
    if "--mv" in args:
        expected_mv = args[args.index("--mv") + 1]
    theta_col = None
    if "--theta-col" in args:
        theta_col = args[args.index("--theta-col") + 1]

    with open(new_path, newline="", encoding="utf-8") as f:
        new_rows = list(csv.reader(f))
    with open(base_path, newline="", encoding="utf-8") as f:
        base_rows = list(csv.reader(f))

    new_header, base_header = new_rows[0], base_rows[0]
    base_idx = {c: i for i, c in enumerate(base_header)}
    mv_idx = new_header.index("metric_version")
    uc_idx = new_header.index("diff_d3_ucount")

    appended = [c for c in new_header if c not in base_idx]
    ok = True
    if new_header != base_header + appended or not appended:
        print(f"FAIL header: new({len(new_header)}) != base({len(base_header)})+{appended}")
        ok = False
    if len(new_rows) != len(base_rows):
        print(f"FAIL row count: {len(new_rows)} vs {len(base_rows)}")
        ok = False

    app_idx = {c: new_header.index(c) for c in appended}
    diff_idx = [new_header.index(c) for c in DIFF_COLS]
    td_idx = new_header.index(appended[-1]) if appended else None

    diff_nonempty = 0
    u_nonzero = 0
    td_bad = 0
    for r, (new, base) in enumerate(zip(new_rows[1:], base_rows[1:]), start=2):
        for name, bi in base_idx.items():
            if name == "metric_version":
                continue
            if new[new_header.index(name)] != base[bi]:
                print(f"FAIL line {r} col {name}: new={new[new_header.index(name)]!r} base={base[bi]!r}")
                ok = False
        if expected_mv and new[mv_idx] != expected_mv:
            print(f"FAIL line {r}: metric_version={new[mv_idx]} (expected {expected_mv})")
            ok = False
        if len(new) == len(new_header) and all(new[i] != "" for i in diff_idx):
            diff_nonempty += 1
        if theta_no_u and new[uc_idx] not in ("", "0"):
            u_nonzero += 1
        if td_idx is not None:
            v = new[td_idx] if len(new) > td_idx else ""
            if theta_col == "filled" and v == "":
                td_bad += 1
            if theta_col == "empty" and v != "":
                td_bad += 1

    if diff_nonempty and diff_nonempty != len(new_rows) - 1:
        print(f"NOTE: diff cols filled on {diff_nonempty}/{len(new_rows)-1} rows (ok-with-best rows only)")
    if theta_no_u and u_nonzero:
        print(f"FAIL theta u_count nonzero on {u_nonzero} rows")
        ok = False
    if td_bad:
        print(f"FAIL theta col policy '{theta_col}' violated on {td_bad} rows")
        ok = False

    print("PASS: old columns byte-identical"
          + (f" + mv {expected_mv}" if expected_mv else "")
          + (f" + theta col {theta_col}" if theta_col else "")
          + (" + theta u all zero" if theta_no_u else "")
          if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
