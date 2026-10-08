"""Summarize docs/model_eval_raw.jsonl into a markdown table (printed)."""
import json
import re
import statistics
import sys
from collections import OrderedDict
from pathlib import Path

p = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "docs" / "model_eval_raw.jsonl")
rows = [json.loads(line) for line in p.read_text(encoding="utf-8").splitlines() if line.strip()]
by = OrderedDict()
for r in rows:
    by.setdefault(r["model"], []).append(r)
print("| Model | Tool choice (of 8) | Valid args | Unrequested burns | Format failures | Median / max latency | VRAM used (MB) | Load time |")
print("|---|---|---|---|---|---|---|---|")
for m, rs in by.items():
    if any("load_error" in r or "run_error" in r for r in rs) and not any("prompt" in r for r in rs):
        print(f"| {m} | load/run failed | | | | | | |")
        continue
    rs = [r for r in rs if "prompt" in r]
    ok = sum(r["expected_ok"] for r in rs)
    calls = sum(len(r["tools"]) for r in rs)
    bad = sum(r["bad_args"] for r in rs)
    forb = sum(len(r["forbidden"]) for r in rs)
    fmt = sum(1 for r in rs if r.get("format_fail") or re.search(r'"name"\s*:', r.get("reply") or ""))
    lat = [r["secs"] for r in rs]
    v = rs[0].get("vram_delta_mb")
    print(f"| {m} | {ok}/{len(rs)} | {calls - bad}/{calls} | {forb} | {fmt} | {statistics.median(lat):.1f} s / {max(lat):.0f} s | {v} | {rs[0].get('load_s')} s |")
print()
for m, rs in by.items():
    print(f"- {m}: " + "; ".join(f"{r['prompt'][:22]!r}->{','.join(r['tools']) or 'none'}{'' if r['expected_ok'] else ' (X)'}"
                                 for r in rs if "prompt" in r))
