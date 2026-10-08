"""Live autopilot status for the mod's STATUS window and the AICS menu's active indicators (GET /status).

snapshot() -> [(key, value)] with ready-to-show strings. It reads the controllers' in-memory STATUS dicts (no kRPC),
plus the landing ETA (the same estimate GET /landing gives), the cached pilot name and a MechJeb check cached 5 s.
Keys: autopilot, phase, ind_<holds|autoland|lander|flightplan|mechjeb|docking|taxi> (1/0), alt, speed, heading, bank,
pitch, throttle, overrides, pilot, eta, plan, protect, alert (active emergencies). Empty values are left out.
"""
import logging
import time

log = logging.getLogger("kspchat.status")

_MJ = {"t": 0.0, "v": ""}
MJ_TTL = 5.0


def _mechjeb_active():
    """Name of the running MechJeb autopilot ("" = none), cached MJ_TTL s (a few kRPC calls)."""
    if time.time() - _MJ["t"] < MJ_TTL:
        return _MJ["v"]
    name = ""
    try:
        from . import ksp_actions
        mj = ksp_actions.conn().mech_jeb
        for attr, label in (("ascent_autopilot", "MJ ascent"), ("node_executor", "MJ node executor"),
                            ("landing_autopilot", "MJ landing"), ("rendezvous_autopilot", "MJ rendezvous"),
                            ("docking_autopilot", "MJ docking")):
            try:
                if getattr(mj, attr).enabled:
                    name = label
                    break
            except Exception:  # noqa: BLE001
                pass
    except Exception:  # noqa: BLE001
        name = ""
    _MJ.update(t=time.time(), v=name)
    return name


_ETA = {"t": 0.0, "v": ""}
ETA_TTL = 2.0


def _eta(spots):
    """Landing distance / ETA line (rocket, plane or chutes) while a landing is happening, cached ETA_TTL s."""
    if time.time() - _ETA["t"] < ETA_TTL:
        return _ETA["v"]
    v = ""
    try:
        r = spots.landing_eta(quiet=True)
        if r.get("active") and r.get("summary"):
            v = r["summary"]
    except Exception:  # noqa: BLE001
        v = ""
    _ETA.update(t=time.time(), v=v)
    return v


def _f(x, fmt="{:.0f}"):
    try:
        return fmt.format(float(x))
    except (TypeError, ValueError):
        return "?"


def _overrides():
    from . import protect, speedcap
    st, marks, out = speedcap._state, speedcap._marks, []
    if st.get("bank"):
        out.append(f"bank {_f(st['bank'])} {marks.get('bank', '*')}")
    if st.get("pitch"):
        out.append(f"pitch {_f(st['pitch'])} {marks.get('pitch', '*')}")
    if st.get("speed_max"):
        out.append(f"speed MAX {marks.get('speed', '*')}")
    elif st.get("eas"):
        out.append(f"speed cap {_f(speedcap.limits()[1])} {marks.get('speed', '*')}")
    if st.get("ceiling"):
        out.append(f"ceiling {_f(st['ceiling'])} m {marks.get('altitude', '*')}")
    if st.get("all"):
        out.append("authorise all")
    s = ", ".join(out)
    if s and protect.factor() < 1.0:
        s += f" (eased to {100 * protect.factor():.0f}%)"
    return s


def snapshot():
    from . import docking, flightplan, heli, hold, lander, plane, protect, speedcap, spots, taxi
    out = []
    ind = {"holds": hold.active(), "autoland": plane.active(), "lander": lander.active(),
           "flightplan": flightplan.active(), "docking": docking.active(), "taxi": taxi.active(),
           "heli": heli.active()}
    mj = "" if (ind["autoland"] or ind["holds"]) else _mechjeb_active()
    ind["mechjeb"] = bool(mj)
    if ind["autoland"]:
        ap, st = "autoland / fly-to", plane.STATUS
    elif ind["holds"]:
        ap, st = "holds", hold.STATUS
    elif ind["heli"]:
        ap, st = "HELI", heli.STATUS
    elif ind["lander"]:
        ap, st = "rocket lander", lander.STATUS
    elif ind["taxi"]:
        ap, st = "taxi", taxi.STATUS
    elif ind["docking"]:
        ap, st = "docking", docking.STATUS
    elif mj:
        ap, st = mj, {}
    else:
        ap, st = "none", {}
    out.append(("autopilot", ap + (" (flight plan)" if ind["flightplan"] else "")))
    if st.get("phase"):
        out.append(("phase", str(st["phase"])))
    out += [("ind_" + k, "1" if v else "0") for k, v in ind.items()]
    try:
        if ind["holds"] and not ind["autoland"]:
            tg = st.get("targets") or {}
            alt_t = tg.get("altitude")
            ref = (tg.get("altitude_ref") or "agl").upper()
            out.append(("alt", f"{_f(st.get('alt_msl'))} m MSL" + (f" -> {_f(alt_t)} m {ref}" if alt_t is not None else "")
                        + (f", V/S {_f(st.get('vs'), '{:+.0f}')} -> {_f(st.get('vs_des'), '{:+.0f}')}" if st.get("vs") is not None else "")))
            out.append(("speed", f"{_f(st.get('speed'))} m/s" + (f" -> {_f(tg['speed'])}" if tg.get("speed") else "")
                        + f" (EAS {_f(st.get('eas'))}, cap {_f(speedcap.limits()[1])})"))
            out.append(("heading", f"{_f(st.get('hdg'), '{:03.0f}')}" + (f" -> {_f(st['hdg_des'], '{:03.0f}')}" if st.get("hdg_des") is not None else "")))
            out.append(("bank", f"{_f(st.get('bank'))} -> {_f(st.get('bank_des'))} (limit {_f(speedcap.bank_limit(st.get('speed') or 0))})"))
            out.append(("pitch", f"{_f(st.get('pitch'), '{:.1f}')} -> {_f(st.get('pitch_des'), '{:.1f}')}"
                        + (f" (pitch hold {_f(tg['pitch'])})" if tg.get("pitch") not in (None, "off") else "")))
            thr = tg.get("throttle")
            manual = thr not in (None, "off")
            out.append(("throttle", ("manual " if manual else "auto ") + f"{_f(100 * float(st.get('throttle') or 0))}%"))
        elif ind["heli"]:
            out.append(("alt", f"{_f(st.get('agl'), '{:.1f}')} m AGL" + (f" -> {_f(heli.STATE.get('alt'))}" if heli.STATE.get("alt") is not None else "")
                        + f", V/S {_f(st.get('vs'), '{:+.1f}')} -> {_f(st.get('vs_t'), '{:+.1f}')}"))
            out.append(("rotor", f"collective {_f(st.get('coll'), '{:.1f}')} deg, {st.get('kind') or ''}"))
            out.append(("speed", f"fwd {_f(st.get('fwd'), '{:.1f}')} -> {_f(st.get('fwd_t'), '{:.1f}')} m/s"))
            out.append(("heading", f"HDG {_f(st.get('hdg'), '{:03.0f}')} -> {_f(st.get('hdg_t'), '{:03.0f}')}"
                        + (" (pinned)" if heli.STATE.get("face") is not None else "")))
            out.append(("track", f"TRK {_f(st.get('trk'), '{:03.0f}')} at {_f(st.get('gs'), '{:.1f}')} m/s"
                        + (f" -> {_f(st['trk_t'], '{:03.0f}')} at {_f(st.get('gs_t'), '{:.0f}')}" if st.get("trk_t") is not None else "")
                        + f", drift {_f(st.get('drift'), '{:+.0f}')}"))
        elif ind["autoland"]:
            rwy = st.get("runway_alt") or 0.0
            h = st.get("h")
            out.append(("alt", (f"{_f((h or 0) + rwy)} m MSL" if h is not None else "?")
                        + (f" -> {_f(st['h_des_msl'])} m" if st.get("h_des_msl") is not None else "")
                        + (f", V/S {_f(st.get('vs'), '{:+.0f}')}" if st.get("vs") is not None else "")))
            out.append(("speed", f"{_f(st.get('speed'))} m/s" + (f" -> {_f(st['v_des'])}" if st.get("v_des") is not None else "")
                        + f" (cap {_f(speedcap.limits()[1])})"))
            out.append(("heading", f"{_f(st.get('hdg'), '{:03.0f}')}" + (f" -> {_f(st['hdg_des'], '{:03.0f}')}" if st.get("hdg_des") is not None else "")))
            out.append(("bank", f"{_f(st.get('bank'))} (limit {_f(speedcap.bank_override() or speedcap.bank_limit(st.get('speed') or 0))})"))
            if st.get("throttle") is not None:
                out.append(("throttle", f"auto {_f(100 * float(st['throttle']))}%"))
            live = ", ".join(f"{k} {_f(v)}" for k, v in plane.LIVE.items())
            if live:
                out.append(("orders", "live: " + live))
    except Exception as e:  # noqa: BLE001
        log.debug("status: %s", e)
    ov = _overrides()
    if ov:
        out.append(("overrides", ov))
    try:
        from . import ksp_actions
        name = ksp_actions._PILOT.get("name")
    except Exception:  # noqa: BLE001
        name = None
    pilot = (name or "pilot") + (" - BLACKED OUT" if protect.blackout() else " - conscious")
    out.append(("pilot", pilot))
    if protect.factor() < 1.0:
        out.append(("protect", f"easing the override: {protect._st.get('reason')}"))
    eta = _eta(spots)
    if eta:
        out.append(("eta", eta))
    try:
        from . import emergency
        al = emergency.alerts_text()
        if al:
            out.append(("alert", al))
    except Exception:  # noqa: BLE001
        pass
    try:
        pl = flightplan.status_line()
        if pl:
            out.append(("plan", pl))
    except Exception:  # noqa: BLE001
        pass
    return [(k, str(v).replace("\t", " ").replace("\n", " ")) for k, v in out if v not in (None, "")]