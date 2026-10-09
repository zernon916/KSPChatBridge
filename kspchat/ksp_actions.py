"""KSP actions implemented with kRPC + kRPC.MechJeb.

Every public action is a plain function with type hints and a docstring. The same
functions are exposed as OpenAI tools (see tool_schemas()) and as MCP tools
(mcp_server.py). Actions return short human-readable strings or dicts and never raise:
errors come back as text so the model can react.
"""
import inspect
import math
import threading
import time

from . import config, docking, guard, hold, lander, maxspeed, memory, plane, protect, science, settings, speedcap, spots, taxi
from . import takeoff as tko

_conn = None
_lock = threading.RLock()


def conn():
    """Return a live kRPC connection, reconnecting if the old one died."""
    global _conn
    import krpc
    with _lock:
        if _conn is not None:
            try:
                _conn.krpc.get_status()
                return _conn
            except Exception:
                _conn = None
        _conn = krpc.connect(name="KSPChatBridge", address=config.KRPC_ADDRESS,
                             rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        return _conn


def _scene():
    return str(conn().krpc.current_game_scene).split(".")[-1]


def _vessel():
    if _scene() != "flight":
        raise RuntimeError(f"Not in flight (scene: {_scene()}). Launch a craft first.")
    return conn().space_center.active_vessel


_PILOT = {"t": 0.0, "name": None}
PILOT_TTL = 10.0  # s: the chat speaker name is re-read from kRPC at most this often


def pilot_name():
    """First name of the active vessel's pilot (crew with the Pilot trait, else the first crew member), cached ~10 s.
    None if there's no crew, no vessel or no kRPC (the chat then uses the AI's / Bridge name)."""
    now = time.time()
    if now - _PILOT["t"] < PILOT_TTL:
        return _PILOT["name"]
    name = None
    try:
        crew = list(_vessel().crew)
        k = next((c for c in crew if str(getattr(c, "trait", "")).lower() == "pilot"), crew[0] if crew else None)
        if k is not None:
            name = str(k.name).split()[0] or None  # "Sidry Kerman" -> "Sidry"
    except Exception:  # noqa: BLE001
        name = None
    _PILOT.update(t=now, name=name)
    return name


def _mj():
    mj = conn().mech_jeb
    t0 = time.time()
    while not mj.api_ready and time.time() - t0 < 10:
        time.sleep(0.5)
    if not mj.api_ready:
        raise RuntimeError("MechJeb API not ready (needs flight scene).")
    return mj


def _autostage_on(mj):
    """Let MechJeb drop empty stages during node burns (node executor alone does not stage)."""
    try:
        mj.staging_controller.enabled = True
    except Exception:
        pass


def _r(x, nd=None):
    """round() that tolerates inf/NaN (hyperbolic orbits have infinite apoapsis)."""
    return round(x, nd) if x == x and abs(x) != float("inf") else None


def _res_pct(res, name):
    mx = res.max(name)
    return round(100 * res.amount(name) / mx, 1) if mx > 0 else None


_RAILS = [1, 5, 10, 50, 100, 1000, 10000, 100000]


def _warp_until(target_ut, max_real_s=600):
    """Rails-warp loop (kRPC's warp_to hung in testing). Steps the warp factor down as the target nears."""
    sc = conn().space_center
    t0 = time.time()
    try:
        while time.time() - t0 < max_real_s:
            left = target_ut - sc.ut
            if left <= 1:
                break
            k = 0
            for i, r in enumerate(_RAILS):
                if r * 4 < left:
                    k = i
            k = min(k, sc.maximum_rails_warp_factor)
            if sc.rails_warp_factor != k:
                sc.rails_warp_factor = k
            time.sleep(0.5)
    finally:
        sc.rails_warp_factor = 0
    time.sleep(1)


# ---------------------------------------------------------------- actions
def get_status() -> dict:
    """Get the active vessel's status: name, situation, body, altitude, apoapsis/periapsis, speeds, fuel %, current stage, electric charge, autopilot state."""
    scene = _scene()
    if scene != "flight":
        return {"scene": scene, "note": "Not in flight. Use list_craft / launch_craft from the Space Center."}
    v = _vessel()
    o = v.orbit
    body = o.body
    f = v.flight(body.reference_frame)
    r = v.resources
    st = {
        "vessel": v.name, "situation": str(v.situation).split(".")[-1], "body": body.name,
        "altitude_m": _r(f.mean_altitude), "surface_altitude_m": _r(f.surface_altitude),
        "apoapsis_m": _r(o.apoapsis_altitude), "periapsis_m": _r(o.periapsis_altitude),
        "time_to_apoapsis_s": _r(o.time_to_apoapsis), "orbital_speed_mps": round(o.speed, 1),
        "surface_speed_mps": round(f.speed, 1), "vertical_speed_mps": round(f.vertical_speed, 1),
        "current_stage": v.control.current_stage, "throttle": round(v.control.throttle, 2),
        "sas": v.control.sas, "mass_t": round(v.mass / 1000, 2),
        "time_to_soi_change_s": _r(o.time_to_soi_change),
        "next_body": o.next_orbit.body.name if o.next_orbit else None,
        "atmosphere_top_m": round(body.atmosphere_depth) if body.has_atmosphere else 0,
    }
    for name, key in (("LiquidFuel", "liquid_fuel_pct"), ("Oxidizer", "oxidizer_pct"), ("SolidFuel", "solid_fuel_pct"),
                      ("MonoPropellant", "monoprop_pct"), ("ElectricCharge", "electric_charge_pct")):
        p = _res_pct(r, name)
        if p is not None:
            st[key] = p
    try:
        mj = conn().mech_jeb
        if mj.api_ready:
            st["mechjeb_ascent_active"] = mj.ascent_autopilot.enabled
            st["mechjeb_node_executor_active"] = mj.node_executor.enabled
            if mj.landing_autopilot.enabled:
                st["mechjeb_landing"] = str(mj.landing_autopilot.status)
        if lander.active() or lander.STATUS.get("result"):
            st["landing_controller"] = dict(lander.STATUS)
        if plane.active() or plane.STATUS.get("phase") == "done":
            st["plane_autoland"] = {k2: v2 for k2, v2 in plane.STATUS.items() if k2 != "started"}
    except Exception:
        pass
    return st


def stage() -> str:
    """Activate the next stage (like pressing space)."""
    v = _vessel()
    before = v.control.current_stage
    v.control.activate_next_stage()
    time.sleep(0.5)
    return f"Staged: stage {before} -> {conn().space_center.active_vessel.control.current_stage}."


def set_throttle(value: float) -> str:
    """Set main throttle from 0.0 (off) to 1.0 (full). Percentages like 50 are treated as 50%. In atmospheric flight it
    never goes to 0 (idle = 5 %). While the plane autopilot holds fly, this is a manual throttle override (they stop
    managing it; stall protection still adds power) until value=-1 ("throttle auto") or a new speed/altitude/pitch."""
    value = float(value)
    if value < 0:  # "throttle auto": hand it back to the holds
        if hold.active():
            hold.set_targets(throttle="off")
            return "Throttle handed back to the autopilot holds."
        return "No autopilot hold is managing the throttle."
    if value > 1.0:
        value /= 100.0
    value = max(0.0, min(1.0, value))
    v = _vessel()
    note = ""
    if str(v.situation).split(".")[-1] == "flying" and value < hold.THR_FLOOR:
        value, note = hold.THR_FLOOR, " (never 0 in flight: idle is 5%)"
    from . import propulsion, heli as heli_mod
    pnote = ""
    # Prop planes: throttle maps to rotor torque. Helicopters: lift is collective pitch — never the main throttle.
    if propulsion.has_props(v) and not heli_mod.is_heli(v):
        flying = str(v.situation).split(".")[-1] == "flying"
        propulsion.STATE["manual_torque"] = False
        pnote = " Props: " + propulsion.set_rotor(v, torque=100.0 * value, flying=flying) + "."
    if plane.active():
        return ("The landing / fly-to autopilot is managing the throttle; abort it first to set the throttle by hand."
                + pnote)
    if hold.active():
        hold.set_targets(throttle=value)
        return (f"Throttle {round(value * 100)}%{note} - manual override: the holds stop managing the throttle (stall "
                "protection can still add power). Say 'throttle auto' to hand it back." + pnote)
    v.control.throttle = value
    return f"Throttle set to {round(value * 100)}%{note}." + pnote


def set_gear(down: bool = True) -> str:
    """Landing gear / wheels (Gear action group): down=true lowers, down=false raises. "gear down", "wheels up"."""
    v = _vessel()
    sit = str(v.situation).split(".")[-1]
    if not down and sit in ("landed", "pre_launch"):
        return "Not raising the gear on the ground."
    try:
        n = len(v.parts.wheels) + len(v.parts.legs)
    except Exception:  # noqa: BLE001
        n = -1
    _own_change()
    v.control.gear = bool(down)
    from . import emergency
    return (f"Gear {'down' if down else 'up'}." + (" (No wheels or legs found on this craft.)" if n == 0 else "")
            + (emergency.gear_note() if down else ""))


def _on_runway(v):
    try:
        return plane._runway_line(v.orbit.body, v, v.orbit.body.reference_frame, v.flight().heading) is not None
    except Exception:  # noqa: BLE001
        return False


def set_brakes(on: bool = True) -> str:
    """Brakes action group (airbrakes + wheel brakes): on=true / false. "airbrake on", "brakes off"."""
    v = _vessel()
    try:
        air = len(v.parts.modules_with_name("ModuleAeroSurface"))
    except Exception:  # noqa: BLE001
        air = -1
    _own_change()
    v.control.brakes = bool(on)
    try:
        from . import parking
        parking.note_player_brakes(bool(on))
    except Exception:  # noqa: BLE001
        pass
    msg = f"Brakes {'on' if on else 'off'}"
    if air == 0:
        return msg + " - note: this craft has no airbrake parts (only wheel brakes, if any)."
    return msg + (f" ({air} airbrake(s) + wheel brakes)." if air > 0 else ".")


def eject_kerbal(confirmed: bool = False) -> str:
    """EVA the pilot out of the pod ("eject"). Asks Luke yes/no first; on yes the AICS mod spawns the EVA (KSP's
    FlightEVA - kRPC has no EVA call), delivered over the mod's /events poll."""
    try:
        crew = [k.name for k in _vessel().crew]
    except Exception:  # noqa: BLE001
        crew = None
    if crew == []:
        return "Nobody aboard to eject (no crew)."
    who = crew[0] if crew else "the pilot"
    if not science.cmd_client_seen():
        return (f"Can't eject {who} yet - kRPC has no EVA call and the AICS mod in KSP is the old build (no eject "
                "hook). Close KSP and run tools\\install_mod.ps1 to get it. Until then, use the EVA button on the crew portrait.")
    if not confirmed:
        return speedcap.confirm("eject_kerbal", {"confirmed": True},
                                f"Eject {who}? Out the hatch at this speed and altitude - sure? (yes/no)")
    science.post_command(f"eject {time.time():.0f} {who if crew else ''}".rstrip())
    return f"Ejecting {who} - the AICS mod opens the hatch within ~3 s. Good luck out there!"


def set_sas(enabled: bool) -> str:
    """Turn SAS (stability assist) on or off."""
    _own_change()
    _vessel().control.sas = bool(enabled)
    return f"SAS {'on' if enabled else 'off'}."


SAS_MODES = ["stability_assist", "prograde", "retrograde", "normal", "anti_normal",
             "radial", "anti_radial", "target", "anti_target", "maneuver"]


def set_sas_mode(mode: str) -> str:
    """Set the SAS mode (turns SAS on). One of: stability_assist, prograde, retrograde, normal, anti_normal, radial, anti_radial, target, anti_target, maneuver."""
    m = str(mode).lower().replace("-", "_").replace(" ", "_")
    if m not in SAS_MODES:
        return f"Unknown SAS mode '{mode}'. Use one of {SAS_MODES}."
    c = _vessel().control
    c.sas = True
    time.sleep(0.2)
    try:
        c.sas_mode = getattr(conn().space_center.SASMode, m)
    except Exception:
        return (f"Couldn't set SAS mode {m}: the pilot/probe core may not support it, there may be no target/node, "
                "or the vessel is still on the pad. SAS is on (stability assist).")
    return f"SAS mode set to {m}."


def run_science(transmit: bool = True) -> str:
    """Run every available science experiment on the vessel, then transmit the results if possible."""
    v = _vessel()
    ran, skipped, sent = [], [], []
    for e in v.parts.experiments:
        title = e.part.title
        try:
            if e.inoperable or e.has_data or not e.available:
                skipped.append(title)
                continue
            e.run()
            ran.append(title)
        except Exception as ex:
            skipped.append(f"{title} ({ex.__class__.__name__})")
    if transmit and ran:
        time.sleep(2.0)
        for e in v.parts.experiments:
            try:
                if e.has_data:
                    e.transmit()
                    sent.append(e.part.title)
            except Exception:
                pass
    return (f"Ran {len(ran)} experiment(s): {', '.join(ran) or 'none'}. "
            f"Skipped {len(skipped)}. Transmitted: {', '.join(sent) or 'nothing'}.")


def _orbit_capability_check(v):
    """Refuse orbit for craft that can't get there: air-breathing engines only (jets), propellers, or no engines."""
    from . import propulsion
    rockets, jets = 0, 0
    for e in v.parts.engines:
        try:
            props = set(e.propellant_names)
            if propulsion.engine_kind(props) == "electric":
                continue  # electric prop: air-dependent, counted with the propellers below
            modes = []
            try:
                modes = list(e.modes.keys()) if e.has_modes else []
            except Exception:
                pass
            if any("closed" in m.lower() for m in modes) or (props and "IntakeAir" not in props):
                rockets += 1
            else:
                jets += 1
        except Exception:
            pass
    if rockets:
        return None
    pk = propulsion.classify(v)
    plane_hint = (" For a runway takeoff say 'take off'." if str(v.situation).split(".")[-1] in ("landed", "pre_launch")
                  else "")
    if pk["props"]:
        return ("Can't do that: this is a propeller-driven craft (" + propulsion.describe(pk) + ") - propellers need "
                "air, so no orbit, deorbit or orbital landing. I can fly it around or land it on a runway "
                "(land_plane)." + plane_hint)
    if jets:
        return ("Can't do that: this craft only has air-breathing jet engines - jets need air, and there is no air "
                "in orbit, so it tops out around 20 km. No rocket or closed-cycle (RAPIER) engine on board, so no "
                "orbit, deorbit or orbital landing. I can fly it around or land it on the runway (land_plane)."
                + plane_hint)
    return "Can't do that: no engines or propellers on board, so no orbit."


def mechjeb_ascent(target_altitude_km: float = 80, inclination_deg: float = 0) -> str:
    """Engage MechJeb ascent autopilot to a circular orbit at target_altitude_km (autostage on). Refuses for jet-only craft (no air in space). If the vessel is on the pad it launches. Returns immediately; the flight continues in-game - poll get_status. inclination_deg: 0 = equatorial (default); only pass another value if Luke asks for an inclined/polar orbit."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    no_orbit = _orbit_capability_check(v)
    if no_orbit:
        return no_orbit
    if str(v.situation).split(".")[-1] in ("landed", "pre_launch") and _planeish(v) and _on_runway(v):
        return ("This is a plane on a runway: 'take off' is a runway takeoff (the takeoff tool), not a MechJeb "
                "rocket ascent. Say 'launch to orbit' from the launch pad for an ascent.")
    mj = _mj()
    a = mj.ascent_autopilot
    a.desired_orbit_altitude = float(target_altitude_km) * 1000.0
    a.desired_inclination = float(inclination_deg)
    a.autostage = True
    a.skip_circularization = False
    v.control.sas = False
    a.enabled = True
    time.sleep(1.0)
    msg = f"MechJeb ascent engaged: target {target_altitude_km} km, inclination {inclination_deg} deg, autostage on."
    if str(v.situation).endswith("pre_launch"):
        time.sleep(2.0)
        if str(conn().space_center.active_vessel.situation).endswith("pre_launch"):
            v.control.throttle = 1.0
            v.control.activate_next_stage()
            msg += " Lift-off: staged first stage."
    return msg + " Status: " + str(a.status)


def circularize() -> str:
    """Create a MechJeb circularization node at the next apoapsis (or periapsis if that comes first) and execute it."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    mj = _mj()
    o = v.orbit
    op = mj.maneuver_planner.operation_circularize
    TR = mj.TimeReference
    use_apo = o.time_to_apoapsis < o.time_to_periapsis or o.periapsis_altitude < 0
    op.time_selector.time_reference = TR.apoapsis if use_apo else TR.periapsis
    nodes = op.make_nodes()
    if not nodes:
        return "MechJeb could not plan a circularization: " + str(op.error_message)
    _autostage_on(mj)
    mj.node_executor.execute_one_node()
    dv = nodes[0].delta_v if hasattr(nodes[0], "delta_v") else "?"
    return f"Circularization node at {'apoapsis' if use_apo else 'periapsis'} created (dv {round(dv, 1) if isinstance(dv, float) else dv} m/s); MechJeb node executor is flying it."


def warp_to_apoapsis(lead_seconds: float = 30) -> str:
    """Time-warp until lead_seconds before apoapsis. Blocks until warp ends. Refused inside the atmosphere."""
    v = _vessel()
    sc = conn().space_center
    body = v.orbit.body
    if body.has_atmosphere and v.flight(body.reference_frame).mean_altitude < body.atmosphere_depth:
        return "Refusing to warp inside the atmosphere. Wait until above %d m." % body.atmosphere_depth
    t = v.orbit.time_to_apoapsis
    if t <= lead_seconds + 5:
        return f"Apoapsis is only {round(t)} s away; no warp needed."
    _warp_until(sc.ut + t - float(lead_seconds))
    return f"Warped; apoapsis in about {round(sc.active_vessel.orbit.time_to_apoapsis)} s."


def deploy_parachutes(force: bool = False) -> str:
    """Arm/deploy all parachutes (works with stock and RealChute). RealChutes are armed and open automatically when safe. Refused on the ground or while climbing unless force=true."""
    v = _vessel()
    try:
        none = not v.parts.parachutes and not v.parts.modules_with_name("RealChuteModule")
    except Exception:  # noqa: BLE001
        none = False
    if none:
        return "No parachutes on this craft."
    sit = str(v.situation).split(".")[-1]
    vs = v.flight(v.orbit.body.reference_frame).vertical_speed
    if not force and (sit in ("pre_launch", "landed", "splashed") or vs > 5):
        return f"Not deploying parachutes now (situation {sit}, vertical speed {round(vs)} m/s). Use force=true to override."
    _own_change()
    chutes = v.parts.parachutes
    done = 0
    for p in chutes:
        try:
            p.deploy()  # RealChute: activates the chute; it opens when pressure/altitude are safe
            done += 1
        except Exception:
            try:
                p.arm()
                done += 1
            except Exception:
                pass
    if not chutes:  # fallback: RealChute module action
        for m in v.parts.modules_with_name("RealChuteModule"):
            try:
                m.set_action("Arm parachute", True)
                done += 1
            except Exception:
                pass
    return f"Armed/deployed {done} parachute(s)." if done else "No parachutes found."


def transfer_to(body: str = "Mun") -> str:
    """Plan a MechJeb Hohmann transfer to a moon/planet (e.g. Mun, Minmus) from the current orbit and start executing the burn with the node executor. Returns immediately; poll get_status."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    sc = conn().space_center
    bodies = {k.lower(): b for k, b in sc.bodies.items()}
    target = bodies.get(str(body).lower())
    if target is None:
        return f"Unknown body '{body}'."
    if not str(v.situation).endswith("orbiting"):
        return "Get into a stable orbit first."
    sc.target_body = target
    mj = _mj()
    for n in list(v.control.nodes):
        n.remove()
    op = mj.maneuver_planner.operation_transfer
    nodes = op.make_nodes()
    if not nodes:
        return "MechJeb could not plan the transfer: " + str(op.error_message)
    for extra in list(v.control.nodes)[1:]:  # MJ may add a capture node; keep only the departure burn
        extra.remove()
    n = v.control.nodes[0] if v.control.nodes else None
    mj.node_executor.autowarp = True
    _autostage_on(mj)
    mj.node_executor.execute_one_node()
    info = f" Burn of {round(n.delta_v)} m/s in {round(n.time_to)} s." if n else ""
    return f"Transfer to {target.name} planned; MechJeb node executor will fly it (autowarp on).{info}"


def course_correction(periapsis_km: float = 50) -> str:
    """After a transfer burn, fine-tune the trajectory so the closest approach (periapsis) at the target body is periapsis_km. Uses MechJeb course correction + node executor. Target body must be set (transfer_to does that)."""
    v = _vessel()
    mj = _mj()
    for n in list(v.control.nodes):
        n.remove()
    op = mj.maneuver_planner.operation_course_correction
    op.course_correct_final_pe_a = float(periapsis_km) * 1000.0
    nodes = op.make_nodes()
    if not nodes:
        return "MechJeb could not plan a course correction: " + str(op.error_message)
    mj.node_executor.autowarp = True
    _autostage_on(mj)
    mj.node_executor.execute_one_node()
    return f"Course correction planned for a {periapsis_km} km periapsis; node executor is flying it."


def warp_to_soi_change(lead_seconds: float = 60) -> str:
    """Time-warp to just after the next sphere-of-influence change (e.g. arriving at the Mun). Blocks until warp ends."""
    v = _vessel()
    sc = conn().space_center
    t = v.orbit.time_to_soi_change
    if t != t or t <= 0:  # NaN -> no SOI change on this orbit
        return "No SOI change on the current trajectory."
    before = v.orbit.body.name
    _warp_until(sc.ut + t + float(lead_seconds))
    return f"Warped through SOI change: {before} -> {sc.active_vessel.orbit.body.name}. Periapsis {round(sc.active_vessel.orbit.periapsis_altitude / 1000, 1)} km."


G0 = 9.80665
_FUELS = ("LiquidFuel", "Oxidizer", "SolidFuel")


def _stage_dvs(v):
    """Per-stage vacuum delta-v by simulating staging. Phase k = after stage k is activated: parts with
    decouple_stage < k are aboard, engines with stage >= k fire, fuel in parts with decouple_stage == k-1
    is burned (dropped at the next staging). Crossfeed/asparagus makes this approximate."""
    density = {n: conn().space_center.Resources.density(n) for n in _FUELS}  # kg per unit (part.mass is kg too)
    parts = []
    for p in v.parts.all:
        fuel = 0.0
        try:
            r = p.resources
            fuel = sum(r.amount(n) * density[n] for n in _FUELS if r.has_resource(n))
        except Exception:
            pass
        e = p.engine
        parts.append((p.mass, p.decouple_stage, p.stage, fuel,
                      (e.max_vacuum_thrust, e.vacuum_specific_impulse) if e else None))
    out = []
    for k in range(v.control.current_stage, -1, -1):
        aboard = [x for x in parts if x[1] < k]
        engines = [x[4] for x in aboard if x[4] and x[2] >= k and x[4][1] > 0]
        fuel = sum(x[3] for x in aboard if x[1] == k - 1)
        if not engines or fuel <= 0:
            continue
        thrust = sum(t for t, _ in engines)
        isp = thrust / sum(t / i for t, i in engines) if thrust else 0
        m0 = sum(x[0] for x in aboard)
        if m0 > fuel:
            out.append({"stage": k, "dv": round(isp * G0 * math.log(m0 / (m0 - fuel))),
                        "twr_vac_kerbin": round(thrust / (m0 * G0), 2)})
    return out


def _dv_numbers(v):
    """Rough delta-v figures. Returns dict; all speeds m/s."""
    body = v.orbit.body
    f = v.flight(body.reference_frame)
    g = body.surface_gravity
    stages = _stage_dvs(v)
    total = sum(x["dv"] for x in stages)
    accel = v.available_thrust / v.mass if v.mass else 0
    if accel <= 0 and stages:  # nothing lit yet: use the next stage's thrust
        accel = stages[0]["twr_vac_kerbin"] * G0
    twr = accel / g if g else 0
    out = {"total_dv": total, "next_stage_dv": stages[0]["dv"] if stages else 0, "stages": stages,
           "twr_surface": round(twr, 2), "surface_speed": round(f.speed), "altitude_m": round(f.surface_altitude),
           "situation": str(v.situation).split(".")[-1]}
    if out["situation"] not in ("landed", "splashed", "pre_launch"):
        # Stop from current speed: v * a/(a-g) (gravity losses, no drag credit). TWR<1 cannot stop.
        land = f.speed * accel / (accel - g) if accel > g else float("inf")
        if out["situation"] == "orbiting":
            land = 100 + (250 * accel / (accel - g) if accel > g else float("inf"))  # deorbit + burn from ~terminal velocity
        out["landing_dv_needed_estimate"] = round(land) if land != float("inf") else "impossible (TWR < 1)"
        # explicit verdict (10 % margin) so the model never compares the numbers itself
        out["enough_fuel"] = bool(land != float("inf") and total >= land * 1.1)
        if _is_aircraft(v):
            out["enough_fuel"] = False
            out["note"] = ("aircraft: jet/air-breathing delta-v is no budget for a vertical powered landing - "
                           "use plane tools (plane_hold altitude_m to descend, land_plane / fly_to to land)")
    return out


def get_delta_v() -> dict:
    """Estimate available delta-v (per stage and total, vacuum), surface TWR, and - when airborne - the delta-v a powered landing would need plus enough_fuel (true/false: the verdict, use it as is - don't compare the numbers yourself). Call before any rocket powered landing, suicide burn or deorbit."""
    return _dv_numbers(_vessel())


def _landing_check(v, force):
    """Return a warning string if a powered landing looks infeasible, else None."""
    if force:
        return None
    d = _dv_numbers(v)
    need = d.get("landing_dv_needed_estimate")
    have = d["total_dv"]
    chutes = len(v.parts.parachutes)
    if need is None:
        return None
    if isinstance(need, str) or have < need * 1.1:
        alt = ("deploy_parachutes (this craft has %d chute(s))" % chutes) if chutes else "no parachutes on board - consider abort/accepting a crash or a different plan"
        return (f"enough_fuel: false. NOT ENOUGH DELTA-V for a powered landing: have ~{have} m/s, need ~{need} m/s "
                f"(TWR {d['twr_surface']}). Suggest: {alt}. Call again with force=true to try anyway.")
    return None


KSC_PAD = (-0.0972, -74.5577)  # Kerbin launch pad lat/lon


def deorbit_burn(periapsis_km: float = 30, force: bool = False) -> str:
    """Lower periapsis to periapsis_km (default 30 km, inside Kerbin's atmosphere) with a MechJeb node at the next apoapsis, executed by the node executor. This changes the orbit - only when Luke asked to deorbit."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    mj = _mj()
    if not str(v.situation).endswith("orbiting"):
        return "Deorbit burn needs a stable orbit."
    d = _dv_numbers(v)
    have = d["total_dv"]
    if not force and have < 120:
        return f"enough_fuel: false. Only ~{have} m/s of delta-v; a deorbit burn needs ~100 m/s plus margin. force=true to try anyway."
    for n in list(v.control.nodes):
        n.remove()
    op = mj.maneuver_planner.operation_periapsis
    op.new_periapsis = float(periapsis_km) * 1000.0
    op.time_selector.time_reference = mj.TimeReference.apoapsis
    nodes = op.make_nodes()
    if not nodes:
        return "MechJeb could not plan the deorbit burn: " + str(op.error_message)
    mj.node_executor.autowarp = True
    mj.node_executor.execute_one_node()
    return f"Deorbit node at apoapsis lowers periapsis to {periapsis_km} km; MechJeb node executor will fly it."


def _plane_vertical_check(v, force):
    """Refuse a vertical (tail-first) powered landing for a plane that can't hover."""
    if force or not _planeish(v):
        return None
    twr = _dv_numbers(v).get("twr_surface") or 0
    if twr >= 1.6:
        return None  # VTOL-capable / rocket plane
    return (f"This looks like a plane (wings + wheels) with a surface TWR of {twr}: it can't hover down tail-first - "
            "tail-sitting a plane is a great way to make modern art. Use land_plane for a runway landing "
            "(force=true to try a vertical landing anyway).")


ROCKET_LANDING_TOOLS = ("land_here", "land_at", "land_at_ksc")
_AIR = {"t": 0.0, "v": False}
AIR_TTL = 10.0


def _is_aircraft(v):
    """An aircraft right now: the plane autopilot / holds are on, or flying in the atmosphere with intakes, real
    lift (> 30 % of the weight) or wings. Cheap checks first (kRPC calls)."""
    try:
        if hold.active() or plane.active():
            return True
    except Exception:  # noqa: BLE001
        pass
    if str(v.situation).split(".")[-1] != "flying":
        return False
    try:
        if list(getattr(v.parts, "intakes", None) or []):
            return True
    except Exception:  # noqa: BLE001
        pass
    try:
        if float(v.flight(v.orbit.body.reference_frame).lift) > 0.3 * float(v.mass) * float(v.orbit.body.surface_gravity):
            return True
    except Exception:  # noqa: BLE001
        pass
    try:
        return _has_wings(v)
    except Exception:  # noqa: BLE001
        return False


def invalidate_craft_cache():
    """Force aircraft / propulsion / heli re-detection after the player switches vessels."""
    _AIR["t"] = 0.0


def aircraft_now():
    """Cached (AIR_TTL s) _is_aircraft for the active vessel; False if kRPC isn't reachable."""
    if time.time() - _AIR["t"] < AIR_TTL:
        return _AIR["v"]
    try:
        with _lock:
            val = bool(_is_aircraft(_vessel()))
    except Exception:  # noqa: BLE001
        val = False
    _AIR.update(t=time.time(), v=val)
    return val


def _aircraft_refusal(v):
    """Rocket powered-descent / suicide-burn landings are refused for aircraft (even with force)."""
    if not _is_aircraft(v):
        return None
    return ("This is an aircraft (flying with wings / intakes / lift, or the plane autopilot is on), so rocket "
            "powered-descent / suicide-burn landings are off. To lose altitude: 'descend to 5 km' (set_altitude) or "
            "plane_hold altitude_m; to land: land_plane / fly_to KSC / 'land'.")


def land_at(latitude: float, longitude: float, touchdown_speed: float = 1.5, force: bool = False) -> str:
    """ROCKETS/LANDERS only (refused for aircraft). Targeted powered landing: MechJeb's landing autopilot deorbits and steers toward latitude/longitude, then our own suicide-burn controller takes over below 5 km for a soft touchdown (legs + chutes auto). Checks delta-v first (force=true overrides). Returns immediately; the result is posted to chat. Leave force=false unless Luke explicitly says to ignore the delta-v warning."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    sit = str(v.situation).split(".")[-1]
    if sit in ("pre_launch", "landed", "splashed"):
        return f"Already on the ground ({sit})."
    warn = _aircraft_refusal(v) or _plane_vertical_check(v, force) or _landing_check(v, force)
    if warn:
        return warn
    r = lander.start(touchdown_speed, True, (float(latitude), float(longitude)))
    if r != "started":
        return r
    return (f"Landing toward {latitude:.4f}, {longitude:.4f}: MechJeb guides the deorbit/approach, then the bridge's "
            "landing controller does the final burn. I'll post the touchdown result in chat.")


def land_at_ksc(touchdown_speed: float = 1.5, force: bool = False) -> str:
    """ROCKETS/LANDERS only (refused for aircraft). Powered landing back near the KSC launch pad (Kerbin only). Checks delta-v first (force=true overrides). Leave force=false unless Luke explicitly says to ignore the delta-v warning."""
    if _vessel().orbit.body.name != "Kerbin":
        return "land_at_ksc only works around Kerbin."
    return land_at(KSC_PAD[0], KSC_PAD[1], touchdown_speed, force)


def land_here(touchdown_speed: float = 1.5, use_chutes: bool = True, force: bool = False) -> str:
    """ROCKETS/LANDERS only (refused for aircraft). Land safely wherever the vessel comes down: from orbit it does a deorbit burn first, then a computed suicide burn with retrograde hold, legs down, optional chutes, touchdown ~1-2 m/s. Checks delta-v/TWR first (force=true overrides). Returns immediately; the result is posted to chat. Use for 'land us', 'land here', 'burn to stop just above the ground'. Leave force=false unless Luke explicitly says to ignore the delta-v warning. For rockets/landers only; for aircraft use land_plane."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    sit = str(v.situation).split(".")[-1]
    if sit in ("pre_launch", "landed", "splashed"):
        return f"Already on the ground ({sit})."
    warn = _aircraft_refusal(v) or _plane_vertical_check(v, force) or _landing_check(v, force)
    if warn:
        return warn
    r = lander.start(touchdown_speed, use_chutes)
    if r != "started":
        return r
    return ("Landing controller engaged" + (" (deorbit burn first)" if sit == "orbiting" else "")
            + ": retrograde hold, legs down, suicide burn to ~%.1f m/s. I'll post the result in chat." % touchdown_speed)


_WING_WORDS = ("wing", "delta", "strake", "elevon", "tailplane")


def _has_wings(v):
    """Real wings (not just rocket fins / winglets): lifting parts titled wing/delta/strake/elevon."""
    n = 0
    for p in v.parts.all:
        t = p.title.lower()
        if any(w in t for w in _WING_WORDS) and "winglet" not in t:
            try:
                if any(m.name in ("ModuleLiftingSurface", "ModuleControlSurface") for m in p.modules):
                    n += 1
                    if n >= 2:
                        return True
            except Exception:
                pass
    return False


def _planeish(v):
    return bool(v.parts.wheels) and _has_wings(v)


def _plane_check(v, strip=None, allow_ground=False):
    strip = strip or plane.STRIPS["KSC"]
    body = v.orbit.body
    if body.name != strip.get("body", "Kerbin"):
        return f"That runway is on {strip.get('body', 'Kerbin')}; we're at {body.name}."
    sit = str(v.situation).split(".")[-1]
    if sit == "splashed":
        return "We're in the water - no takeoff from here."
    if sit in ("landed", "pre_launch") and not allow_ground:
        return f"The vessel is already on the ground ({sit})."
    if sit not in ("flying", "sub_orbital", "landed", "pre_launch"):
        return f"land_plane is for aircraft in the atmosphere (situation: {sit}). For rockets use land_here."
    if not v.parts.wheels:
        return ("That's not a plane: no wheels to roll down the runway on (it would be a very short, very loud "
                "runway landing). Use land_here for a vertical powered landing.")
    if not _has_wings(v):
        return ("No wings found - this would arrive at the runway like a dropped piano. Fins aren't wings. "
                "Use land_here for a vertical powered landing.")
    f = v.flight(body.reference_frame)
    lat, lon = f.latitude, f.longitude
    a = strip["a"]
    dist = spots.gc_dist(lat, lon, a[0], a[1], body.equatorial_radius)
    if dist > 600_000:  # (was 150 km: that refused a controller hand-over 165 km out on a dash leg -> crash)
        return f"Too far from the runway ({dist / 1000:.0f} km); fly within ~600 km first."
    if sit in ("landed", "pre_launch") and f.speed > 5:
        return "Still rolling - stop first, then I'll take off."
    if f.mean_altitude > 20_000:
        return f"Too high ({f.mean_altitude / 1000:.0f} km); descend below 20 km first."
    fuel = v.resources.amount("LiquidFuel")
    if fuel <= 0.5 and f.mean_altitude / max(dist, 1) < 0.1:
        return (f"Out of fuel and too far to glide ({dist / 1000:.0f} km at {f.mean_altitude:.0f} m). "
                "Consider a field landing or parachutes.")
    return None


def land_plane(runway: str = "", approach_speed: float = 0, touch_and_go: int = 0, final_km: float = 0,
               approach_heading: float = -1) -> str:
    """AIRCRAFT ONLY: autoland a plane on the KSC runway (09 = landing eastbound, 27 = westbound; empty = pick by approach direction). Flies to an 11 km final, 3 deg glideslope, gear down, flare, brakes, stops on the centerline. approach_speed in m/s (0 = automatic: 1.3 x the plane's stall speed with flaps, measured once per craft in a slow-flight test at altitude, then cached). touch_and_go = N touch-and-goes before the final full-stop landing (-1 = keep going until told to stop; each touchdown is graded A-F). If a touch-and-go series is running, calling land_plane with touch_and_go=0 makes the next landing a full stop. Other runways (Island Airfield, saved strips): land_at_spot. For rockets/landers use land_here. final_km = length of the straight final (0 = default 11 km; 5 = short final). approach_heading = the heading you want to land on (picks 09 or 27); -1 = automatic."""
    claim = guard.busy()
    if claim and claim.get("kind") == "plane" and not touch_and_go:
        settings.put("plane_tg_stop", time.time())
        return "OK - no more touch-and-goes: the next landing will be a full stop."
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    if _heli_v(v) is not None:
        return "This is a helicopter - say 'land' for a vertical landing (heli_control land)."
    err = _plane_check(v, allow_ground=True)
    if err:
        return err
    if plane.active():
        return "A plane landing is already in progress."
    _set_final(final_km)
    if not runway and approach_heading is not None and approach_heading >= 0:
        runway = _runway_for_heading(plane.STRIPS["KSC"], approach_heading)
    r = plane.start(runway or None, approach_speed or None, touch_and_go=int(touch_and_go or 0))
    if r != "started":
        return r
    time.sleep(1.5)
    return (f"Autoland engaged for runway {plane.STATUS.get('runway', '?')} (heading {plane.STATUS.get('runway_heading', '?')}): "
            "flying to final, then glideslope, flare and brakes. I'll post the result in chat.")


def fly_to(name: str, cruise_altitude_m: float = 0, cruise_speed: float = 0) -> str:
    """PLANES: fly to a runway spot (see list_landing_spots, e.g. "Runway 27", "Island Airfield") and land there; takes off first if we're on the ground. cruise_altitude_m / cruise_speed (m/s) for a long hop or a supersonic dash (Mach 1 is ~300 m/s up high; speed is limited to 200 m/s equivalent airspeed (220 hard cap) so it may only go fast up high; a supersonic dash needs ~11-12 km, e.g. cruise_altitude_m=12000, cruise_speed=360); 0 = defaults (low, ~110 m/s). It climbs, cruises, then plans the descent and slows down early so it arrives at the approach slow. Checks fuel range first. A speed above the cap, or an altitude above the craft's estimated safe ceiling, answers 'needs_override: ...' - relay that yes/no question to Luke and wait (the bridge applies it on his yes)."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    c = conn()
    sc = c.space_center
    v = sc.active_vessel
    label, spot = spots.resolve(sc, name)
    if label is None:
        return spot
    strip = spots.make_strip(spot) if spot.get("mode") == "H" else None
    if strip is None:
        return f"'{label}' isn't a runway spot; fly_to needs a runway (H spot)."
    err = _plane_check(v, strip, allow_ground=True)
    if err:
        return err
    body = v.orbit.body
    f = v.flight(body.reference_frame)
    a = strip["a"]
    dist = spots.gc_dist(f.latitude, f.longitude, a[0], a[1], body.equatorial_radius)
    # fuel range estimate: jets' fuel flow at ~30% thrust over the trip time (+30% reserve)
    jets = [e for e in v.parts.engines if "IntakeAir" in set(e.propellant_names)]
    lf = v.resources.amount("LiquidFuel")
    spd = float(cruise_speed or 110)
    alt = float(cruise_altitude_m or 0)
    if alt > 0:  # above the craft's estimated safe ceiling: ask for override authority first
        need = speedcap.check_altitude("fly_to", {"name": name, "cruise_altitude_m": cruise_altitude_m,
                                                  "cruise_speed": cruise_speed}, alt, tko.vessel_ceiling(v)[0],
                                       "cruise_altitude_m")
        if need:
            return need
    note = ""
    if cruise_speed:  # physical limit (best estimate, thrust vs drag): clamp and say so
        spd, note = maxspeed.clamp(v, spd, alt if alt > 0 else None)
        if note:
            cruise_speed = spd
            note += " "
    if cruise_speed and alt > 0:  # above the speed cap at that altitude: ask for override authority first
        need = speedcap.check("fly_to", {"name": name, "cruise_altitude_m": cruise_altitude_m,
                                         "cruise_speed": cruise_speed}, spd, alt, "cruise_speed")
        if need:
            return need.replace("needs_override: ", "needs_override: " + note, 1)
    flow = 0.0
    for e in jets:
        try:
            isp = e.kerbin_sea_level_specific_impulse or e.specific_impulse or 4000.0
            flow += e.max_thrust / (max(1.0, isp) * 9.81) / 5.0  # LiquidFuel units/s at full thrust (5 kg/unit)
        except Exception:
            pass
    trip_s = (dist + 11000 + alt / math.tan(math.radians(6))) / max(spd * 0.8, 60)
    need_units = flow * 0.3 * trip_s * 1.3  # ~30% average thrust, +30% reserve
    if lf > 0 and need_units > lf:
        return (f"enough_fuel: false. Probably not enough fuel: ~{need_units:.0f} units needed for {dist / 1000:.0f} km at "
                f"{spd:.0f} m/s (+30% reserve), have {lf:.0f}. Try a lower speed / altitude.")
    if spd > 300 and not jets and not any("IntakeAir" not in set(e.propellant_names) for e in v.parts.engines):
        return "No engines that can push past Mach 1."
    cruise_alt = max(alt, 0.0) or None
    eas_req = speedcap.eas_at(spd, alt) if alt > 0 else 0.0
    granted = eas_req > plane.V_LOW and speedcap.limits()[0] >= eas_req - 0.5  # override: keep Luke's altitude
    if spd > plane.V_LOW and not granted:  # speed limit is EAS 200 (220 hard): faster needs thinner air (H ~5.6 km)
        need_alt = 5600.0 * 2 * math.log(spd / plane.V_LOW) + 2000.0
        cruise_alt = max(cruise_alt or 0.0, need_alt)
    _set_final(0)
    r = plane.start(spot.get("runway"), None, spd if cruise_speed else None, dict(strip, name=label), cruise_alt)
    if r != "started":
        return r
    time.sleep(1.5)
    return note + (f"{'Taking off, then f' if str(v.situation).split('.')[-1] in ('landed', 'pre_launch') else 'F'}lying to "
            f"{label} ({dist / 1000:.0f} km)" + (f" at {cruise_alt:.0f} m / {spd:.0f} m/s" if cruise_alt else "")
            + f", then landing on runway {plane.STATUS.get('runway', '?')}. I'll post the result in chat.")


def get_landing_eta() -> dict:
    """Estimated distance and time to touchdown for the current landing or descent (rocket, plane or parachutes): predicted impact point / runway threshold, distance in km, ETA in seconds, and how it was estimated. All values are estimates."""
    return spots.landing_eta(conn())


def list_landing_spots() -> dict:
    """Named landing spots: built-ins (KSC Pad, Runway 09, Runway 27, Island Airfield) plus Luke's saved ones. mode V = vertical landing point (rockets), H = runway (planes)."""
    out = {}
    for k, s in spots.all_spots().items():
        lat, lon = spots.spot_point(s)
        out[k] = {"mode": s["mode"], "body": s.get("body", "Kerbin"), "lat": round(lat, 4), "lon": round(lon, 4)}
        if s["mode"] == "H":
            st = spots.make_strip(s)
            out[k]["runways"] = [s["runway"]] if s.get("runway") else (list(st["names"]) if st else [])
    return out


def save_landing_spot(name: str, mode: str = "", latitude: float = 999, longitude: float = 999, heading: float = -1) -> str:
    """Save a named landing spot. Leave latitude/longitude out to mark the vessel's CURRENT position. mode "V" = vertical landing point (rockets); "H" = runway for planes, starting at that point in the landing direction 'heading' (default: the vessel's current heading), assumed ~1 km long. Empty mode: H for a plane on the ground, else V."""
    v = _vessel()
    body = v.orbit.body
    f = v.flight(body.reference_frame)
    here = latitude == 999 or longitude == 999
    lat, lon = (f.latitude, f.longitude) if here else (float(latitude), float(longitude))
    m = (mode or "").strip().upper()[:1]
    sit = str(v.situation).split(".")[-1]
    if m not in ("V", "H"):
        m = "H" if heading >= 0 or (here and sit in ("landed", "pre_launch") and _planeish(v)) else "V"
    spot = {"mode": m, "body": body.name, "lat": round(lat, 6), "lon": round(lon, 6), "name": name}
    if m == "H":
        spot["heading"] = round(heading if heading >= 0 else v.flight(v.surface_reference_frame).heading, 1)
        spot["length"] = 1000
    r = spots.save(name, spot)
    if r != "saved":
        return r
    extra = f", runway heading {spot['heading']:.0f}" if m == "H" else ""
    return f"Saved landing spot '{name}' ({m}) at {lat:.4f}, {lon:.4f} on {body.name}{extra}."


def _set_final(final_km):
    """Final approach length for the next plane landing (0 = default 11 km)."""
    km = float(final_km or 0)
    settings.put("plane_final_m", max(3.0, min(30.0, km)) * 1000.0 if km > 0 else 0)


def _runway_for_heading(strip, hdg):
    """Runway name whose landing direction is closest to heading hdg (names[0] lands a->b)."""
    names = tuple(strip.get("names") or ("A", "B"))
    a, b = strip["a"], strip["b"]
    brg = spots.bearing(a[0], a[1], b[0], b[1])
    return names[0] if abs((float(hdg) - brg + 180.0) % 360.0 - 180.0) < 90.0 else names[1]


def landing_check() -> str:
    """Before a vertical landing: parachutes on board, delta-v available vs needed for a powered landing, verdict."""
    v = _vessel()
    d = _dv_numbers(v)
    chutes = len(v.parts.parachutes)
    need = d.get("landing_dv_needed_estimate")
    have = d["total_dv"]
    body = v.orbit.body
    air = body.has_atmosphere
    ok_dv = need is not None and not isinstance(need, str) and have >= need * 1.1
    if ok_dv:
        verdict = "powered landing OK"
    elif chutes and air:
        verdict = "not enough dV for a fully powered landing - use the parachutes (they're armed by the lander)"
    else:
        verdict = "NOT ENOUGH dV and " + ("no parachutes" if not chutes else f"no air on {body.name} for chutes") + " - don't land this way"
    return (f"Landing check (enough_fuel: {'true' if ok_dv else 'false'}): {chutes} parachute(s), dV have ~{have} m/s, need ~{need if need is not None else '?'} m/s "
            f"(TWR {d['twr_surface']}) on {body.name}{' (atmosphere)' if air else ''}: {verdict}.")


def land_at_spot(name: str, mode: str = "", final_km: float = 0, approach_heading: float = -1,
                 chute_check: bool = False) -> str:
    """Land at a named place: a saved landing spot (see list_landing_spots), "target" for whatever is set as the KSP target (vessel or flag), or the name of a vessel, flag or waypoint to home in on like a radio beacon. mode "V" = vertical powered landing (rockets), "H" = runway landing (planes, needs a runway spot); empty = pick from the craft. Checks delta-v/feasibility first. Planes: final_km (0 = 11 km default, 5 = short final), approach_heading (heading to land on, picks the runway end; -1 = auto). Rockets: chute_check=true reports parachutes + dV first and refuses if neither is enough."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    c = conn()
    sc = c.space_center
    v = sc.active_vessel
    label, spot = spots.resolve(sc, name)
    if label is None:
        return spot
    body = v.orbit.body
    if spot.get("mode") == "DOCK":
        return dock_with(label)
    if spot.get("body", body.name) != body.name:
        return f"'{label}' is on {spot.get('body')}; we're at {body.name}."
    planeish = _planeish(v)
    m = (mode or "").strip().upper()[:1]
    if m not in ("V", "H"):
        m = "H" if planeish else "V"
    if m == "H":
        strip = spots.make_strip(spot)
        if strip is None:
            return (f"'{label}' is a point, not a runway. Planes need a runway spot (save one with a heading), "
                    "or use mode V for a rocket.")
        err = _plane_check(v, strip, allow_ground=True)
        if err:
            return err
        if plane.active():
            return "A plane landing is already in progress."
        rwy = spot.get("runway")
        if approach_heading is not None and approach_heading >= 0:
            rwy = _runway_for_heading(strip, approach_heading)
        strip = dict(strip, name=label)
        _set_final(final_km)
        r = plane.start(rwy, None, None, strip)
        if r != "started":
            return r
        time.sleep(1.5)
        return (f"Autoland engaged for {label} runway {plane.STATUS.get('runway', '?')} "
                f"(heading {plane.STATUS.get('runway_heading', '?')}). I'll post the result in chat."
                + (" Note: this runway's coordinates are approximate." if spot.get("approx") else ""))
    pre = ""
    if chute_check:
        pre = landing_check() + " "
        if "don't land" in pre:
            return pre + "Not starting the landing (untick the chute check or add force via chat to override)."
    lat, lon = spots.spot_point(spot)
    if spot.get("offset_m"):  # don't land on top of the beacon vessel/flag: stop short on our side of it
        f = v.flight(body.reference_frame)
        brg = spots.bearing(lat, lon, f.latitude, f.longitude)
        lat, lon = spots.offset(lat, lon, brg, spot["offset_m"], body.equatorial_radius)
    r = land_at(lat, lon)
    return pre + f"[{label}] " + r


def dock_with(name: str = "target") -> str:
    """Rendezvous and dock with a vessel in orbit (e.g. a station or refuel depot): "target" = the current KSP target, or a vessel name. Checks that both ships have a free docking port of the same size, picks the nearest free station port, checks delta-v and RCS, then MechJeb's rendezvous + docking autopilots fly it (EXPERIMENTAL). Only when Luke asks to dock/rendezvous."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    c = conn()
    sc = c.space_center
    v = sc.active_vessel
    if spots._norm(name) in ("", "target", "my target", "the target"):
        tv = sc.target_vessel
        if tv is None and sc.target_docking_port is not None:
            tv = sc.target_docking_port.part.vessel
        if tv is None:
            return "No KSP target set; target the station on the map or name it."
    else:
        tv = next((x for x in sc.vessels if spots._norm(x.name) == spots._norm(name) and x != v), None)
        if tv is None:
            return f"No vessel named '{name}'."
    p, err = docking.plan(sc, tv)
    if err:
        return err
    have = _dv_numbers(v)["total_dv"]
    if have < p["dv_needed"] * 1.2:
        return (f"enough_fuel: false. Not enough delta-v to reach {tv.name}: have ~{have} m/s, need ~{p['dv_needed']} m/s "
                f"(relative inclination {p['rel_incl_deg']} deg).")
    if not p["rcs"]:
        return "No RCS thrusters or monopropellant: MechJeb's docking autopilot needs RCS."
    r = docking.start(tv.name)
    if r != "started":
        return r
    return (f"Rendezvous with {tv.name} (~{p['dv_needed']} m/s of ~{have}), then docking at its nearest free "
            f"{p['size']} port. MechJeb flies it; I'll post the result in chat.")


def _stop_controllers():
    """Stop every bridge controller (flight plan, landers, plane/heli/taxi/docking, holds) and other processes' ones."""
    try:
        from . import flightplan
        flightplan.stop()  # a running Flight Plan must not start its next step after an abort
    except Exception:
        pass
    lander.stop()
    plane.stop()
    hold.stop()
    try:
        from . import heli
        heli.stop()
    except Exception:
        pass
    taxi.stop()
    docking.stop()
    guard.request_stop()  # also stops controllers running in other processes (harness, MCP)


def _mj_autopilots_off():
    try:
        mj = conn().mech_jeb
        if mj.api_ready:
            mj.ascent_autopilot.enabled = False
            for ap_name in ("rendezvous_autopilot", "docking_autopilot"):
                try:
                    getattr(mj, ap_name).enabled = False
                except Exception:
                    pass
            mj.node_executor.abort()
            if mj.landing_autopilot.enabled:
                mj.landing_autopilot.stop_landing()
    except Exception:
        pass


def stop_current() -> str:
    """'stop current (plan)': cancel the running flight plan and every autopilot (plane / heli / lander / taxi /
    docking, holds, MechJeb) but leave the throttle where it is (never 0 in flight) and fire nothing. SAS on.
    The harder version is abort."""
    _stop_controllers()
    v = _vessel()
    _mj_autopilots_off()
    try:
        v.control.sas = True
    except Exception:  # noqa: BLE001
        pass
    from . import heli
    t0 = time.time()  # let the controller threads finish so a follow-up order ('..., and land at 27') can start
    while time.time() - t0 < 6:
        if not any(m.active() for m in (plane, hold, heli, taxi, lander) if hasattr(m, "active")) and not guard.busy():
            break
        time.sleep(0.2)
    return "Current plan cancelled: autopilots off, throttle unchanged."


def abort() -> str:
    """Safe abort: throttle to 0, disengage MechJeb autopilots, SAS on. Does NOT fire the abort action group."""
    _stop_controllers()
    v = _vessel()
    try:
        flying_plane = str(v.situation).split(".")[-1] == "flying" and bool(v.parts.wheels) and _has_wings(v)
    except Exception:
        flying_plane = False
    if not flying_plane:  # a plane in flight keeps its throttle (never chop it in the air - Luke)
        v.control.throttle = 0.0
    _mj_autopilots_off()
    v.control.sas = True
    return "Aborted: throttle 0, MechJeb autopilots off, SAS on."


def _nearest_runway_elev(v):
    """Elevation of the nearest known runway within 60 km (for AGL targets = 'above the field'), else None."""
    try:
        body = v.orbit.body
        f = v.flight(body.reference_frame)
        best = None
        for name, sp in spots.all_spots().items():
            st = spots.make_strip(sp) if sp.get("mode") == "H" else None
            if not st or st.get("body", "Kerbin") != body.name:
                continue
            d = spots.gc_dist(f.latitude, f.longitude, st["a"][0], st["a"][1], body.equatorial_radius)
            elev = st.get("alt")
            if elev is None:
                elev = plane.RWY_ALT if sp.get("strip") == "KSC" else None
            if elev is not None and d < 60_000 and (best is None or d < best[0]):
                best = (d, float(elev), name)
        return best
    except Exception:
        return None


HELI_AGL_BELOW = 500.0


def _heli_v(v):
    """Helicopter info dict if the craft is a helicopter, else None."""
    try:
        from . import heli
        return heli.scan(v) if heli.is_heli(v) else None
    except Exception:
        return None


def heli_control(mode: str = "hover", altitude_m: float = -1, heading: float = -1, speed: float = -1,
                 direction: str = "", distance: float = -1) -> str:
    """HELICOPTERS (Breaking Ground rotor craft, incl. compound helis with side props). HEADING (nose) and TRACK
    (direction over the ground) are separate. mode = "hover" (hold here at altitude_m above the ground; default 20 m;
    from the ground = vertical takeoff; heading = hover facing N), "climb" / "descend" (to altitude_m AGL; bare
    descend = down to 10 m), "land" (slow vertical descent, touchdown < 1.5 m/s), "fly" (heading = the TRACK to fly,
    + speed m/s; the nose follows the track above 15 m/s unless pinned by face), "face" (point the nose to heading,
    travel unchanged), "sidestep" (direction left/right, distance m, default 5), "back" (back up distance m,
    default 10), "hold" (hold position). Collective = vertical speed; tilt = ground velocity; yaw = heading."""
    from . import heli
    v = _vessel()
    info = _heli_v(v)
    m = str(mode or "hover").strip().lower()
    if info is None:
        if str(v.situation).split(".")[-1] in ("landed", "pre_launch") and _planeish(v):  # a plane, not a heli
            alt = float(altitude_m) if altitude_m is not None and altitude_m > 0 else 300.0
            return "Not a helicopter - plane runway takeoff instead: " + takeoff(altitude_m=alt)
        if m == "fly":
            out = []
            if heading is not None and heading >= 0:
                out.append(set_heading(heading))
            if speed is not None and speed > 0:
                out.append(set_speed(speed))
            return " ".join(out) or "Give a heading and/or a speed."
        return "That's not a helicopter (no vertical-axis lift rotor) - for planes use the holds / autoland."
    claim = guard.busy()
    if claim and not (claim.get("kind") == "heli" and heli.active()):
        return guard.refuse_msg()
    f = v.flight(v.orbit.body.reference_frame)
    agl = float(f.surface_altitude)
    sit = str(v.situation).split(".")[-1]
    alt = float(altitude_m) if altitude_m is not None and altitude_m >= 0 else None
    hdg = float(heading) % 360.0 if heading is not None and heading >= 0 else None
    spd = float(speed) if speed is not None and speed >= 0 else None
    pre = ""
    if sit != "flying" and m in ("takeoff", "hover", "climb"):
        from . import parking, propulsion
        parking.release("heli takeoff")
        heli.STATE.pop("spooled", None)
        propulsion.reset_spool_state()
        # Bridge (not the model): release brakes, engage motors, ramp torque, wait for ~90% RPM on EVERY rotor
        # before the heli thread adds collective. Live miss (daeebe3): preflight alone left Brake 100 / Torque 0.
        lift = set(info.get("lift") or ())
        others = set(info.get("left") or ()) | set(info.get("right") or ()) | set(info.get("tail") or ())
        ok, report = propulsion.spool_up(v, rotors=lift or None)
        pre = report
        if not ok:
            return f"Heli takeoff aborted: {report}"
        if others:
            ok2, report2 = propulsion.spool_up(v, rotors=others)
            pre = f"{report}; {report2}"
            if not ok2:
                return f"Heli takeoff aborted (side/tail): {report2}"
        heli.STATE["spooled"] = True  # _fly skips a second full ramp when RPM is already there
        m = "hover" if m == "climb" else m
    if m in ("takeoff", "hover"):
        alt = alt if alt is not None else (max(agl, heli.HOVER_AGL) if sit == "flying" else heli.HOVER_AGL)
        r = heli.command("hover", alt=alt, hold=None, face=hdg if hdg is not None else "clear")
        what = (f"vertical takeoff to a hover at {alt:.0f} m AGL" if sit != "flying"
                else f"hovering at {alt:.0f} m AGL, holding this spot") + (f", facing {hdg:03.0f}" if hdg is not None else "")
    elif m == "face":
        if hdg is None:
            return "Face which heading? e.g. 'face 270'."
        if sit != "flying" and not heli.active():
            return "We're on the ground - take off first, then 'face N'."
        r = heli.command(heli.STATE["mode"] if heli.active() else "hold", face=hdg)
        what = f"nose to {hdg:03.0f} (travel unchanged; 'fly N' unpins it)"
    elif m in ("sidestep", "back"):
        if sit != "flying":
            return "We're on the ground - nothing to sidestep."
        hdg_now = float(v.flight(v.surface_reference_frame).heading)
        dist = float(distance) if distance is not None and distance > 0 else (heli.SIDESTEP_M if m == "sidestep" else heli.BACKUP_M)
        side = str(direction or "").lower()
        if m == "sidestep" and side not in ("left", "right"):
            return "Sidestep left or right?"
        brg = (hdg_now + (180.0 if m == "back" else (-90.0 if side == "left" else 90.0))) % 360.0
        pt = spots.offset(float(f.latitude), float(f.longitude), brg, dist, v.orbit.body.equatorial_radius)
        r = heli.command("hold", hold=tuple(pt[:2]), face=heli.STATE.get("face") or hdg_now,
                         alt=heli.STATE.get("alt") or agl)
        what = (f"sidestepping {side} {dist:.0f} m" if m == "sidestep" else f"backing up {dist:.0f} m") + \
            f", nose stays on {hdg_now:03.0f}"
    elif m in ("climb", "descend"):
        alt = alt if alt is not None else (heli.DESCEND_AGL if m == "descend" else agl + 20.0)
        if m == "climb" and alt < agl - 2:
            return f"{alt:.0f} m AGL is below us ({agl:.0f} m) - say 'descend to {alt:.0f}'."
        if m == "descend" and alt > agl + 2:
            return f"{alt:.0f} m AGL is above us ({agl:.0f} m) - say 'climb to {alt:.0f}'."
        r = heli.command(m, alt=alt)
        what = f"{'climbing' if m == 'climb' else 'descending'} to {alt:.0f} m AGL (max {heli.VS_UP:.0f} m/s)"
    elif m == "land":
        if sit != "flying":
            return "We're already on the ground."
        r = heli.command("land")
        what = "landing straight down: 3 m/s, 2 below 30 m, 1 below 10 m, touchdown under 1.5 m/s on the radar altitude"
    elif m == "fly":
        if sit != "flying":
            return "Take off first ('take off' = vertical liftoff to a hover)."
        spd = _clampf(spd if spd is not None else (heli.STATE.get("speed") or 20.0), 0.0, heli.SPD_MAX)
        trk = hdg if hdg is not None else (heli.STATE.get("track") if heli.STATE.get("track") is not None
                                            else float(v.flight(v.surface_reference_frame).heading))
        r = heli.command("fly", track=trk, speed=spd, face="clear" if hdg is not None else None,
                         alt=max(agl, heli.HOVER_AGL) if heli.STATE.get("alt") is None else None)
        what = (f"flying track {trk:03.0f} at {spd:.0f} m/s" +
                (", nose follows the track" if spd > heli.NOSE_FOLLOW and heli.STATE.get("face") is None else
                 f", nose stays on {heli.STATE['face']:03.0f}" if heli.STATE.get("face") is not None else ""))
    elif m in ("hold", "hold position"):
        if sit != "flying":
            return "We're on the ground - nothing to hold."
        r = heli.command("hold", hold=(float(f.latitude), float(f.longitude)), alt=heli.STATE.get("alt") or agl)
        what = "holding this position"
    else:
        return "Heli modes: hover, climb, descend, land, fly, face, sidestep, back, hold."
    return (f"Heli autopilot {'engaged' if r == 'started' else 'updated'} ({info['kind']}, yaw by {info['yaw']}): "
            f"{what}." + (f" Layout: {info['roles']}." if r == "started" and info.get("roles") else "")
            + (" Main rotor is set to 'On Power Loss: Locked' - no autorotation if it loses power."
                          if info.get("locked") and r == "started" else "") + (f" {pre}." if pre else ""))


def takeoff(altitude_m: float = 300) -> str:
    """PLANES on the ground: runway takeoff ("take off", "takeoff") - jets, rockets or PROPELLERS. Rolls on the runway
    heading, rotates and climbs to altitude_m above the field, then the autopilot holds fly on. Never use
    mechjeb_ascent for a plane takeoff. HELICOPTERS / MULTIROTORS: vertical liftoff to a 20 m hover (spool every rotor)."""
    from . import propulsion
    v = _vessel()
    if _heli_v(v) is not None:
        return heli_control("hover", altitude_m=-1)
    sit = str(v.situation).split(".")[-1]
    if sit == "flying":
        return "We're already airborne."
    if sit not in ("landed", "pre_launch"):
        return f"Can't take off from here ({sit})."
    pk = propulsion.classify(v)
    # Multirotor / heli without wings: clear scan cache and vertical-takeoff (don't demand wings+wheels)
    if pk["rotors"] >= 3 or (pk["rotors"] and not _planeish(v)):
        from . import heli as heli_mod
        heli_mod.clear_vessel_cache()
        if _heli_v(v) is not None:
            return heli_control("hover", altitude_m=-1)
    if not _planeish(v):
        return ("Takeoff is for planes (wings + wheels). For a rocket say 'launch' (MechJeb ascent to orbit). "
                "For a multirotor / heli, the bridge needs ModuleRoboticServoRotor parts with blades.")
    if not pk["any"]:
        return "No engines or propellers found on this craft - nothing to take off with."
    pre = ""
    if pk["rotors"]:
        pre, _ = propulsion.preflight(v)  # Luke: every rotor Brake 0, Torque Limit > 0, Motor Engaged
    if not hold.active():
        settings.put(hold.KEY, {"t": time.time()})  # fresh targets (no stale roll/heading/speed from earlier)
    r = plane_hold(altitude_m=float(altitude_m), altitude_ref="agl")
    if pre:
        r += f" {pre}."
    if pk["props"]:
        r += (" Propellers: motor on, RPM and torque max, blades deployed at a fine pitch (then pitch follows the "
              "airspeed); kRPC can't read their thrust, so the roll acceleration governs the throttle.")
    return r


def prop_control(pitch: str = "", rpm: str = "", torque: str = "", motor: str = "", reverse: str = "",
                 group: str = "") -> str:
    """PROPELLER craft (Breaking Ground rotors + blades): pitch = blade pitch in deg or "auto" (speed schedule);
    rpm = RPM limit or "max"; torque = torque limit % or "max"/"auto"; motor = "on"/"off"; reverse = "on"/"off"
    (reverse thrust, on the ground only); group = "left" / "right" / "center" to act on one side's rotors only
    (rotors are grouped by their side of the center of mass). Never cuts props in flight (torque/RPM floors, motor
    stays on)."""
    from . import propulsion
    v = _vessel()
    if not propulsion.has_props(v):
        return "No propellers (rotor + blades) on this craft."
    g = str(group or "").strip().lower().replace("centre", "center") or None
    if g is not None and g not in propulsion.groups(v):
        return f"No {g} propellers on this craft (groups: {', '.join(propulsion.groups(v)) or 'none'})."
    sit = str(v.situation).split(".")[-1]
    flying = sit == "flying"
    out = []

    def val(x, mx):
        x = str(x).strip().lower()
        return mx if x in ("max", "full", "maximum") else float(x)
    try:
        if str(reverse).strip():
            ok, msg = propulsion.reverse(v, str(reverse).lower() in ("on", "true", "1", "reverse"), sit, group=g)
            out.append(msg)
        p = str(pitch).strip().lower()
        if p == "auto":
            propulsion.STATE["manual_pitch"] = None
            propulsion.GOV.last_pitch = None
            out.append("blade pitch back on the speed schedule")
        elif p:
            propulsion.STATE["manual_pitch"] = float(p)
            out.append(propulsion.set_blades(v, pitch=float(p), deploy=True, flying=flying, group=g))
        if str(rpm).strip():
            out.append(propulsion.set_rotor(v, rpm=val(rpm, propulsion.RPM_MAX), flying=flying, group=g))
        t = str(torque).strip().lower()
        if t == "auto":
            propulsion.STATE["manual_torque"] = False
            propulsion.GOV.last_torque = None
            out.append("torque back on the autopilot throttle")
        elif t:
            propulsion.STATE["manual_torque"] = True
            out.append(propulsion.set_rotor(v, torque=val(t, propulsion.TORQUE_MAX), flying=flying, group=g))
        m = str(motor).strip().lower()
        if m:
            out.append(propulsion.set_rotor(v, motor=m in ("on", "true", "1", "engage", "engaged"), flying=flying,
                                            group=g))
    except ValueError:
        return "Give a number (or max / auto), e.g. 'prop pitch 20', 'rpm max', 'torque 60'."
    return ("Props: " + "; ".join(x for x in out if x) + ".") if out else "Nothing to change."


def damage_report() -> str:
    """'damage report' / 'what are we missing' / 'are we damaged': the parts the emergency watcher actually saw come
    off this flight, whether the craft is upside down, and the active alerts. Use it instead of guessing."""
    from . import emergency
    return emergency.damage_text()


def plane_hold(altitude_m: float = -1, altitude_ref: str = "agl", vertical_speed: float = -999, heading: float = -1,
               roll: float = -999, speed: float = -1, off: str = "", engage: bool = True) -> str:
    """PLANES: aircraft autopilot holds (like an airliner autopilot). Set any of: altitude_m (with altitude_ref "agl" =
    above the ground/field - default - or "msl"), vertical_speed (m/s, climbs are flown with the throttle), heading
    (deg), roll (bank deg; overrides heading), speed (m/s, throttle only). Unset values keep their current setting
    (-1 / -999 = leave). off = comma list of holds to turn off (e.g. "roll,speed"). Changes apply LIVE without
    restarting. engage=false disengages the holds (throttle left where it is). Takes off first (on the runway
    heading) if on the ground and lined up on a runway. Use for "set altitude 7200", "hold heading 270", "climb at
    50 m/s", "set speed 250". Pitch attitude ("pitch up 10") -> plane_pitch; setting altitude or V/S ends a pitch
    hold. Not for landing (use fly_to / land_plane). A speed above the cap or an
    altitude above the estimated safe ceiling answers 'needs_override: ...' - relay that yes/no question to Luke and wait (the bridge applies it on his yes)."""
    call_args = dict(altitude_m=altitude_m, altitude_ref=altitude_ref, vertical_speed=vertical_speed, heading=heading,
                     roll=roll, speed=speed, off=off, engage=engage)
    if not engage:
        if hold.active():
            hold.set_targets(engaged=False)
            hold.stop()
            return "Autopilot holds disengaged (throttle left as is)."
        return "The holds weren't engaged."
    v = _vessel()
    if _heli_v(v) is not None:
        return "This is a helicopter - the plane holds would wreck it. Use heli_control (hover / climb / fly / land)."
    claim = guard.busy()
    if claim and not (claim.get("kind") == "plane" and hold.active()):
        return guard.refuse_msg() + " (For a running landing, abort first, then engage the holds.)"
    sit = str(v.situation).split(".")[-1]
    if sit not in ("flying", "landed", "pre_launch"):
        return f"Holds are for aircraft in the atmosphere (situation: {sit})."
    if not v.parts.wheels and not _has_wings(v):
        return "That doesn't look like a plane (no wings) - holds are for aircraft."
    protect.ensure()  # G / heat / breakage watcher + blackout handling while the holds fly
    upd, said = {"engaged": True}, []
    if altitude_m is not None and altitude_m >= 0:
        ref = (altitude_ref or "agl").lower()
        ref = "msl" if ref in ("msl", "sea", "asl") else "agl"
        upd.update(altitude=float(altitude_m), altitude_ref=ref)
        if ref == "agl":
            nr = _nearest_runway_elev(v)
            if nr:
                upd["ref_elev"] = nr[1]
                said.append(f"altitude {altitude_m:.0f} m above {nr[2]} (field elevation {nr[1]:.0f} m -> {altitude_m + nr[1]:.0f} m MSL)")
            else:
                upd["ref_elev"] = None
                said.append(f"altitude {altitude_m:.0f} m above the ground here")
        else:
            said.append(f"altitude {altitude_m:.0f} m MSL")
    if vertical_speed is not None and vertical_speed > -900:
        upd["vertical_speed"] = float(vertical_speed)
        said.append(f"V/S {vertical_speed:+.0f} m/s")
    if heading is not None and heading >= 0:
        upd["heading"] = float(heading) % 360
        upd["roll"] = "off"
        said.append(f"heading {heading % 360:.0f}")
    if roll is not None and roll > -900:
        upd["roll"] = float(roll)
        said.append(f"roll {roll:+.0f} deg")
    if speed is not None and speed > 0:
        upd["speed"] = float(speed)
        said.append(f"speed {speed:.0f} m/s")
    for k in [x.strip().lower() for x in (off or "").split(",") if x.strip()]:
        k = {"alt": "altitude", "vs": "vertical_speed", "v/s": "vertical_speed", "hdg": "heading", "bank": "roll",
             "spd": "speed", "nose": "pitch"}.get(k, k)
        upd[k] = "off"
        said.append(f"{k} off")
    if isinstance(upd.get("altitude"), float) or isinstance(upd.get("vertical_speed"), float) or upd.get("pitch") == "off":
        upd.update(pitch="off", pitch_t="off", pitch_ceiling="off")  # a new altitude / V/S command ends a pitch hold
    if isinstance(upd.get("speed"), float) or isinstance(upd.get("altitude"), float) \
            or isinstance(upd.get("vertical_speed"), float):
        upd["throttle"] = "off"  # ... and hands a manual throttle back to the holds
    alt_tgt = None
    if isinstance(upd.get("altitude"), float):  # above the estimated safe ceiling: ask for override authority first
        elev = upd.get("ref_elev") if isinstance(upd.get("ref_elev"), float) else 0.0
        alt_tgt = upd["altitude"] + (elev if upd.get("altitude_ref") == "agl" else 0.0)
        need = speedcap.check_altitude("plane_hold", call_args, alt_tgt, tko.vessel_ceiling(v)[0],
                                       given=upd["altitude"])
        if need:
            return need
    note = ""
    if isinstance(upd.get("speed"), float):
        # physical limit first (best estimate from thrust vs drag): clamp and say so; then the cap / override check
        fly, note = maxspeed.clamp(v, upd["speed"], alt_tgt)
        if note:
            said = [x if not x.startswith("speed ") else f"speed {fly:.0f} m/s" for x in said]
            upd["speed"] = call_args["speed"] = fly
            note += " "
        alt_chk = alt_tgt if alt_tgt is not None else v.flight(v.orbit.body.reference_frame).mean_altitude
        need = speedcap.check("plane_hold", call_args, upd["speed"], alt_chk)
        if need:
            return need.replace("needs_override: ", "needs_override: " + note, 1)
    if "ref_elev" in upd and upd["ref_elev"] is None:
        upd["ref_elev"] = "off"
    hold.set_targets(**upd)
    if hold.active():
        return note + "Holds updated: " + (", ".join(said) or "no change") + "."
    r = hold.start()
    time.sleep(1.0)
    try:
        from . import craft_notes
        if craft_notes.apply_on_engage(v):
            said.append("loaded craft trim notes")
    except Exception:  # noqa: BLE001
        pass
    first = "Taking off on the runway heading, then holding " if sit in ("landed", "pre_launch") else "Holds engaged: "
    return note + first + (", ".join(said) or "current altitude and heading") + "."


DESCENT_VS, DESCENT_VS_FAST = 15.0, 30.0  # m/s; the hold's nose-down limit (-5 deg) and overspeed guard still apply


def set_altitude(altitude_m: float, direction: str = "", ref: str = "msl", urgent: bool = False) -> str:
    """Direct 'descend to 5 km' / 'climb to 8000' / 'altitude 3000 [agl]' for a plane in the air: the autopilot
    holds' altitude target (engages the holds if nothing is flying the plane). urgent ('max down', 'now') descends
    at up to 30 m/s instead of 15, still within the hold's nose-down limit; it never dives for speed past the cap.
    Above the estimated safe ceiling it asks for override authority (yes/no), like plane_hold."""
    v = _vessel()
    if _heli_v(v) is not None:
        f = v.flight(v.orbit.body.reference_frame)
        agl_now = float(f.surface_altitude)
        # helicopters fly low: heights under HELI_AGL_BELOW m are taken as above the ground whatever the ref says
        t_agl = (float(altitude_m) if str(ref).lower() == "agl" or float(altitude_m) < HELI_AGL_BELOW
                 else float(altitude_m) - (float(f.mean_altitude) - agl_now))
        return heli_control("climb" if t_agl >= agl_now else "descend", altitude_m=max(1.0, t_agl))
    if not _flying(v):
        return "Altitude orders are for a plane in the air - take off first."
    alt = float(v.flight(v.orbit.body.reference_frame).mean_altitude)
    tgt = float(altitude_m)
    r_ = "agl" if str(ref).lower() == "agl" else "msl"
    tgt_msl = tgt if r_ == "msl" else tgt + max(0.0, alt - float(v.flight(v.orbit.body.reference_frame).surface_altitude))
    d = str(direction or "").lower()
    if d == "down" and tgt_msl > alt + 50:
        return f"{tgt:.0f} m is above us ({alt:.0f} m) - say 'climb to {tgt:.0f}' if that's what you want."
    if d == "up" and tgt_msl < alt - 50:
        return f"{tgt:.0f} m is below us ({alt:.0f} m) - say 'descend to {tgt:.0f}' if that's what you want."
    if plane.active() and not hold.active():  # landing / fly-to autopilot: change its en-route cruise altitude
        ph = plane.STATUS.get("phase", "")
        if ph in ("final", "flare", "rollout"):
            return f"{_phase_name(ph).capitalize()}: the glide path sets the altitude now - say 'go around' and give it again."
        need = speedcap.check_altitude("set_altitude", {"altitude_m": altitude_m, "direction": direction, "ref": ref,
                                                        "urgent": urgent}, tgt_msl, tko.vessel_ceiling(v)[0], "altitude_m")
        if need:
            return need
        plane.LIVE["cruise_alt"] = tgt_msl
        return (f"En-route cruise altitude {tgt_msl:.0f} m MSL" + (" (from the next en-route leg)" if ph != "to_entry" else "")
                + "; the descent to the runway is still planned.")
    args = {"altitude_m": tgt, "altitude_ref": r_}
    rate = ""
    if tgt_msl < alt - 50:
        vs = DESCENT_VS_FAST if urgent else DESCENT_VS
        args["vertical_speed"] = -vs
        rate = f" Descending at up to {vs:.0f} m/s (nose-down limit and speed cap still apply)."
    r = plane_hold(**args)
    if r.startswith("needs_override") or not (r.startswith("Holds") or r.startswith("Autopilot")):
        return r
    return r + rate


def set_speed(speed: float) -> str:
    """Direct 'set speed N' / 'speed N' (m/s): the holds' speed, or the landing / fly-to autopilot's speed target
    (en route: cruise speed; final / flare: flown too, with a caution - Luke's call). Nothing engaged + flying: the
    holds engage at the current altitude / heading with that speed. Cap / override flow and max-speed estimate apply."""
    if hold.active():
        return plane_hold(speed=float(speed))
    v = _vessel()
    if _heli_v(v) is not None:
        return heli_control("fly", speed=float(speed))
    if not _flying(v):
        return "Speed orders are for a plane in the air - take off first."
    if not plane.active():
        r = plane_hold(speed=float(speed))
        return r if r.startswith("needs_override") else "Nothing was engaged, so I engaged the holds: " + r
    return _plane_speed(v, float(speed))


_PHASES = {"to_entry": "en route", "final": "on final", "flare": "in the flare", "rollout": "rolling out",
           "takeoff": "taking off", "stall_test": "in the stall test", "recover": "recovering", "starting": "starting"}


def _phase_name(ph):
    return _PHASES.get(ph or "", ph or "busy")


def _plane_speed(v, speed):
    """Speed order while the landing / fly-to autopilot flies (plane.LIVE)."""
    alt = float(v.flight(v.orbit.body.reference_frame).mean_altitude)
    spd, note = maxspeed.clamp(v, speed, None)
    note = (note + " ") if note else ""
    need = speedcap.check("set_speed", {"speed": speed}, spd, alt, "speed")
    if need:
        return need.replace("needs_override: ", "needs_override: " + note, 1)
    ph = plane.STATUS.get("phase", "")
    plane.LIVE["vcruise"] = spd
    if ph in ("final", "flare", "rollout"):
        plane.LIVE["v_final"] = spd
        return note + (f"Flying {spd:.0f} m/s {_phase_name(ph)} as ordered. Caution: that's hot for a landing - "
                       "'go around' if it doesn't settle.")
    when = "" if ph == "to_entry" else f" once en route (now {_phase_name(ph)})"
    return note + f"Cruise speed {spd:.0f} m/s for the en-route leg{when}; the approach slows down as planned."


def plane_pitch(degrees: float = 5, direction: str = "up") -> str:
    v_ = _vessel()
    if _heli_v(v_) is not None:
        return ("Helicopter: the pitch attitude comes from the speed loop (nose down = faster, nose up = slow/stop) "
                "and the height from the collective - say 'speed 20', 'hover' or 'climb to 50'.")
    return _plane_pitch(degrees, direction)


plane_pitch.__doc__ = """PLANES: hold an ABSOLUTE pitch attitude with the running autopilot holds. "pitch up 10" -> degrees=10,
    direction="up" (hold 10 deg nose up); "pitch up 90" = vertical climb; "pitch down 5" / "nose down 5" ->
    direction="down". 0-90 deg, default 5. Near the stall margin it levels off with full power, rebuilds speed and
    returns to the pitch automatically, until the command changes or the target altitude / safe ceiling is reached.
    Needs the holds engaged (plane_hold); "set altitude ..." ends it. Not for helicopters (heli_control)."""


def _plane_pitch(degrees: float = 5, direction: str = "up") -> str:
    """PLANES: hold an ABSOLUTE pitch attitude with the running autopilot holds. "pitch up 10" -> degrees=10,
    direction="up" (hold 10 deg nose up); "pitch up 90" = vertical climb; "pitch down 5" / "nose down 5" ->
    direction="down". 0-90 deg, default 5. Near the stall margin it levels off with full power, rebuilds speed and
    returns to the pitch automatically, until the command changes or the target altitude / safe ceiling is reached.
    Needs the holds engaged (plane_hold); "set altitude ..." ends it."""
    if not hold.active():
        if plane.active():
            return ("The landing / fly-to autopilot is flying its own profile; pitch commands work with the autopilot "
                    "holds (engage them with plane_hold).")
        return "Nothing is engaged - pitch commands need the autopilot holds on (e.g. 'set altitude 3000' or AICS Engage)."
    try:
        deg = float(degrees)
    except (TypeError, ValueError):
        return "Pitch must be a number of degrees (0-90)."
    d = str(direction or "up").strip().lower()
    sign = -1.0 if d in ("down", "dn", "nose down", "-", "negative") else 1.0
    if deg < 0:
        sign, deg = -sign, -deg
    if deg > 90.0:
        return f"Pitch must be 0-90 degrees (you asked {deg:.0f})."
    p = sign * deg
    ceil = None
    if p > 0:
        try:
            est = tko.vessel_ceiling(_vessel())[0]
            ceil = speedcap.ceiling(est) if est else None
        except Exception:  # noqa: BLE001
            ceil = None
    hold.set_targets(pitch=p, pitch_t=time.time(), pitch_ceiling=ceil if ceil else "off", throttle="off")
    if p == 0:
        return "Pitch hold: 0 deg (level attitude). Say 'set altitude ...' to go back to altitude hold."
    if p < 0:
        return (f"Pitch hold: {deg:.0f} deg nose down, until the target altitude or the terrain floor (then it holds "
                "altitude); levels off if over the speed cap.")
    return (f"Pitch hold: {deg:.0f} deg nose up, throttle up. Near the stall margin I level off with full power, "
            "rebuild speed and return to it, until you change it or reach the target altitude"
            + (f" / safe ceiling (~{ceil:.0f} m)." if ceil else "."))


def autopilot_status() -> str:
    """Current autopilot state: holds (targets, altitude AGL/radar, signed heading error +right/-left) or the landing
    autopilot's phase, distance/ETA and heading error."""
    if hold.active():
        return hold.summary()
    st = plane.STATUS
    if plane.active():
        he = st.get("hdg_err")
        return (f"Plane autopilot {st.get('phase')}: runway {st.get('runway', '?')}, hdg {st.get('hdg')} -> "
                f"{st.get('hdg_des')} (err {he:+.0f}), h {st.get('h')} m above the runway, radar {st.get('hr')}, "
                f"{st.get('speed')} m/s, thr {st.get('throttle')}" if he is not None else f"Plane autopilot {st.get('phase')}")
    eta = spots.landing_eta(conn(), quiet=True)
    return eta.get("summary") or "No autopilot running."


def _ap(ap, on):
    """kRPC autopilot on/off across kRPC versions (engage()/disengage() or the engaged property)."""
    from . import krpcx
    krpcx.autopilot(ap, on)


def sun_lock(on: bool = True) -> str:
    """SPACE ONLY: point the craft's nose at the sun and hold it (for fixed solar panels facing forward). on=false releases."""
    c = conn()
    v = c.space_center.active_vessel
    ap = v.auto_pilot
    if not on:
        _ap(ap, False)
        v.control.sas = True
        return "Sun lock off, SAS on."
    if str(v.situation).split(".")[-1] not in ("orbiting", "sub_orbital", "escaping"):
        return "Sun lock is for space (orbit / sub-orbital) only."
    sun = c.space_center.bodies["Sun"]
    ref = v.orbit.body.non_rotating_reference_frame
    sp, vp = sun.position(ref), v.position(ref)
    d = (sp[0] - vp[0], sp[1] - vp[1], sp[2] - vp[2])
    ap.reference_frame = ref
    ap.target_direction = d
    v.control.sas = False
    _ap(ap, True)
    return "Sun lock: nose pointed at the sun and held (kRPC autopilot). Say 'sun lock off' to release."


def antenna_lock(on: bool = True) -> str:
    """SPACE: point the nose straight down at the body below (nadir) and hold it, so fixed dishes / antennas face the ground. on=false releases."""
    c = conn()
    v = c.space_center.active_vessel
    ap = v.auto_pilot
    if not on:
        _ap(ap, False)
        v.control.sas = True
        return "Antenna lock off, SAS on."
    if str(v.situation).split(".")[-1] not in ("orbiting", "sub_orbital", "escaping"):
        return "Antenna lock is for space only."
    ref = v.orbit.body.non_rotating_reference_frame
    p = v.position(ref)
    ap.reference_frame = ref
    ap.target_direction = (-p[0], -p[1], -p[2])
    v.control.sas = False
    _ap(ap, True)
    return "Antenna lock: nose pointed at the ground (nadir) and held. (Re-run it now and then: nadir drifts as you orbit.)"


def reset_experiments(discard_data: bool = False) -> str:
    """Reset science experiments so they can run again: inoperable ones (Goo, Materials Bay - needs a scientist on board)
    and, if discard_data=true, ones still holding data (e.g. after you transmitted a copy)."""
    v = _vessel()
    sci = any(getattr(k, "trait", "") == "Scientist" for k in v.crew)
    done, skipped = [], []
    for e in v.parts.experiments:
        t = e.part.title
        try:
            if e.inoperable:
                if not sci:
                    skipped.append(f"{t} (needs a scientist)")
                    continue
                e.reset()
                done.append(t)
            elif e.has_data and discard_data:
                e.reset()
                done.append(t)
        except Exception as ex:
            skipped.append(f"{t} ({ex.__class__.__name__})")
    return f"Reset {len(done)}: {', '.join(done) or 'none'}." + (f" Skipped: {', '.join(skipped)}." if skipped else "")


# ---------------------------------------------------------------- MechJeb maneuver planner (AICS Orbit Plan / Capture / Orbital AP)
def _mj_burn(what, setup):
    """Plan one MechJeb maneuver-planner node (setup(mj, v) -> operation) and let MJ's node executor fly it."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    if str(v.situation).split(".")[-1] in ("pre_launch", "landed", "splashed", "flying"):
        return f"{what} needs an orbit (situation: {str(v.situation).split('.')[-1]})."
    mj = _mj()
    for n in list(v.control.nodes):
        n.remove()
    op = setup(mj, v)
    if isinstance(op, str):
        return op
    try:
        nodes = op.make_nodes()
    except Exception as e:  # MJ OperationException: e.g. no target
        return f"MechJeb could not plan {what}: {e}"
    if not nodes:
        return f"MechJeb could not plan {what}: " + str(op.error_message)
    for extra in list(v.control.nodes)[1:]:
        extra.remove()
    n = v.control.nodes[0] if v.control.nodes else None
    mj.node_executor.autowarp = True
    _autostage_on(mj)
    mj.node_executor.execute_one_node()
    info = f" (dv {round(n.delta_v, 1)} m/s, burn in {round(n.time_to)} s)" if n else ""
    return f"{what}: node planned{info}; MechJeb node executor is flying it (autowarp on)."


def _hyperbolic(o):
    return o.eccentricity >= 1.0 or o.apoapsis_altitude != o.apoapsis_altitude or o.apoapsis_altitude == float("inf")


def change_apoapsis(altitude_km: float) -> str:
    """Change the apoapsis to altitude_km with a MechJeb node (burn at the next periapsis; on an escape/hyperbolic path 60 s from now) flown by the node executor. Orbit-changing: only when Luke asked."""
    def setup(mj, v):
        op = mj.maneuver_planner.operation_apoapsis
        op.new_apoapsis = float(altitude_km) * 1000.0
        ts, TR = op.time_selector, mj.TimeReference
        if _hyperbolic(v.orbit):
            ts.time_reference, ts.lead_time = TR.x_from_now, 60.0
        else:
            ts.time_reference = TR.periapsis
        return op
    return _mj_burn(f"Apoapsis -> {altitude_km:g} km", setup)


def change_periapsis(altitude_km: float) -> str:
    """Change the periapsis to altitude_km with a MechJeb node (at the next apoapsis; when arriving on an escape/hyperbolic path or for an aerocapture, 60 s from now) flown by the node executor. Orbit-changing: only when Luke asked."""
    def setup(mj, v):
        op = mj.maneuver_planner.operation_periapsis
        op.new_periapsis = float(altitude_km) * 1000.0
        ts, TR = op.time_selector, mj.TimeReference
        o = v.orbit
        if _hyperbolic(o) or o.time_to_apoapsis > o.time_to_periapsis * 3:
            ts.time_reference, ts.lead_time = TR.x_from_now, 60.0
        else:
            ts.time_reference = TR.apoapsis
        return op
    return _mj_burn(f"Periapsis -> {altitude_km:g} km", setup)


def change_inclination(inclination_deg: float) -> str:
    """Change the orbit inclination with a MechJeb node at the cheapest equatorial AN/DN, flown by the node executor."""
    def setup(mj, v):
        op = mj.maneuver_planner.operation_inclination
        op.new_inclination = float(inclination_deg)
        op.time_selector.time_reference = mj.TimeReference.eq_highest_ad
        return op
    return _mj_burn(f"Inclination -> {inclination_deg:g} deg", setup)


def match_target_plane() -> str:
    """Match orbital planes with the KSP target (vessel or moon) with a MechJeb node at the cheapest AN/DN."""
    def setup(mj, v):
        sc = conn().space_center
        if sc.target_vessel is None and sc.target_body is None:
            return "Set a KSP target (vessel or body) first."
        op = mj.maneuver_planner.operation_plane
        op.time_selector.time_reference = mj.TimeReference.rel_highest_ad
        return op
    return _mj_burn("Match target plane", setup)


def apsis_longitude(longitude_deg: float) -> str:
    """At the next apoapsis, burn so that one orbit later we reach apoapsis over surface longitude longitude_deg (MechJeb 'change surface longitude of apsis'). Used for synchronous/station-keeping orbits."""
    def setup(mj, v):
        op = mj.maneuver_planner.operation_longitude
        op.new_surface_longitude = float(longitude_deg)
        op.time_selector.time_reference = mj.TimeReference.apoapsis
        return op
    return _mj_burn(f"Apoapsis over longitude {longitude_deg:g}", setup)


def launch_to_target_plane(target_altitude_km: float = 80) -> str:
    """On the pad: MechJeb timed launch into the KSP target's orbital plane (sets inclination + launch time from the target's LAN), ascent to target_altitude_km with autostage. Needs a target (vessel or moon)."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    if not str(v.situation).endswith("pre_launch") and not str(v.situation).endswith("landed"):
        return "Launch to plane is for a craft on the pad."
    no_orbit = _orbit_capability_check(v)
    if no_orbit:
        return no_orbit
    sc = conn().space_center
    if sc.target_vessel is None and sc.target_body is None:
        return "Set a KSP target (the station / moon whose plane you want) first."
    a = _mj().ascent_autopilot
    a.desired_orbit_altitude = float(target_altitude_km) * 1000.0
    a.autostage = True
    a.skip_circularization = False
    try:
        a.launch_to_target_plane()
    except Exception as e:
        return f"MechJeb couldn't time the launch: {e}"
    v.control.sas = False
    a.enabled = True
    time.sleep(1.0)
    return (f"MechJeb timed launch into the target's plane: inclination {a.desired_inclination:.1f} deg, "
            f"{target_altitude_km:g} km, autostage on (MechJeb warps to the launch time). Status: {a.status}")


def sync_orbit_altitude() -> dict:
    """Synchronous (stationary) orbit altitude of the current body."""
    body = _vessel().orbit.body
    T, mu = body.rotational_period, body.gravitational_parameter
    a = (mu * T * T / (4 * math.pi ** 2)) ** (1 / 3)
    return {"body": body.name, "sync_altitude_km": round((a - body.equatorial_radius) / 1000.0, 2),
            "rotation_period_s": round(T), "fits_in_soi": a < body.sphere_of_influence}


def station_keep(longitude: float = 999) -> str:
    """Station-keep over a ground spot: synchronous equatorial orbit with its sub-satellite point at longitude (999 = the KSP target's longitude if one is set on the ground, else KSC). Runs as a Flight Plan with MechJeb: raise apoapsis to synchronous altitude, shift the apoapsis over the longitude, circularize there. Orbit-changing: only when Luke asked."""
    from . import flightplan
    v = _vessel()
    if not str(v.situation).endswith("orbiting"):
        return "Get into a stable orbit first."
    sync = sync_orbit_altitude()
    if not sync["fits_in_soi"]:
        return f"{sync['body']} rotates too slowly: a synchronous orbit would be outside its sphere of influence."
    lon, where = float(longitude), ""
    if lon > 360:
        sc = conn().space_center
        tv = sc.target_vessel
        lon, where = KSC_PAD[1], "KSC"
        if tv is not None and tv.orbit.body.name == v.orbit.body.name:
            try:
                lon, where = tv.flight(tv.orbit.body.reference_frame).longitude, tv.name
            except Exception:
                pass
    plan = (f"# Station-keep over {where or f'longitude {lon:.2f}'} (synchronous orbit {sync['sync_altitude_km']} km)\n"
            f"set ap {sync['sync_altitude_km']} km\napsis longitude {lon:.3f}\ncircularize\n")
    flightplan.push(plan, f"Station-keep over {where or round(lon, 2)}")
    r = flightplan.start(plan)
    inc = v.orbit.inclination * 180 / math.pi
    return r + (f" Note: inclination is {inc:.1f} deg - the spot will drift north/south daily unless you set inclination 0."
                if abs(inc) > 0.5 else "")


def capture_plan() -> str:
    """Arriving at a body: periapsis, time to periapsis, dV to capture (just bound / circular at periapsis) vs dV available, and whether an aerocapture is possible (atmosphere + suggested periapsis). Plans nothing."""
    v = _vessel()
    o = v.orbit
    body = o.body
    mu, r_pe, a = body.gravitational_parameter, o.periapsis, o.semi_major_axis
    v_pe = math.sqrt(max(0.0, mu * (2.0 / r_pe - 1.0 / a)))
    v_circ, v_esc = math.sqrt(mu / r_pe), math.sqrt(2 * mu / r_pe)
    have = _dv_numbers(v)["total_dv"]
    pe_km = o.periapsis_altitude / 1000.0
    out = (f"{body.name}: periapsis {pe_km:.1f} km in {_r(o.time_to_periapsis, 0)} s, speed there {v_pe:.0f} m/s "
           f"({'escape path' if _hyperbolic(o) else 'captured'}). Circular capture at Pe needs ~{max(0, v_pe - v_circ):.0f} m/s"
           f", just-bound ~{max(0, v_pe - 0.98 * v_esc):.0f} m/s; you have ~{have} m/s"
           f" (enough_fuel for circular: {'true' if have >= max(0, v_pe - v_circ) * 1.1 else 'false'},"
           f" for just-bound: {'true' if have >= max(0, v_pe - 0.98 * v_esc) * 1.1 else 'false'}).")
    if pe_km < 0:
        out += " WARNING: periapsis is below the surface - impact course."
    if body.has_atmosphere:
        atm = body.atmosphere_depth / 1000.0
        sug = AEROCAPTURE_PE_KM.get(body.name, round(atm * 0.5))
        out += (f" Atmosphere to {atm:.0f} km: aerocapture possible - suggested periapsis ~{sug} km (Set Pe), heat shield "
                "advised" + ("; your Pe is already inside the atmosphere." if 0 < pe_km < atm else "."))
    else:
        out += " No atmosphere: capture must be a burn (Capture = circularize at periapsis)."
    return out


AEROCAPTURE_PE_KM = {"Kerbin": 32, "Duna": 15, "Eve": 80, "Laythe": 30, "Jool": 140}


# ---------------------------------------------------------------- ground taxi (AICS Taxi / Base Run)
def taxi_to(name: str, speed: float = 8) -> str:
    """Drive along the ground to a waypoint (rover wheel motors, or a plane's engines + brakes): name = a taxi point ("Runway 09 start", "Runway 27 start", "KSC Pad", "Island 27 start"), a saved spot, or "lat,lon". Several waypoints: separate them with ';'. speed = cap in m/s (default 8, max 25). Straight lines between waypoints - no obstacle avoidance."""
    busy = guard.refuse_msg()
    if busy:
        return busy
    v = _vessel()
    sit = str(v.situation).split(".")[-1]
    if sit not in ("landed", "pre_launch"):
        return f"Taxi is for craft on the ground (situation: {sit})."
    if not v.parts.wheels:
        return "No wheels - nothing to taxi on."
    pts = []
    for nm in [x.strip() for x in str(name).split(";") if x.strip()]:
        p = taxi.resolve(nm)
        if p is None:
            return f"Unknown taxi point '{nm}'. Known: {', '.join(taxi.points())} (or 'lat,lon')."
        pts.append(p)
    if not pts:
        return "Name a taxi point."
    r = taxi.start(pts, max(2.0, min(25.0, float(speed or 8))))
    if r != "started":
        return r
    return f"Taxiing to {' -> '.join(p[0] for p in pts)} at up to {speed:g} m/s (straight lines). 'abort' stops."


def list_taxi_points() -> dict:
    """Ground taxi waypoints (name -> lat, lon)."""
    return {k: [round(a, 5), round(b, 5)] for k, (a, b) in taxi.points().items()}


def list_craft() -> dict:
    """List craft that can be launched from the VAB and SPH (Space Center scene)."""
    sc = conn().space_center
    return {"VAB": sc.launchable_vessels("VAB"), "SPH": sc.launchable_vessels("SPH")}


# launch-site areas (lat_min, lat_max, lon_min, lon_max): leftovers here are recovered before a launch so their
# crew is available again (otherwise KSP spawns the new craft uncrewed behind a modal "No Control" dialog)
_SITE_AREAS = [(-0.13, 0.0, -74.80, -74.40),      # KSC pad + runway
               (-1.60, -1.45, -72.00, -71.80)]    # Island Airfield
LAUNCH_SITES = {"vab": "LaunchPad", "sph": "Runway", "island": "Island_Airfield", "island_airfield": "Island_Airfield",
                "runway": "Runway", "launchpad": "LaunchPad", "pad": "LaunchPad"}


def _clear_launch_site(sc):
    """Recover whatever sits at the launch sites. Must happen from the Space Center: when kRPC launches over a
    crewed vessel from the flight scene, the crew are still 'assigned', the new craft comes out uncrewed and KSP
    shows a modal 'No Control' dialog that makes the launch call hang."""
    c = conn()
    if str(c.krpc.current_game_scene).endswith("flight"):
        sc.load_space_center()
        time.sleep(6)
    for v in sc.vessels:
        sit = str(v.situation).split(".")[-1]
        near = sit == "pre_launch"  # sitting on a launch site by definition
        if sit in ("landed", "splashed"):
            try:  # vessel.flight() isn't available in the Space Center scene; use the body position instead
                body = v.orbit.body
                if body.name == "Kerbin" and str(v.type).split(".")[-1] not in ("flag", "base", "station"):
                    pos = v.position(body.reference_frame)
                    lat, lon = body.latitude_at_position(pos, body.reference_frame), body.longitude_at_position(
                        pos, body.reference_frame)
                    near = any(a <= lat <= b and lo <= lon <= hi for a, b, lo, hi in _SITE_AREAS)
            except Exception:
                pass
        if near and v.recoverable:
            v.recover()
            time.sleep(10)  # launching while the recovery is still being processed hangs the kRPC call


def launch_craft(craft_name: str, editor: str = "VAB", site: str = "") -> str:
    """Launch a saved craft onto the LaunchPad (VAB) or Runway (SPH); site = "Island_Airfield" for the island strip. Recovers leftovers at the launch sites first so a crew is available. Only from the Space Center or flight scene."""
    sc = conn().space_center
    names = sc.launchable_vessels(editor)
    match = next((n for n in names if n.lower() == craft_name.lower()), None) or \
        next((n for n in names if craft_name.lower() in n.lower()), None)
    if not match:
        return f"No {editor} craft named '{craft_name}'. Available: {names}"
    site = LAUNCH_SITES.get(site.strip().lower(), site.strip()) if site else \
        ("LaunchPad" if editor.upper() == "VAB" else "Runway")
    _clear_launch_site(sc)
    sc.launch_vessel(editor.upper(), match, site, crew=[], recover=True)
    time.sleep(6)
    note = ""
    try:
        v = conn().space_center.active_vessel
        if v.name.startswith("#"):  # stock craft carry a localization tag as name
            v.name = match
        if v.crew_count == 0 and not any(p.name.lower().startswith("probe") for p in v.parts.all):
            note = (" WARNING: it spawned without crew (no free kerbal?) - recover old vessels or hire crew in the "
                    "Astronaut Complex.")
    except Exception:
        pass
    return f"Launched '{match}' to the {site}. Now in flight scene." + note


def recover_vessel() -> str:
    """Recover the active vessel (only when landed, splashed or on the pad)."""
    v = _vessel()
    if not v.recoverable:
        return f"Vessel can't be recovered now (situation {str(v.situation).split('.')[-1]})."
    name = v.name
    v.recover()
    return f"Recovered {name}."


def set_science_watcher(mode: str) -> str:
    """Science watcher mode: 'auto' = auto-run and transmit all new experiments when entering a new situation/biome/SOI ('science auto on'); 'remind' = remind in chat and auto-run only rerunnable ones (default, 'science auto off'); 'off' = disabled."""
    return science.set_mode(mode)


def set_flight_plan(plan: str) -> str:
    """Put a step-by-step flight plan into the AICS Flight Plan editor so Luke can edit it and press Fly there. Does NOT fly it. One step per line, e.g. "takeoff / climb 3000 m agl vs 50 / cruise hdg 090 speed 180 for 3 min / circle 3 laps right bank 20 around field / land" (planes) or "ascent 80 km inc 0 / circularize / transfer Mun" (rockets). Use whenever Luke asks for a flight plan."""
    from . import flightplan
    n = flightplan.push(plan.replace(" / ", "\n"), "From the chat AI. Check it, then Fly.")
    if not n:
        return ("No usable steps in that plan. Use one step per line: takeoff, climb 3000 m agl vs 50, cruise hdg 090 "
                "speed 180 for 3 min, circle 3 laps around field, land, ascent 80 km, circularize, transfer Mun.")
    return f"Put {n} steps into AICS > Flight Plan. Luke can edit them there and press Fly."


def set_ai_name(name: str) -> str:
    """Set the assistant's name when Luke gives it one (e.g. 'I'll call you Bob'). Overwrites any earlier name; shown in the in-game chat."""
    name = " ".join(str(name).split())[:24].strip(" .!\"'")
    if not name:
        return "Name can't be empty."
    settings.put("ai_name", name)
    return f"Got it - my name is now {name}."


def remember_preference(note: str) -> str:
    """Save a lasting note about how Luke likes to play (e.g. preferred orbit altitude, wants crossfeed kept on). Call this whenever he states a preference."""
    return memory.remember(note)



# ---------------- captain's orders (orders.py parses them; most are menu-only, the model uses captain_order)
def _clampf(x, lo, hi):
    return max(lo, min(hi, x))


def _need_hold(what):
    return f"Nothing is engaged - {what} needs the autopilot holds on (e.g. 'set altitude 3000' or AICS Engage)."


def _flying(v):
    return str(v.situation).split(".")[-1] == "flying"


def _ksc_mid():
    return (plane.RWY_W[0] + plane.RWY_E[0]) / 2, (plane.RWY_W[1] + plane.RWY_E[1]) / 2


_KSC_NAMES = ("ksc", "the ksc", "base", "home", "kerbal space center", "space center", "runway", "the runway")
_ISLAND_NAMES = ("island", "the island", "island airfield", "island runway")


def _dist_brg(v, lat, lon):
    body = v.orbit.body
    f = v.flight(body.reference_frame)
    return (spots.gc_dist(f.latitude, f.longitude, lat, lon, body.equatorial_radius),
            spots.bearing(f.latitude, f.longitude, lat, lon))


def _fuel_text(v):
    parts = []
    for name, short in (("LiquidFuel", "LF"), ("Oxidizer", "Ox"), ("MonoPropellant", "Mono"), ("SolidFuel", "Solid")):
        try:
            p = _res_pct(v.resources, name)
        except Exception:  # noqa: BLE001
            p = None
        if p is not None:
            parts.append(f"{short} {p:.0f}%")
    return ", ".join(parts) or "no fuel tanks"


def turn(direction: str = "left", degrees: float = 90) -> str:
    """Turn left/right by N degrees from the current heading (autopilot holds; bank limits 20 / 15 deg apply)."""
    from . import heli
    if heli.active():
        v = _vessel()
        sign = -1.0 if str(direction).strip().lower().startswith("l") else 1.0
        if heli.STATE["mode"] in ("fly", "goto") and heli.STATE.get("track") is not None:  # moving: turn the track
            new = (heli.STATE["track"] + sign * min(abs(float(degrees)), 175.0)) % 360.0
            return heli_control("fly", heading=new)
        hdg = float(v.flight(v.surface_reference_frame).heading)  # hovering: pedal turn
        new = (hdg + sign * min(abs(float(degrees)), 175.0)) % 360.0
        return heli_control("face", heading=new)
    if not hold.active():
        return _need_hold("a turn")
    v = _vessel()
    hdg = float(v.flight(v.surface_reference_frame).heading)
    deg, note = abs(float(degrees)), ""
    if deg > 175.0:
        deg, note = 175.0, " (capped at 175 so it turns the way you said)"
    sign = -1.0 if str(direction).strip().lower().startswith("l") else 1.0
    new = (hdg + sign * deg) % 360.0
    plane_hold(heading=new)
    return f"Turning {'left' if sign < 0 else 'right'} {deg:.0f} deg: heading {new:03.0f}{note}."


def set_heading(heading: float) -> str:
    """Direct 'heading 270' for the running autopilot holds."""
    from . import heli
    if heli.active():  # moving: the track; hovering: the nose
        if heli.STATE["mode"] in ("fly", "goto"):
            return heli_control("fly", heading=float(heading))
        return heli_control("face", heading=float(heading))
    if not hold.active():
        return _need_hold("a heading")
    return plane_hold(heading=float(heading) % 360.0)


def fly_to_place(name: str) -> str:
    """'fly to KSC' / 'fly to Island' / 'fly to <spot or waypoint>' / 'return to base'. Runways: fly_to (lands there;
    KSC picks runway 09 or 27 from where we are). Other places: the holds fly the bearing (no landing)."""
    v = _vessel()
    n = " ".join(str(name).lower().split())
    if n in _KSC_NAMES:
        brg = _dist_brg(v, *_ksc_mid())[1]
        name = "Runway 09" if brg < 180.0 else "Runway 27"  # coming from the west -> land eastbound (09)
    elif n in _ISLAND_NAMES:
        name = "Island Airfield"
    label, spot = spots.resolve(conn().space_center, name)
    if label is None:
        return spot
    if _heli_v(v) is not None:
        if not _flying(v):
            return "Take off first ('take off' = vertical liftoff to a hover)."
        from . import heli
        lat, lon = spots.spot_point(spot)
        d, brg = _dist_brg(v, lat, lon)
        heli.command("goto", goto=(lat, lon), speed=heli.STATE.get("speed") or 25.0,
                     alt=heli.STATE.get("alt") or max(heli.HOVER_AGL, float(v.flight().surface_altitude)))
        return f"Heli flying to {label} ({d / 1000:.1f} km, bearing {brg:03.0f}); it'll hover there and hold position."
    if spot.get("mode") == "H":
        if not _handoff_from_hold():  # the holds hand the craft to the fly_to autopilot
            return "The holds didn't release - try again."
        return fly_to(label)
    if not _flying(v):
        return f"'{label}' isn't a runway; I can only fly toward it in the air (no landing there)."
    lat, lon = spots.spot_point(spot)
    d, brg = _dist_brg(v, lat, lon)
    r = plane_hold(heading=brg)
    return (f"Heading {brg:03.0f} toward {label} ({d / 1000:.0f} km) - a straight heading, not a landing; say it again "
            "to correct drift. " + r)


def circle_here(direction: str = "left", bank: float = 15) -> str:
    """Circle where we are (a bank hold at the current altitude): left/right, bank 5-20 deg (15 above 250 m/s)."""
    v = _vessel()
    if not _flying(v):
        return "Circling is for a plane in the air."
    sign = -1.0 if str(direction).strip().lower().startswith("l") else 1.0
    spd = float(v.flight(v.orbit.body.reference_frame).speed)
    b = _clampf(abs(float(bank)), 5.0, speedcap.bank_limit(spd))
    r = plane_hold(roll=sign * b)
    return f"Circling {'left' if sign < 0 else 'right'} at {b:.0f} deg bank (say 'level off' to stop). " + r


def flight_report() -> str:
    """Short flight report: altitude, speed, heading, fuel %, distance to KSC, autopilot."""
    v = _vessel()
    body = v.orbit.body
    f = v.flight(body.reference_frame)
    hdg = float(v.flight(v.surface_reference_frame).heading)
    out = (f"Alt {f.mean_altitude:.0f} m ({f.surface_altitude:.0f} m above ground), {f.speed:.0f} m/s, V/S "
           f"{f.vertical_speed:+.0f}, heading {hdg:03.0f}, fuel {_fuel_text(v)}")
    if body.name == "Kerbin":
        d, brg = _dist_brg(v, *_ksc_mid())
        out += f", KSC {d / 1000:.1f} km bearing {brg:03.0f}"
    if hold.active():
        out += ". Autopilot: holds"
    elif plane.active():
        out += f". Autopilot: {plane.STATUS.get('phase')}"
    return out + "."


def fuel_check() -> str:
    """Fuel % plus a rough endurance / range at the current burn rate, and whether that reaches KSC."""
    v = _vessel()
    f = v.flight(v.orbit.body.reference_frame)
    flow = 0.0  # kg/s at the current thrust
    for e in v.parts.engines:
        try:
            isp = float(e.specific_impulse)
            if e.active and isp > 0:
                flow += float(e.thrust) / (isp * 9.81)
        except Exception:  # noqa: BLE001
            pass
    r = v.resources
    fuel_kg = 5.0 * (r.amount("LiquidFuel") + r.amount("Oxidizer"))
    out = f"Fuel {_fuel_text(v)}"
    if flow > 1e-4 and fuel_kg > 0:
        t = fuel_kg / flow
        rng = t * float(f.speed)
        out += f"; at this burn rate ~{t / 60:.0f} min, ~{rng / 1000:.0f} km at {f.speed:.0f} m/s (estimate)"
        if v.orbit.body.name == "Kerbin":
            d = _dist_brg(v, *_ksc_mid())[0]
            ok = rng > 1.3 * d
            out += (f"; KSC is {d / 1000:.0f} km - " + ("enough" if ok else "NOT enough with a 30% reserve")
                    + f" (enough_fuel: {'true' if ok else 'false'})")
    else:
        out += "; engines idle, no burn-rate estimate"
    return out + "."


def how_far(name: str = "KSC") -> str:
    """Distance, bearing and ETA (at the current ground speed) to KSC or a named spot / waypoint."""
    v = _vessel()
    n = " ".join(str(name).lower().split())
    if n in _KSC_NAMES:
        label, (lat, lon) = "KSC", _ksc_mid()
    else:
        label, spot = spots.resolve(conn().space_center, "Island Airfield" if n in _ISLAND_NAMES else name)
        if label is None:
            return spot
        lat, lon = spots.spot_point(spot)
    d, brg = _dist_brg(v, lat, lon)
    gs = float(v.flight(v.orbit.body.reference_frame).horizontal_speed)
    eta = f", ~{d / gs / 60:.0f} min at {gs:.0f} m/s" if gs > 20 else ""
    return f"{label}: {d / 1000:.1f} km, bearing {brg:03.0f}{eta}."


def time_to_target() -> str:
    """ETA to touchdown (running landing) or to the KSP target vessel."""
    eta = spots.landing_eta(conn(), quiet=True)
    if eta.get("active") and eta.get("summary"):
        return eta["summary"]
    sc = conn().space_center
    try:
        tv = sc.target_vessel
    except Exception:  # noqa: BLE001
        tv = None
    if tv is None:
        return "No landing in progress and no KSP target set."
    v = sc.active_vessel
    d = math.sqrt(sum(c * c for c in tv.position(v.reference_frame)))
    gs = float(v.flight(v.orbit.body.reference_frame).speed)
    return f"{tv.name}: {d / 1000:.1f} km" + (f", ~{d / gs / 60:.0f} min at {gs:.0f} m/s." if gs > 5 else ".")


def level_off() -> str:
    """Level off: cancel pitch / turn / circle, hold the current altitude (MSL) and heading, wings level."""
    v = _vessel()
    if not _flying(v):
        return "Level off is for a plane in the air."
    alt = float(v.flight(v.orbit.body.reference_frame).mean_altitude)
    hdg = float(v.flight(v.surface_reference_frame).heading)
    r = plane_hold(altitude_m=round(alt), altitude_ref="msl", heading=hdg, off="roll,pitch")
    return f"Leveling off at {alt:.0f} m, heading {hdg:03.0f}, wings level. " + r


def set_rcs(on: bool = True) -> str:
    """RCS on / off."""
    _own_change()
    _vessel().control.rcs = bool(on)
    return f"RCS {'on' if on else 'off'}."


def set_lights(on: bool = True) -> str:
    """Lights action group on / off."""
    _own_change()
    _vessel().control.lights = bool(on)
    return f"Lights {'on' if on else 'off'}."


def action_group(group: int, state: str = "toggle") -> str:
    """Custom action group 1-10: state 'toggle' (default), 'on' or 'off'."""
    g = int(group)
    if not 1 <= g <= 10:
        return "Action groups are 1-10."
    k = g % 10  # kRPC: 0 = AG 10
    _own_change()
    ctl = _vessel().control
    st = str(state or "toggle").lower()
    if st in ("on", "off"):
        ctl.set_action_group(k, st == "on")
        return f"Action group {g} {st}."
    ctl.toggle_action_group(k)
    return f"Action group {g} toggled."


def set_override(kind: str = "all", value: str = "off", authority: str = "ask", confirmed: bool = False) -> str:
    """Raise (or restore) a flight limit for the rest of the session: kind bank (deg) / pitch (climb deg) / speed
    (m/s EAS cap) / altitude (m ceiling); value N or 'off' (kind 'all' + 'off' = every default back). authority 'ask'
    (default, '*' in a plan) asks Luke yes/no first; 'pre' ('***') applies directly. The max-speed estimate still
    clamps speeds."""
    k = {"alt": "altitude", "spd": "speed", "ceiling": "altitude", "": "all"}.get(str(kind).strip().lower(), str(kind).strip().lower())
    if k not in ("bank", "pitch", "speed", "altitude", "all"):
        return "Override kinds: bank, pitch, speed, altitude (or 'override off')."
    if str(value).strip().lower() in ("off", "default", "reset", "none", ""):
        return speedcap.clear_override(k)
    if k == "all":
        return "Say which limit: override bank|pitch|speed|altitude N."
    if k == "speed" and str(value).strip().lower() in ("max", "full", "maximum"):
        return _override_speed_max(authority, confirmed)
    try:
        v = float(value)
    except (TypeError, ValueError):
        return f"Override {k} needs a number."
    lo, hi = speedcap.OVERRIDE_RANGE[k]
    if not lo <= v <= hi:
        return f"Override {k} must be {lo:.0f}-{hi:.0f}."
    if not confirmed and str(authority).strip().lower() not in ("pre", "***") and not speedcap.all_authorised():
        return speedcap.confirm("set_override", {"kind": k, "value": v, "confirmed": True},
                                f"Override the {speedcap._override_desc(k, v)} for the rest of the session? (yes/no)")
    msg = speedcap.apply_override(k, v)  # always stored, whatever is (or isn't) flying
    speedcap._marks[k] = _auth_mark(authority)
    try:
        holds, ap = hold.active(), plane.active()
    except Exception:  # noqa: BLE001
        holds = ap = False
    if not (holds or ap):
        return msg + " Nothing is engaged - it applies to whatever autopilot engages next."
    if k == "speed":  # 'override speed to 325': fly it too (the max-speed estimate still clamps)
        try:
            return msg + " " + set_speed(v)
        except Exception as e:  # noqa: BLE001
            return msg + f" (Couldn't set the speed target: {e.__class__.__name__}.)"
    if k == "bank" and ap and plane.STATUS.get("phase") in ("final", "flare"):
        return msg + f" Caution: applied {_phase_name(plane.STATUS.get('phase'))} too."
    return msg


def _auth_mark(authority):
    """Authority marker for STATUS: '***' pre-authorized, 'all' under authorise all, '*' asked and granted."""
    if str(authority).strip().lower() in ("pre", "***"):
        return "***"
    return "all" if speedcap.all_authorised() else "*"


def _override_speed_max(authority, confirmed):
    """'override speed max': no speed cap and full throttle in whatever flies (the physical estimate is reported)."""
    if not confirmed and str(authority).strip().lower() not in ("pre", "***") and not speedcap.all_authorised():
        return speedcap.confirm("set_override", {"kind": "speed", "value": "max", "confirmed": True},
                                "Override the speed cap completely - no cap, full throttle? (yes/no)")
    msg = speedcap.apply_override("speed", "max")
    speedcap._marks["speed"] = _auth_mark(authority)
    try:
        est = maxspeed.estimate(_vessel())
    except Exception:  # noqa: BLE001
        est = None
    note = f" Best estimate max here is ~{est:.0f} m/s." if est else ""
    if hold.active():
        hold.set_targets(throttle=1.0)
        return msg + " Full throttle on the holds (manual; 'throttle auto' hands it back)." + note
    if plane.active():
        plane.LIVE["vcruise"] = 5000.0
        ph = plane.STATUS.get("phase", "")
        if ph in ("final", "flare", "rollout"):
            plane.LIVE["v_final"] = 5000.0
            return msg + f" Full throttle {_phase_name(ph)} as ordered. Caution: 'go around' if it's too hot." + note
        return msg + " Full throttle on the en-route leg; the approach still slows down." + note
    return msg + " Nothing is engaged - it applies to whatever engages next." + note


def authorise_all() -> str:
    """'authorise all' / 'authorize all': grant every override (and speed / ceiling request) without asking."""
    return speedcap.authorise_all()


def captain_order(order: str) -> str:
    """Run a short captain's order exactly like the chat shortcut (pass Luke's words): 'turn left 30', 'heading 270',
    'fly to KSC' / 'fly to Island', 'return to base', 'circle here', 'report', 'fuel check', 'how far to KSC',
    'time to target', 'level off' / 'wings level', 'stage', 'SAS on', 'hold prograde', 'RCS off', 'lights on', 'AG 3',
    'gear down', 'airbrake on', 'throttle 60', 'set speed 200', 'pitch up 10', 'deploy chutes', 'engines on',
    'cut engines', 'switch engine mode', 'afterburner on', 'flaps 1' / 'flaps up', 'trim nose up 5', 'land',
    'land at KSC', 'go around', 'touch and go', 'abort', 'crew report', 'override bank 40 *', 'override off',
    'descend to 5 km', 'climb to 8000', 'altitude 3000 agl'. Destructive ones answer 'needs_confirm: ...'."""
    from . import orders
    p = orders.parse(order)
    if not p:
        return f"Not an order I know: '{order}'. Use the specific tool instead."
    name, args = p
    if name == "eject_kerbal":
        args = {}  # the model never skips Luke's yes/no (only his own one-step 'eject confirm' in chat does)
    f = BY_NAME.get(name)
    return f(**args) if f else f"Unknown order tool {name}."



# ---------------- captain's orders, batch 2 (engines / flaps / trim / landing / abort / crew)
FLAPS_DEFAULT = {"1": 5, "2": 6}  # flaps 1 = AG 5 on, flaps 2 = AG 5 + AG 6 on, flaps up = both off


def _handoff_from_hold(timeout=15.0):
    """Release the autopilot holds (if flying) so a landing / fly_to can take the craft; True when free."""
    if not hold.active():
        return True
    hold.set_targets(engaged=False)
    hold.stop()
    t0 = time.time()
    while hold.active() and time.time() - t0 < timeout:
        time.sleep(0.2)
    return not hold.active()


def _modal_engines(v):
    out = []
    for e in v.parts.engines:
        try:
            if e.has_modes:
                out.append(e)
        except Exception:  # noqa: BLE001
            pass
    return out


def _own_change():
    """Engine / mode changes by our own tools are not sabotage (emergency watcher)."""
    try:
        from . import emergency
        emergency.own_change()
    except Exception:  # noqa: BLE001
        pass


def set_engines(on: bool = True) -> str:
    """Engines on (activate all engines) / off (= cut engines, asks to confirm first)."""
    if not on:
        return cut_engines()
    _own_change()
    n = 0
    for e in _vessel().parts.engines:
        try:
            if not e.active:
                e.active = True
                n += 1
        except Exception:  # noqa: BLE001
            pass
    return f"Engines on ({n} activated)." if n else "All engines were already on."


def cut_engines(confirmed: bool = False) -> str:
    """Cut all engines: autopilots off, throttle 0, every engine shut down. Asks Luke to confirm first."""
    v = _vessel()
    if not confirmed:
        glider = " We're flying - that makes us a glider." if _flying(v) else ""
        return speedcap.confirm("cut_engines", {"confirmed": True},
                                f"Cut all engines (throttle 0, every engine shut down, autopilots off)?{glider} (yes/no)")
    _own_change()
    if hold.active():
        hold.set_targets(engaged=False)
        hold.stop()
    if plane.active():
        plane.stop()
    v.control.throttle = 0.0
    n = 0
    for e in v.parts.engines:
        try:
            if e.active:
                e.active = False
                n += 1
        except Exception:  # noqa: BLE001
            pass
    return f"Engines cut: throttle 0, {n} engine(s) shut down, autopilots off."


def engine_mode() -> str:
    """Switch the mode of multi-mode engines (e.g. RAPIER air-breathing <-> closed cycle)."""
    eng = _modal_engines(_vessel())
    if not eng:
        return "No multi-mode engines (e.g. RAPIER) on this craft."
    _own_change()
    modes = []
    for e in eng:
        e.toggle_mode()
        try:
            modes.append(str(e.mode))
        except Exception:  # noqa: BLE001
            pass
    return f"Engine mode switched on {len(eng)} engine(s)" + (f": now {', '.join(sorted(set(modes)))}." if modes else ".")


def afterburner(on: bool = True) -> str:
    """Afterburner on/off: engines with Wet/Dry modes (Panther-style) go to Wet (on) or Dry (off)."""
    want, n, found = ("wet" if on else "dry"), 0, 0
    _own_change()
    for e in _modal_engines(_vessel()):
        try:
            names = list(e.modes.keys())
        except Exception:  # noqa: BLE001
            continue
        tgt = next((m for m in names if want in m.lower()), None)
        if tgt is None:
            continue
        found += 1
        if str(e.mode).lower() != tgt.lower():
            try:
                e.mode = tgt
            except Exception:  # noqa: BLE001
                e.toggle_mode()
            n += 1
    if not found:
        return "No afterburning engines (Wet/Dry modes, like the Panther) on this craft."
    return f"Afterburner {'on' if on else 'off'} ({found} engine(s){'' if n else ', already there'})."


def flaps(setting: str = "1") -> str:
    """Flaps 1 / 2 / up through action groups (default: flaps 1 = AG 5, flaps 2 = AG 5 + AG 6, up = both off;
    change with bridge_settings.json "flaps_action_groups": {"1": 5, "2": 6})."""
    ags = dict(FLAPS_DEFAULT, **{str(k): int(v) for k, v in (settings.get("flaps_action_groups") or {}).items()})
    s = str(setting).strip().lower()
    s = {"one": "1", "down": "1", "two": "2", "full": "2", "0": "up", "off": "up", "retract": "up", "retracted": "up"}.get(s, s)
    if s not in ("1", "2", "up"):
        return "Flaps settings: 1, 2 or up."
    ctl = _vessel().control
    g1, g2 = ags["1"] % 10, ags["2"] % 10
    ctl.set_action_group(g1, s in ("1", "2"))
    ctl.set_action_group(g2, s == "2")
    return f"Flaps {s} (AG {ags['1']} {'on' if s != 'up' else 'off'}, AG {ags['2']} {'on' if s == '2' else 'off'})."


def trim(direction: str = "up", percent: float = 5, axis: str = "pitch") -> str:
    """Pitch trim nose up/down by N % of the trim range (default 5); direction 'reset' = neutral. axis=pitch|roll|yaw."""
    ctl = _vessel().control
    d = str(direction).strip().lower()
    ax = {"p": "pitch", "r": "roll", "y": "yaw"}.get(str(axis).strip().lower()[:1], str(axis).strip().lower())
    if ax not in ("pitch", "roll", "yaw"):
        ax = "pitch"
    attr = f"{ax}_trim"
    try:
        cur = float(getattr(ctl, attr))
    except AttributeError:
        if ax == "pitch":
            return "This kRPC version has no trim control (pitch_trim needs a newer kRPC)."
        return f"This kRPC version has no {ax} trim ({attr})."
    new = 0.0 if d in ("reset", "zero", "neutral", "0", "off") else \
        _clampf(cur + (1.0 if d.startswith("u") else -1.0) * abs(float(percent)) / 100.0, -1.0, 1.0)
    if d in ("reset", "zero", "neutral", "0", "off") and ax == "pitch":
        from . import trim_auto
        n = trim_auto.restore_surface_snapshot(_vessel())
        trim_auto.reset_vessel_context()
        for other in ("pitch_trim", "roll_trim", "yaw_trim"):
            try:
                setattr(ctl, other, 0.0)
            except AttributeError:
                pass
        new = 0.0
    try:
        setattr(ctl, attr, new)
    except AttributeError:
        return f"This kRPC version has no {ax} trim ({attr})."
    note = " (the autopilot flies pitch itself, so trim mostly offsets its input)" if hold.active() or plane.active() else ""
    label = {"pitch": "Pitch", "roll": "Roll", "yaw": "Yaw"}[ax]
    extra = ""
    if d in ("reset", "zero", "neutral", "0", "off") and ax == "pitch":
        try:
            extra = f" Restored {n} control surface(s) to pre-trim snapshot." if n else " Surfaces unchanged (no snapshot)."
        except NameError:
            extra = ""
    return f"{label} trim {100 * new:+.0f}%{note if ax == 'pitch' else ''}.{extra}"


def set_trim(axis: str = "pitch", value: float = 0.0) -> str:
    """AICS Trim panel: axis pitch|roll|yaw|collective; value -1..1 (or collective blade degrees if |value|>1).
    Auto-trim toggles: auto_master|auto_pitch|auto_roll|auto_yaw|auto_rotor with value 0 or 1."""
    from . import heli, propulsion, trim_auto
    ax = str(axis or "pitch").strip().lower()
    val = float(value)
    if ax.startswith("auto_"):
        key = ax[5:]
        if key not in trim_auto.auto_trim_settings():
            return f"Unknown auto-trim axis '{axis}' (use auto_master, auto_pitch, auto_roll, auto_yaw, auto_rotor)."
        trim_auto.set_auto_trim(key, val >= 0.5)
        on = trim_auto.auto_trim_settings()[key]
        label = {"master": "Auto-trim master", "pitch": "Pitch auto-trim", "roll": "Roll auto-trim",
                 "yaw": "Yaw auto-trim", "rotor": "Rotor auto-trim"}.get(key, key)
        return f"{label} {'ON' if on else 'OFF'}."
    v = _vessel()
    ctl = v.control
    if ax == "collective":
        if not heli.is_heli(v):
            return "Collective trim only applies to rotorcraft."
        deg = val * 12.0 if abs(val) <= 1.0 else val
        deg = _clampf(deg, 0.0, 12.0)
        flying = str(v.situation).split(".")[-1] == "flying"
        return propulsion.set_blades(v, pitch=deg, flying=flying)
    if ax not in ("pitch", "roll", "yaw"):
        return f"Unknown trim axis '{axis}' (use pitch, roll, yaw, or collective)."
    attr = f"{ax}_trim"
    try:
        setattr(ctl, attr, _clampf(val, -1.0, 1.0))
    except AttributeError:
        return f"This kRPC build has no {attr}."
    note = " (autopilot flies pitch)" if ax == "pitch" and (hold.active() or plane.active()) else ""
    return f"{ax.title()} trim {100 * float(getattr(ctl, attr)):+.0f}%{note}."


def get_trim_state() -> dict:
    """Trim UI poll JSON: {pitch, roll, yaw, collective?, heli, craft, notes_saved}."""
    from . import craft_notes, heli, trim_auto
    v = _vessel()
    snap = trim_auto.snapshot_state(v)
    heli_craft = False
    collective = None
    try:
        heli_craft = heli.is_heli(v)
    except Exception:  # noqa: BLE001
        pass
    if heli_craft:
        try:
            from . import propulsion
            ps = propulsion.prop_status(v)
            if ps.get("pitch") is not None:
                collective = float(ps["pitch"])
        except Exception:  # noqa: BLE001
            pass
    name = craft_notes.vessel_name(v)
    out = {
        "pitch": float(snap.get("pitch_trim") or 0.0),
        "roll": float(snap.get("roll_trim") or 0.0),
        "yaw": float(snap.get("yaw_trim") or 0.0),
        "heli": bool(heli_craft),
        "craft": name,
        "notes_saved": bool(craft_notes.load_for_vessel(v)),
    }
    if collective is not None:
        out["collective"] = collective
    at = trim_auto.auto_trim_settings()
    for k, v in at.items():
        out[f"auto_{k}"] = bool(v)
    return out


def trim_panel_open() -> str:
    """Bare 'trim' chat order: queue !cmd trim_show for the in-game Trim window."""
    from . import science
    science.post_command("trim_show")
    return "Opening the Trim panel."


def auto_trim_now() -> str:
    """One auto-trim step in level flight (elevator deploy + pitch trim toward neutral input)."""
    from . import trim_auto
    return trim_auto.auto_trim_now()


def save_craft_notes() -> str:
    """Save current trim and cruise settings to craft_notes.json for this vessel."""
    from . import trim_auto
    return trim_auto.save_to_craft_notes()


def land(where: str = "") -> str:
    """'land' / 'land at KSC' / 'land at Island': planes autoland (KSC: land_plane, runway from the approach direction;
    elsewhere fly_to); rockets/landers: 'land' = land_here, 'land at KSC' = land_at_ksc."""
    v = _vessel()
    n = " ".join(str(where or "").lower().split())
    if _heli_v(v) is not None and not n:
        return heli_control("land")
    is_plane = bool(v.parts.wheels) and _has_wings(v)
    if not is_plane:
        return land_at_ksc() if n in _KSC_NAMES else land_here()
    if n and n not in _KSC_NAMES:
        return fly_to_place(where)
    if plane.active():
        return "A plane landing is already in progress."
    if not _handoff_from_hold():
        return "The holds didn't release - try again."
    return land_plane()


def go_around() -> str:
    """Go around: drop the running approach, climb straight ahead to 600 m above the field with the holds."""
    if not plane.active():
        return "No landing in progress to go around from."
    hdg = plane.STATUS.get("runway_heading")
    plane.stop()
    t0 = time.time()
    while plane.active() and time.time() - t0 < 15.0:
        time.sleep(0.2)
    v = _vessel()
    if hdg is None:
        hdg = float(v.flight(v.surface_reference_frame).heading)
    r = plane_hold(altitude_m=600, altitude_ref="agl", heading=float(hdg) % 360.0)
    return (f"Going around: approach dropped, climbing to 600 m above the field on heading {float(hdg) % 360:03.0f} "
            "(throttle chases the climb). Say 'land' for another approach. " + r)


def touch_and_go() -> str:
    """One touch-and-go at KSC, then a full-stop landing (land_plane touch_and_go=1)."""
    if plane.active():
        return "A landing is already running - say 'go around' first, then 'touch and go'."
    if not _handoff_from_hold():
        return "The holds didn't release - try again."
    return land_plane(touch_and_go=1)


def abort_ag(confirmed: bool = False) -> str:
    """Fire the ABORT action group (and stop the bridge autopilots). Asks Luke to confirm first."""
    if not confirmed:
        return speedcap.confirm("abort_ag", {"confirmed": True},
                                "Fire the ABORT action group (and stop all bridge autopilots)? (yes/no)")
    r = abort()
    _vessel().control.abort = True
    return "ABORT action group fired. " + r


def crew_report() -> str:
    """Who's aboard: name, trait, courage / stupidity."""
    try:
        crew = list(_vessel().crew)
    except Exception:  # noqa: BLE001
        crew = []
    if not crew:
        return "No crew aboard (probe-controlled)."
    out = []
    for k in crew:
        bits = []
        for attr, label in (("trait", ""), ("courage", "courage "), ("stupidity", "stupidity ")):
            try:
                val = getattr(k, attr)
                bits.append(f"{label}{100 * float(val):.0f}%" if label else str(val))
            except Exception:  # noqa: BLE001
                pass
        out.append(f"{k.name}" + (f" ({', '.join(bits)})" if bits else ""))
    return f"Crew ({len(crew)}): " + "; ".join(out) + "."


TOOLS = [heli_control, get_status, stage, set_throttle, set_gear, set_brakes, eject_kerbal, set_sas, set_sas_mode, run_science, mechjeb_ascent, circularize,
         warp_to_apoapsis, transfer_to, course_correction, warp_to_soi_change, get_delta_v, deorbit_burn, land_at, land_at_ksc, land_here, land_plane, land_at_spot, fly_to, get_landing_eta, list_landing_spots, save_landing_spot, dock_with, deploy_parachutes, abort, list_craft, launch_craft, recover_vessel, set_science_watcher, set_ai_name, remember_preference,
         plane_hold, plane_pitch, takeoff, damage_report, prop_control, captain_order, autopilot_status, sun_lock, antenna_lock, reset_experiments, set_flight_plan,
         change_apoapsis, change_periapsis, change_inclination, station_keep, capture_plan, taxi_to]
# AICS-menu-only tools (POST /tool, Flight Plan steps): kept out of the model's tool list so small local models
# aren't swamped by schemas
MENU_ONLY = [stop_current, set_speed, set_altitude, turn, set_heading, fly_to_place, circle_here, flight_report, fuel_check, how_far,
             time_to_target, level_off, set_rcs, set_lights, action_group, set_engines, cut_engines, engine_mode,
             afterburner, flaps, trim, set_trim, get_trim_state, auto_trim_now, save_craft_notes, trim_panel_open, land, go_around,
             touch_and_go, abort_ag, crew_report, set_override,
             authorise_all, match_target_plane, apsis_longitude, launch_to_target_plane, sync_orbit_altitude, landing_check,
             list_taxi_points]
BY_NAME = {f.__name__: f for f in TOOLS + MENU_ONLY}
_JSON = {int: "integer", float: "number", bool: "boolean", str: "string"}


def tool_schemas(hide=()):
    """OpenAI 'tools' list generated from the function signatures (minus the names in `hide`)."""
    out = []
    for f in TOOLS:
        if f.__name__ in hide:
            continue
        props, req = {}, []
        for name, p in inspect.signature(f).parameters.items():
            props[name] = {"type": _JSON.get(p.annotation, "string")}
            if p.default is inspect.Parameter.empty:
                req.append(name)
            else:
                props[name]["default"] = p.default
        out.append({"type": "function", "function": {
            "name": f.__name__, "description": inspect.getdoc(f),
            "parameters": {"type": "object", "properties": props, "required": req}}})
    return out


def _engine_restart_note(name, args, res):
    """Throttle up with engines shut down (abort / emergency): the pilot re-lights them after a few seconds."""
    from . import engine_restart
    if name not in engine_restart.TOOLS or " failed: " in res or res.startswith("Not in flight"):
        return ""
    if name == "set_throttle":
        try:
            if float(args.get("value", 0)) <= 0:
                return ""
        except (TypeError, ValueError):
            return ""
    try:
        return engine_restart.maybe_restart(_vessel(), pilot_name())
    except Exception:  # noqa: BLE001
        return ""


# tools that steer toward a target: during a plane takeoff (roll, rotation, climb-out < 150 m) they wait for climb-out
STEER_TOOLS = {"land_at_ksc", "land_plane", "land_at_spot", "land", "fly_to", "fly_to_place", "set_heading", "turn",
               "circle_here", "heli_control", "go_around", "touch_and_go", "set_flight_plan"}


def takeoff_in_progress():
    return hold.active() and (hold.STATUS.get("phase") == "takeoff" or hold.STATUS.get("climbout_hdg") is not None)


def _defer_until_climbout(name, args, timeout=240.0):
    """Run the tool once the takeoff is past the climb-out gate; dropped if the takeoff ends any other way."""
    def run():
        t0 = time.time()
        while takeoff_in_progress() and time.time() - t0 < timeout:
            time.sleep(0.5)
        if hold.active() and not takeoff_in_progress():
            res = call_tool(name, args)
            science.post_event(f"Climb-out done - {name}: {res}")
        else:
            science.post_event(f"Takeoff ended - dropped the deferred {name}.")
    threading.Thread(target=run, daemon=True, name=f"defer-{name}").start()
    return (f"Deferred: taking off - holding the runway heading, wings level. {name} starts by itself once we're "
            f"above {tko.CLIMBOUT_AGL:.0f} m and climbing. No need to call it again.")


def call_tool(name, args=None):
    """Run a tool by name with a dict of args. Always returns a string."""
    import json
    f = BY_NAME.get(name)
    if f is None:
        return f"Unknown tool '{name}'."
    if name in STEER_TOOLS:
        try:
            if takeoff_in_progress():
                return _defer_until_climbout(name, dict(args or {}))
        except Exception:  # noqa: BLE001
            pass
    try:
        with _lock:
            res = f(**(args or {}))
        res = res if isinstance(res, str) else json.dumps(res)
        return res + _engine_restart_note(name, args or {}, res)
    except TypeError as e:
        return f"Bad arguments for {name}: {e}"
    except Exception as e:
        global _conn
        if "connect" in str(e).lower() or e.__class__.__name__ in ("ConnectionRefusedError", "ConnectionResetError"):
            _conn = None
        return f"{name} failed: {e.__class__.__name__}: {e}"
