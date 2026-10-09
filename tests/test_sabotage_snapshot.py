"""Pre-takeoff sabotage snapshot: detect inverted deploy direction, chute deploy, wrong gear; fumble + revert."""
from types import SimpleNamespace as NS

from kspchat import emergency as em


def SURF(p=True, y=False, r=True, inv=False, auth=100.0, dep=False, dep_dir=False):
    return ("Elevon", p, y, r, inv, auth, dep, dep_dir)


def S(t, **kw):
    s = {"t": t, "vid": 1, "sit": "flying", "throttle": 0.8, "parts": 20, "stage": 0, "engines": [],
         "want_gear": False, "landing": False, "who": "Sidry"}
    s.update(kw)
    return s


def test_deploy_direction_detected_and_reverted(monkeypatch):
    """Inverted control-surface deploy direction vs the pre-takeoff snapshot -> call-out + revert action."""
    ok = {"surfaces": [SURF()], "gear": False, "chutes": [("Mk2-R", False, False)]}
    bad = {"surfaces": [SURF(dep_dir=True)], "gear": False, "chutes": [("Mk2-R", False, False)]}
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=ok, want_gear=True))          # runway snapshot
    evs = d.tick(S(1, cfg=bad))
    assert evs and evs[0]["kind"] == "config" and evs[0]["tag"] == "deploy_dir"
    assert "revert" in evs[0]["actions"] and "deploy direction" in evs[0]["changes"][0]

    made = []
    class T:
        def __init__(self, delay, fn):
            self.d, self.fn = delay, fn
            made.append(self)
        def start(self):
            pass
    monkeypatch.setattr(em.threading, "Timer", T)
    monkeypatch.setattr(em, "_engaged", lambda: True)
    cs = NS(part=NS(title="Elevon", modules=[]), inverted=False, authority_limiter=100.0, deployed=False,
            pitch_enabled=True, yaw_enabled=False, roll_enabled=True)
    v = NS(parts=NS(control_surfaces=[cs], reaction_wheels=[], intakes=[], parachutes=[], all=[]),
           control=NS(gear=False, brakes=False, lights=False, sas=True, rcs=False,
                      get_action_group=lambda i: False, set_action_group=lambda i, x: None))
    calls = []
    monkeypatch.setattr(em, "_set_deploy_dir", lambda c, inv: calls.append(inv) or True)
    monkeypatch.setattr(em, "_post", lambda m: None)
    lines = em.handle(evs, v, S(1, cfg=bad), post=lambda x: None)
    assert "WRONG way" in lines[0] or "deploy direction" in lines[0].lower() or "messing" in lines[0].lower()
    assert made and 2.0 <= made[0].d <= 4.0
    made[0].fn()
    assert calls == [False]                                   # snapshot had normal deploy direction


def test_chute_deployed_in_flight_is_cut(monkeypatch):
    """A chute deployed in flight can't be repacked -> cut it and mention the drag."""
    ok = {"surfaces": [], "gear": False, "chutes": [("Mk2-R", False, False)]}
    bad = {"surfaces": [], "gear": False, "chutes": [("Mk2-R", True, True)]}
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=ok, want_gear=True))
    evs = d.tick(S(1, cfg=bad))
    assert evs[0]["tag"] == "chute" and "cut_chutes" in evs[0]["actions"]

    cut = []
    chute = NS(deployed=True, cut=lambda: cut.append(1))
    v = NS(parts=NS(parachutes=[chute], control_surfaces=[], reaction_wheels=[], intakes=[], all=[]),
           control=NS())
    monkeypatch.setattr(em, "_engaged", lambda: True)
    monkeypatch.setattr(em.threading, "Timer", lambda d, fn: NS(start=lambda: None))
    lines = em.handle(evs, v, S(1), post=lambda x: None)
    assert cut == [1] and ("cut" in lines[0].lower() or "chute" in lines[0].lower() or "drag" in lines[0].lower())


def test_gear_down_at_cruise_reverted(monkeypatch):
    """Gear lowered at cruise (want up) -> call-out and revert raises it after a fumble."""
    ok = {"surfaces": [], "gear": True, "chutes": []}           # runway: gear down
    cruise = {"surfaces": [], "gear": False, "chutes": []}      # after takeoff: up (ok)
    down = {"surfaces": [], "gear": True, "chutes": []}         # saboteur drops it
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=ok, want_gear=True))
    d.tick(S(1, cfg=cruise, want_gear=False))                  # gear-up is correct for cruise
    evs = d.tick(S(2, cfg=down, want_gear=False))
    assert evs and evs[0]["tag"] == "gear" and "revert" in evs[0]["actions"]

    made = []
    class T:
        def __init__(self, delay, fn):
            self.d, self.fn = delay, fn
            made.append(self)
        def start(self):
            pass
    ctl = NS(gear=True, brakes=False, lights=False, sas=False, rcs=False,
             get_action_group=lambda i: False, set_action_group=lambda i, x: None)
    v = NS(parts=NS(control_surfaces=[], reaction_wheels=[], intakes=[], parachutes=[], all=[]), control=ctl)
    monkeypatch.setattr(em.threading, "Timer", T)
    monkeypatch.setattr(em, "_engaged", lambda: True)
    monkeypatch.setattr(em, "_post", lambda m: None)
    em.handle(evs, v, S(2, want_gear=False), post=lambda x: None)
    made[0].fn()
    assert ctl.gear is False                                  # raised for cruise


def test_gear_up_on_final_reverted(monkeypatch):
    """Gear raised on final approach (want down) -> put it back down."""
    cfg_down = {"surfaces": [], "gear": True, "chutes": []}
    cfg_up = {"surfaces": [], "gear": False, "chutes": []}
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=cfg_down, want_gear=True))
    d.tick(S(1, cfg=cfg_down, want_gear=True, landing=True))
    evs = d.tick(S(2, cfg=cfg_up, want_gear=True, landing=True))
    assert evs and evs[0]["tag"] == "gear"

    made = []
    class T:
        def __init__(self, delay, fn):
            made.append(self)
            self.fn = fn
        def start(self):
            pass
    ctl = NS(gear=False, brakes=False, lights=False, sas=False, rcs=False,
             get_action_group=lambda i: False, set_action_group=lambda i, x: None)
    v = NS(parts=NS(control_surfaces=[], reaction_wheels=[], intakes=[], parachutes=[], all=[]), control=ctl)
    monkeypatch.setattr(em.threading, "Timer", T)
    monkeypatch.setattr(em, "_engaged", lambda: True)
    monkeypatch.setattr(em, "_post", lambda m: None)
    em.handle(evs, v, S(2, want_gear=True, landing=True), post=lambda x: None)
    made[0].fn()
    assert ctl.gear is True


def test_landing_flaps_and_brakes_not_sabotage():
    """Approach/rollout: flaps and brakes are the autopilot's - not sabotage."""
    old = {"surfaces": [SURF()], "brakes": False, "gear": True, "chutes": []}
    new = {"surfaces": [SURF(dep=True)], "brakes": True, "gear": True, "chutes": []}
    assert [c[2] for c in em.diff_config(old, new, landing=True, want_gear=True) if c[0]] == []


def test_own_changes_ignored():
    ok = {"surfaces": [SURF()], "gear": False, "chutes": [("Mk2-R", False, False)], "engines": [("J-33", True, "")]}
    bad = {"surfaces": [SURF(dep_dir=True)], "gear": True, "chutes": [("Mk2-R", True, True)],
           "engines": [("J-33", False, "")]}
    # own=True still reports surface invert/deploy_dir (tools don't flip those) but skips engines/chutes/gear
    tags = [c[2] for c in em.diff_config(ok, bad, own=True, want_gear=False) if c[0]]
    assert "deploy_dir" in tags and "engine_off" not in tags and "chute" not in tags and "gear" not in tags


def test_thrust_limit_detected_and_reverted(monkeypatch):
    """Engine thrust limiter lowered in flight -> call-out and revert after fumble."""
    ok = {"surfaces": [], "gear": False, "chutes": [], "engines": [("J-33", True, "", 100.0)]}
    bad = {"surfaces": [], "gear": False, "chutes": [], "engines": [("J-33", True, "", 40.0)]}
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=ok, want_gear=True))
    evs = d.tick(S(1, cfg=bad))
    assert evs and evs[0]["kind"] == "config" and evs[0]["tag"] == "thrust_limit"
    assert "revert" in evs[0]["actions"]

    class Mod:
        name = "ModuleEnginesFX"
        fields = ["Thrust Percentage"]
        pct = 40.0

        def get_field(self, k):
            return str(self.pct)

        def set_field_float(self, k, v):
            self.pct = float(v)

    eng = NS(part=NS(title="J-33", modules=[Mod()]), active=True, has_modes=False)
    v = NS(parts=NS(control_surfaces=[], reaction_wheels=[], intakes=[], parachutes=[], engines=[eng], all=[]),
           control=NS(gear=False, brakes=False, lights=False, sas=False, rcs=False,
                      get_action_group=lambda i: False, set_action_group=lambda i, x: None))
    made = []

    class T:
        def __init__(self, delay, fn):
            self.d, self.fn = delay, fn
            made.append(self)
        def start(self):
            pass
    monkeypatch.setattr(em.threading, "Timer", T)
    monkeypatch.setattr(em, "_engaged", lambda: True)
    monkeypatch.setattr(em, "_post", lambda m: None)
    lines = em.handle(evs, v, S(1, cfg=bad), post=lambda x: None)
    assert "thrust limit" in lines[0].lower() or "limiter" in lines[0].lower()
    assert made and 2.0 <= made[0].d <= 4.0
    made[0].fn()
    assert eng.part.modules[0].pct == 100.0


def test_thrust_limit_own_change_ignored():
    old = {"engines": [("J-33", True, "", 100.0)]}
    new = {"engines": [("J-33", True, "", 50.0)]}
    assert [c[2] for c in em.diff_config(old, new, own=True) if c[0]] == []


def test_gear_wrong_steady_state_without_tick_change():
    """Gear wrong vs want_gear with no last->cur transition (missed between scans) -> steady-state alert."""
    down = {"surfaces": [], "gear": True, "chutes": []}
    d = em.Detector()
    d.tick(S(0, sit="landed", cfg=down, want_gear=True))
    d.last_cfg = down
    evs = d.tick(S(1, cfg=down, want_gear=False))
    assert any(e.get("key") == "gear_wrong" for e in evs)


def test_diff_gear_when_previous_scan_had_no_gear_field():
    old = {"surfaces": [], "chutes": []}
    new = {"surfaces": [], "gear": True, "chutes": []}
    tags = [c[2] for c in em.diff_config(old, new, want_gear=False) if c[0]]
    assert tags == ["gear"]


def test_want_gear_cruise_during_hold_takeoff_if_gear_up(monkeypatch):
    """Airborne with gear retracted: enforce gear-up even while hold still reports takeoff phase."""
    from kspchat import hold, plane

    monkeypatch.setattr(plane, "active", lambda: False)
    monkeypatch.setattr(hold, "active", lambda: True)
    monkeypatch.setattr(hold, "STATUS", {"phase": "takeoff"})
    landing, want = em._phase_flags()
    assert landing is False and want is None


def test_heli_land_phase_wants_gear_down(monkeypatch):
    from kspchat import heli, hold, plane

    monkeypatch.setattr(heli, "active", lambda: True)
    monkeypatch.setattr(heli, "STATE", {"mode": "land"})
    monkeypatch.setattr(plane, "active", lambda: False)
    monkeypatch.setattr(hold, "active", lambda: False)
    assert em._phase_flags() == (True, True)


def test_snapshot_refreshed_on_runway():
    """Sitting on the runway refreshes the pre-takeoff snapshot so the next flight baselines cleanly."""
    d = em.Detector()
    a = {"surfaces": [SURF()], "gear": True}
    b = {"surfaces": [SURF(inv=True)], "gear": True}
    d.tick(S(0, sit="landed", cfg=a, want_gear=True))
    d.tick(S(1, sit="landed", cfg=b, want_gear=True))          # still on runway: new normal
    assert d.ref_cfg["surfaces"][0][4] is True
    cfg_air = {"surfaces": [SURF(inv=True)], "gear": False}      # gear up in cruise; inverted was runway baseline
    evs = d.tick(S(2, cfg=cfg_air, want_gear=False))
    assert not any(e["kind"] == "config" for e in evs)
