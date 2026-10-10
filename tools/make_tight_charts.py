import json, math, os, sys
src = r"C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\approaches.json"
out = os.path.join(os.path.dirname(__file__), "charts"); os.makedirs(out, exist_ok=True)
M = 600000 * math.pi / 180   # Kerbin m per degree
all_ = json.load(open(src, encoding="utf-8-sig"))
# Aeris 3A, ~70-90 m/s at 1.5-2 g joins: r ~350-480 m -> 2.5 km downwind offset, base at the 12 km FAF line
OFF, UP, DWEND = 2500, 1500, 9500
RWY = ((-0.0485997, -74.724375), (-0.0485997, -74.4903))
for key in ("KSC 09", "KSC 27"):
    e = all_[key]; fx = {f["role"]: f for f in e["fixes"]}
    a, b = RWY if key.endswith("09") else RWY[::-1]   # threshold, far end (mod constants)
    cl = math.cos(math.radians(a[0]))
    ux, uy = (b[1] - a[1]) * cl * M, (b[0] - a[0]) * M; L = math.hypot(ux, uy); ux, uy = ux / L, uy / L
    tx, ty = a[1] * cl * M, a[0] * M
    def pt(along, lat_off):   # along: + = past threshold along course; lat_off: + = left of course
        x = tx + ux * along - uy * lat_off; y = ty + uy * along + ux * lat_off
        return round(y / M, 5), round(x / (cl * M), 5)
    fl, fo = pt(-12000, 0); sl, so = pt(-4000, 0)
    fixes = [{"role": "faf", "side": "both", "alt_ref": "agl", "name": "FAF (long final 12 km)", "lat": fl, "lon": fo, "alt": fx["faf"]["alt"]},
             {"role": "sf", "side": "both", "alt_ref": "agl", "name": "SF (short final 4 km)", "lat": sl, "lon": so, "alt": fx["sf"]["alt"]}]
    for side, s in (("left", 1), ("right", -1)):
        S = "L" if s > 0 else "R"
        for role, name, al, alt in (("dw_" + side, "IAF-%s (tight downwind)" % S, UP, 600), ("dwend_" + side, "DW-%s end" % S, -DWEND, 600), ("base_" + side, "BASE-%s (12 km)" % S, -12000, 650)):
            la, lo = pt(al, s * OFF); fixes.append({"role": role, "side": side, "alt_ref": "agl", "name": name, "lat": la, "lon": lo, "alt": alt})
    chart = {"alt_ref": "agl", "fixes": fixes, "variant": "tight", "note": "Aeris 3A, 1.5-2 g joins: 2.5 km downwind, base on the 12 km FAF line; FAF 12 km / SF 4 km kept",
             "flare_start_m": e.get("flare_start_m", 15), "flare_sink_ms": e.get("flare_sink_ms", 1), "touchdown_m": e.get("touchdown_m", 350), "tch_m": e.get("tch_m", 15)}
    p = os.path.join(out, key.replace(" ", "_") + "_tight.json"); json.dump(chart, open(p, "w"), indent=1); print(p, [ (f["role"], f["lat"], f["lon"]) for f in fixes])

