"""Generate tools/approach_map.html: editable instrument-approach charts (plates) for KSC/Island runway ends,
mirroring KSPChatMod/FlightMissions.cs ApproachChart, with today's flight track from the chat log [T] lines.
SAVE downloads approaches.json (put it in GameData/KSPChatBridge/PluginData; tools/import_approaches.ps1 copies it)."""
import json, math, re, sys, glob, os, datetime
R = 600000.0
G = 9.81
def offset(lat, lon, brg, d):
    la1, lo1, b, a = map(math.radians, (lat, lon, brg, 0)) ; a = d / R
    la2 = math.asin(math.sin(la1)*math.cos(a) + math.cos(la1)*math.sin(a)*math.cos(b))
    lo2 = lo1 + math.atan2(math.sin(b)*math.sin(a)*math.cos(la1), math.cos(a) - math.sin(la1)*math.sin(la2))
    return math.degrees(la2), math.degrees(lo2)
def bearing(la1, lo1, la2, lo2):
    la1, lo1, la2, lo2 = map(math.radians, (la1, lo1, la2, lo2))
    y = math.sin(lo2-lo1)*math.cos(la2); x = math.cos(la1)*math.sin(la2) - math.sin(la1)*math.cos(la2)*math.cos(lo2-lo1)
    return (math.degrees(math.atan2(y, x)) + 360) % 360
RUNWAYS = {"KSC": ((-0.0485997, -74.724375), (-0.0502119, -74.490300), 69.1),
           "Island": ((-1.516092, -71.856744), (-1.514809, -71.961815), 134.6)}
def chart(thr, end, elev, speed=150.0, bank=20.0):
    crs = bearing(thr[0], thr[1], end[0], end[1]); r = 1.3 * speed**2 / (G * math.tan(math.radians(bank))); tg = math.tan(math.radians(3))
    def at(behind, side):
        la, lo = offset(thr[0], thr[1], crs + 180, behind)
        if side: la, lo = offset(la, lo, crs + (90 if side > 0 else -90), abs(side))
        return la, lo
    fixes = []
    def add(role, name, behind, side, alt):
        la, lo = at(behind, side); fixes.append({"role": role, "side": "left" if role.endswith("_left") else "right" if role.endswith("_right") else "both", "name": name, "lat": round(la, 5), "lon": round(lo, 5), "alt": round(alt)})
    longAlt, shortAlt = elev + 12000 * tg, elev + 4000 * tg
    for s, nm in ((-1, "L"), (1, "R")):
        add("dw_" + ("left" if s < 0 else "right"), "IAF-%s (downwind)" % nm, 0, 2*r*s, longAlt)
        add("dwend_" + ("left" if s < 0 else "right"), "DW-%s (downwind end)" % nm, 12000, 2*r*s, longAlt)
        add("base_" + ("left" if s < 0 else "right"), "IF-%s (base)" % nm, 12000 + r, r*s, longAlt)
    add("faf", "FAF (long final 12 km)", 12000, 0, longAlt); add("sf", "SF (short final 4 km)", 4000, 0, shortAlt)
    return {"course": round(crs, 1), "elev": elev, "thr": thr, "end": end, "radius_m": round(r), "fixes": fixes,
            "flare_start_m": 15, "flare_sink_ms": 1, "touchdown_m": 350, "tch_m": 15}
def ends():
    out = {}
    for site, (a, b, elev) in RUNWAYS.items():
        for thr, end in ((a, b), (b, a)):
            crs = bearing(thr[0], thr[1], end[0], end[1]); key = site + " " + ("09" if abs(((crs - 90 + 180) % 360) - 180) < 45 else "27")
            out[key] = chart(thr, end, elev)
    return out
def track(logdir):
    pts = []
    for f in sorted(glob.glob(os.path.join(logdir, "chat_*.log")))[-1:]:
        for line in open(f, encoding="utf-8", errors="replace"):
            m = re.match(r"(\d\d:\d\d:\d\d)\.\d+ \[T\] alt=(-?\d+)m .*?lat=(-?[\d.]+) lon=(-?[\d.]+)", line)
            if m: pts.append([m.group(1), float(m.group(3)), float(m.group(4)), int(m.group(2))])
    return pts
def main():
    logdir = sys.argv[1] if len(sys.argv) > 1 else r"C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\logs"
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "approach_map.html")
    data = {"ends": ends(), "track": track(logdir), "generated": datetime.datetime.now().strftime("%Y-%m-%d %H:%M")}
    tpl = open(os.path.join(os.path.dirname(__file__), "approach_map_template.html"), encoding="utf-8").read()
    open(out, "w", encoding="utf-8").write(tpl.replace("/*DATA*/null", json.dumps(data)))
    print(out, len(data["track"]), "track points")
if __name__ == "__main__":
    main()
