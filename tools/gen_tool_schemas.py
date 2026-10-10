"""Generate KSPChatMod/NativeToolSchemas.cs: OpenAI tool schemas for every NativeCommands.Ported tool, from the
bridge's python signatures/docstrings (kspchat.ksp_actions.BY_NAME). Run: python tools/gen_tool_schemas.py"""
import inspect, json, os, re, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
OUT = os.path.join(ROOT, "KSPChatMod", "NativeToolSchemas.cs")
TYPES = {float: "number", int: "integer", bool: "boolean", str: "string"}

# Native-only tools (no bridge twin): name -> (description, {param: type}, [required])
NATIVE_ONLY = {
    "hold_pattern": ("Circle a place (saved spot name, or here) at a set altitude until told otherwise.",
                     {"name": "string", "altitude_m": "number", "radius_m": "number", "direction": "string"}, []),
    "fuel_check_return": ("Watch fuel and fly home automatically (then land) when what's left only just covers the trip home; off=true disarms.",
                          {"home": "string", "reserve_pct": "number", "off": "boolean"}, []),
    "formation": ("AI wingman: a second loaded plane (within ~2.3 km) flies echelon on us; off=true releases it.",
                  {"wingman": "string", "side": "string", "spacing_m": "number", "off": "boolean"}, []),
    "scan_coverage": ("SCANsat: map coverage per scanner type for a body (default: current), plus active scanners aboard. Needs SCANsat.",
                      {"body": "string"}, []),
    "mapping_orbit": ("SCANsat: plan a polar mapping orbit for the scanners aboard; execute=true runs the next burn step. Needs SCANsat.",
                      {"execute": "boolean"}, []),
    "tech_advisor": ("R&D advice: call with no goal to ask Luke his plan; with a goal (Mun landing, planes, science...) returns the cheapest unlock path and highlights it.",
                     {"goal": "string"}, []),
    "drive_to_building": ("Rover: drive to KSC buildings (VAB, SPH, R&D, Tracking Station, Mission Control, Astronaut Complex, Administration, Launch Pad, Runway; ';'-list or all) and do science at each.",
                          {"buildings": "string", "science": "boolean", "speed": "number"}, []),
    "roll": ("Roll/bank the plane: direction left/right and degrees, inverted=true for upside down, level=true for wings level. Bank rule 20 deg (10 fast) unless override=true.",
             {"direction": "string", "degrees": "number", "inverted": "boolean", "level": "boolean", "override": "boolean"}, []),
    "autopilot": ("Turn the autopilot on (hold current altitude/heading/speed) or off (on=false).", {"on": "boolean"}, []),
    "make_flight_plan": ("Write and fly a flight plan. Prefer plan = lines of: takeoff | climb N m msl | cruise for N km | cruise for N min | turn around | head KSC bank N left|right | circle N laps left|right bank N | wait N s | land KSC 27|KSC 09|Island 09. Or pass Luke's words as request.",
                         {"request": "string", "plan": "string", "fly": "boolean"}, []),
    "follow_terrain": ("Hold a fixed height above the ground (AGL, 50-5000 m); agl_m=0 turns it off.", {"agl_m": "number"}, ["agl_m"]),
}


def ported():
    src = open(os.path.join(ROOT, "KSPChatMod", "NativeCommands.cs"), encoding="utf-8").read()
    block = src[src.index("Ported = new"):]
    block = block[block.index("{"):block.index("};")]
    block = re.sub(r"//[^\n]*", "", block)
    return [n for n in re.findall(r'"([^"]+)"', block) if "/" not in n]


def short(doc, limit=160):
    d = " ".join((doc or "").split())
    m = re.match(r"(.+?[.!?])(\s|$)", d)
    d = m.group(1) if m else d
    return d if len(d) <= limit else d[:limit - 3].rstrip() + "..."


def schemas():
    from kspchat import ksp_actions as k
    out, missing = [], []
    for name in ported():
        if name in NATIVE_ONLY:
            desc, ps, req = NATIVE_ONLY[name]
            params = {"type": "object", "properties": {p: {"type": t} for p, t in ps.items()}}
            if req:
                params["required"] = req
            out.append({"type": "function", "function": {"name": name, "description": desc, "parameters": params}})
            continue
        fn = k.BY_NAME.get(name)
        if fn is None:
            missing.append(name)
            continue
        props, req = {}, []
        for p in inspect.signature(fn).parameters.values():
            if p.kind in (p.VAR_POSITIONAL, p.VAR_KEYWORD):
                continue
            props[p.name] = {"type": TYPES.get(p.annotation, "string")}
            if p.default is inspect.Parameter.empty:
                req.append(p.name)
        params = {"type": "object", "properties": props}
        if req:
            params["required"] = req
        out.append({"type": "function", "function": {"name": name, "description": short(fn.__doc__), "parameters": params}})
    return out, missing


def render():
    tools, missing = schemas()
    js = json.dumps(tools, separators=(",", ":"), ensure_ascii=True)
    return ("// GENERATED by tools/gen_tool_schemas.py from kspchat.ksp_actions - do not edit by hand.\n"
            "namespace KSPChatBridge\n{\n    internal static class NativeToolSchemas\n    {\n"
            "        internal const int Count = %d;\n"
            "        // ported but not a bridge tool (no schema): %s\n"
            "        internal const string Json = @\"%s\";\n    }\n}\n") % (len(tools), ", ".join(missing) or "none", js.replace('"', '""'))


if __name__ == "__main__":
    open(OUT, "w", encoding="utf-8", newline="\n").write(render())
    print(OUT)
