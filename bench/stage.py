"""Copy every variant's lit BSP into garrysmod/maps as vwb_<map>_<variant>.bsp, write each map's camera path
into garrysmod/data/vwbench/<map>.json, and list the runs in garrysmod/data/vwbench/plan.json."""
import json, math, os, shutil, subprocess, sys

bench, gmod, pathpy = sys.argv[1], sys.argv[2], sys.argv[3]
out = os.path.join(bench, "out")
results = json.load(open(os.path.join(out, "results.json")))
maps_dir = os.path.join(gmod, "maps")
data_dir = os.path.join(gmod, "data", "vwbench")
os.makedirs(data_dir, exist_ok=True)

plan = []
for m, v in results.items():
    variants = ["stripped", "valve", "viswright"] if v["game"] == "tf2" else ["plain", "shipped", "viswright"]
    path_file = os.path.join(data_dir, f"{m}.json")
    subprocess.run([sys.executable, "-I", pathpy, os.path.join(out, m, "points.json"), path_file], check=True)
    pts = json.load(open(path_file))
    length = sum(math.dist(a, b) for a, b in zip(pts, pts[1:]))
    speed = max(400, round(length / 90))  # about a minute and a half a run, the same for every variant of the map
    for var in variants:
        if var == "shipped":
            src = os.path.join(bench, "sources", "gmod", f"{m}.bsp")
        else:
            src = os.path.join(out, m, var, f"vwb_{m}_{var}.bsp")
        if not os.path.exists(src):
            print(f"missing {m} {var}")
            continue
        name = f"vwb_{m}_{var}"
        shutil.copyfile(src, os.path.join(maps_dir, name + ".bsp"))
        plan.append({"map": name, "path": f"vwbench/{m}.json", "speed": speed})
    print(f"{m}: path {length:.0f} units at {speed} u/s")

json.dump(plan, open(os.path.join(data_dir, "plan.json"), "w"), indent=1)
print(f"{len(plan)} variants staged")
