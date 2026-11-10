"""The README's results from bench/run.ps1's results.json and the frame-rate lines vwbench_auto.lua
wrote (docs/benchmark.md): the tables, put into readme_results.md where it says @@TF2@@, @@GMOD@@,
@@TIME@@ and @@FPS@@.

Usage: python -I tables.py <results.json> <fps results.txt> [readme_results.md]
"""
import contextlib, io, json, os, re, statistics as st, sys

results = json.load(open(sys.argv[1]))
fps = {}
if len(sys.argv) > 2 and os.path.exists(sys.argv[2]):
    for line in open(sys.argv[2]):
        m = re.match(r"\[vwbench\] vwb_(.+)_(stripped|valve|viswright|plain|shipped) run\d+ frames \d+ seconds [\d.]+ avg ([\d.]+) low1 ([\d.]+)", line)
        if m:
            fps.setdefault((m[1], m[2]), []).append((float(m[3]), float(m[4])))

TF2 = ["ctf_2fort", "cp_dustbowl", "cp_badlands", "cp_granary", "cp_gravelpit", "tc_hydro", "pl_badwater", "pl_goldrush", "arena_lumberyard", "arena_ravine"]
GMOD = ["rp_downtown_v4c_v2", "rp_downtown_tits_v2", "rp_rockford_v2b", "rp_evocity_v33x"]


def runs(v, k):
    x = v.get(k)
    if isinstance(x, dict) and "runs" in x:
        return x["runs"]
    return [x] if isinstance(x, dict) and "pvsMean" in x else []


def med(rs, f):
    return st.median(r[f] for r in rs)


def pct(a, b):
    return f"{(a / b - 1) * 100:+.1f}%"


def n(x):
    return f"{x:,.0f}"


def secs(rs, f):
    vals = [r[f] for r in rs]
    lo, mid, hi = min(vals), st.median(vals), max(vals)
    return f"{mid:.1f} s" if hi - lo < 0.15 else f"{mid:.1f} s ({lo:.1f}-{hi:.1f})"


def visibility(maps, base, others, names):
    print(f"| Map | Points | {names[0]}: seen / drawn | " + " | ".join(f"{x}: seen / drawn" for x in names[1:]) + " |")
    print("| --- | ---: | ---: | " + " | ".join("---:" for _ in names[1:]) + " |")
    for m in maps:
        v = results[m]
        b = runs(v, base)
        bp, bd = med(b, "pvsMean"), med(b, "drawnMean")
        cells = []
        for k in others:
            rs = runs(v, k)
            cells.append(f"{pct(med(rs, 'pvsMean'), bp)} / {pct(med(rs, 'drawnMean'), bd)}" if rs else "-")
        print(f"| {m} | {n(b[0]['points'])} | {n(bp)} / {n(bd)} | " + " | ".join(cells) + " |")


def compile_times(maps, variants, names):
    print("| Map | " + " | ".join(f"{x} VVIS" for x in names) + " | Viswright's own run |")
    print("| --- | " + " | ".join("---:" for _ in names) + " | ---: |")
    for m in maps:
        v = results[m]
        cells = [secs(runs(v, k), "vvisSeconds") if runs(v, k) and "vvisSeconds" in runs(v, k)[0] else "-" for k in variants]
        print(f"| {m} | " + " | ".join(cells) + f" | {v['viswrightSeconds'] / 60:.0f} min |")


def frame_rates(maps, variants, names):
    print("| Map | " + " | ".join(f"{x}: avg / 1% low" for x in names) + " |")
    print("| --- | " + " | ".join("---:" for _ in names) + " |")
    for m in maps:
        cells = []
        for k in variants:
            r = fps.get((m, k))
            cells.append(f"{st.median(a for a, _ in r):.0f} / {st.median(l for _, l in r):.0f}" if r else "-")
        print(f"| {m} | " + " | ".join(cells) + " |")


def section(write):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        write()
    return out.getvalue().rstrip("\n")


def all_times():
    compile_times(TF2, ["stripped", "valve", "viswright"], ["Stripped", "Valve", "Viswright"])
    print()
    compile_times(GMOD, ["plain", "viswright"], ["Plain", "Viswright"])


def all_fps():
    frame_rates(TF2, ["stripped", "valve", "viswright"], ["Stripped", "Valve", "Viswright"])
    print()
    frame_rates(GMOD, ["plain", "shipped", "viswright"], ["Plain", "Shipped", "Viswright"])


parts = {
    "@@TF2@@": section(lambda: visibility(TF2, "stripped", ["valve", "viswright"], ["Stripped", "Valve", "Viswright"])),
    "@@GMOD@@": section(lambda: visibility(GMOD, "plain", ["shipped", "viswright"], ["Plain", "Shipped", "Viswright"])),
    "@@TIME@@": section(all_times),
    "@@FPS@@": section(all_fps),
}
template = open(sys.argv[3], encoding="utf-8").read() if len(sys.argv) > 3 else "\n\n".join(parts) + "\n"
for key, text in parts.items():
    template = template.replace(key, text)
sys.stdout.reconfigure(encoding="utf-8")
print(template, end="")
