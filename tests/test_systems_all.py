"""Offline: Systems dashboard (GET /systems) for every craft type - rockets / landers / rovers / stations / probes get
their own rows (fuel per stage, power + solar, RCS, SAS, comms, next stage, wheels), planes / helis unchanged."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import emergency as em  # noqa: E402


def setup_function(_):
    em.DET.reset()


def eng(active=True, air=False, thrust=100.0, has_fuel=True):
    return {"title": "LV-T45", "active": active, "thrust": thrust, "max_thrust": 200.0, "air": air, "has_fuel": has_fuel}


def ship(**kw):
    d = {"res": {}, "stage_res": {}, "stage": None, "next_stage": None, "gimbals": [], "solar": None, "rcs": None,
         "rcs_n": 0, "sas": None, "sas_mode": "", "comms": None, "antennas": None, "wheels": None, "wings": False,
         "legs": 0, "docking": 0, "body": "Kerbin", "sit": ""}
    d.update(kw)
    return d


def rows_of(s, sh):
    return {r[1]: (r[0], r[2]) for r in em.build_systems(s, {"ship": sh}, None)}


def test_kinds():
    assert em.ship_kind({"engines": [eng()]}, ship()) == "rocket"
    assert em.ship_kind({"engines": [eng()]}, ship(legs=4)) == "lander"
    assert em.ship_kind({"engines": [eng()]}, ship(wings=True)) == "rocket"  # fins on a rocket
    assert em.ship_kind({"engines": [eng(air=True)]}, ship(wings=True)) == "plane"
    assert em.ship_kind({"engines": []}, ship(wheels=[(True, False, True, True)] * 4)) == "rover"
    assert em.ship_kind({"engines": [], "crew": 3}, ship()) == "station"
    assert em.ship_kind({"engines": [], "crew": 0}, ship()) == "probe"
    assert em.ship_kind({"heli": {"kind": "x"}}, ship()) == "heli"


def test_rocket_rows_fuel_stage_power_gimbal_next_stage():
    sh = ship(res={"LF": (900.0, 1000.0), "OX": (1100.0, 1222.0), "EC": (5.0, 100.0)},
              stage_res={"LF": (90.0, 1000.0), "OX": (110.0, 1222.0)}, stage=3,
              next_stage=(2, {"engines": 1, "decouplers": 1, "chutes": 0, "other": 0}),
              gimbals=[(True, False, 100.0)], rcs=False, rcs_n=4, sas=True, sas_mode="prograde")
    r = rows_of({"sit": "flying", "engines": [eng()], "crew": 1, "fuel": 0.9, "g": 1.0}, sh)
    assert r["Craft"] == ("ok", "ROCKET")
    assert r["Fuel (stage 2)"][0] == "caution" and "LF 9%" in r["Fuel (stage 2)"][1]
    assert r["Power"][0] == "fail" and r["Power"][1].startswith("5%")  # EC < 10% red
    assert "gimbal 100%" in r["Engine 1 LV-T45"][1]
    assert r["Next stage"][1] == "S2: 1 engine, 1 decoupler"
    assert r["RCS"][1].startswith("off, 4 thrusters") and r["SAS"][1] == "on, prograde"
    assert "Fuel" not in r  # the whole-craft fuel % is replaced by the per-resource rows


def test_flameout_no_fuel_and_low_fuel_red():
    sh = ship(res={"LF": (10.0, 1000.0)}, stage_res={"LF": (0.0, 500.0)}, stage=1)
    r = rows_of({"sit": "flying", "engines": [eng(thrust=0.0, has_fuel=False)], "g": 1.0}, sh)
    assert "NO FUEL" in r["Engine 1 LV-T45"][1] and r["Fuel (stage 0)"][0] == "fail"


def test_rover_rows():
    sh = ship(res={"EC": (20.0, 100.0)}, wheels=[(True, False, True, True)] * 3 + [(False, True, True, False)],
              sas=False)
    r = rows_of({"sit": "landed", "engines": [], "speed": 7.25, "crew": 1, "g": 1.0}, sh)
    assert r["Craft"][1] == "ROVER" and r["Battery"][0] == "caution"
    assert r["Wheels"] == ("fail", "3/4 grounded, motors 3/4 on, 1 BROKEN") and r["Speed"][1] == "7.2 m/s"


def test_probe_and_station_rows():
    sh = ship(res={"EC": (900.0, 1000.0), "Mono": (3.0, 100.0)}, solar=(2, 4, 3.5), comms=(False, 0.0),
              antennas=(0, 1), rcs=True, rcs_n=8, sas=True, sas_mode="stability_assist")
    r = rows_of({"sit": "orbiting", "engines": [], "crew": 0, "g": 0.0}, sh)
    assert r["Craft"][1] == "PROBE" and r["Power"] == ("ok", "90% (900/1000), solar 2/4 out +3.5/s")
    assert r["Comms"][0] == "fail" and "NO SIGNAL" in r["Comms"][1] and "antennas 0/1" in r["Comms"][1]
    assert r["RCS"][0] == "caution" and r["Mono total"][0] == "fail"
    st = rows_of({"sit": "orbiting", "engines": [], "crew": 2, "g": 0.0}, sh)
    assert st["Craft"][1] == "STATION" and st["Comms"][0] == "caution"


def test_plane_and_heli_get_no_ship_rows():
    r = rows_of({"sit": "flying", "engines": [eng(air=True)], "fuel": 0.5, "g": 1.0}, ship(wings=True))
    assert "Craft" not in r and r["Fuel"][1] == "50%"
    r = rows_of({"sit": "flying", "engines": [], "heli": {"kind": "compound"}, "vs": 0.0, "g": 1.0}, ship())
    assert "Craft" not in r and "Mode" in r


def test_scan_ship_with_fake_vessel():
    class Res:
        def __init__(self, d):
            self.d = d

        def max(self, n):
            return self.d.get(n, (0, 0))[1]

        def amount(self, n):
            return self.d.get(n, (0, 0))[0]

    parts_s2 = [NS(engine=object(), decoupler=None, parachute=None), NS(engine=None, decoupler=object(), parachute=None)]
    v = NS(resources=Res({"LiquidFuel": (50, 100), "ElectricCharge": (10, 200)}),
           resources_in_decouple_stage=lambda st, cum: Res({"LiquidFuel": (5, 50)}),
           control=NS(current_stage=3, rcs=False, sas=True, sas_mode="SASMode.stability_assist"),
           parts=NS(in_stage=lambda st: parts_s2, engines=[NS(gimballed=True, gimbal_locked=False, gimbal_limit=80.0)],
                    solar_panels=[], rcs=[], antennas=[], wheels=[], legs=[1, 2, 3], docking_ports=[]),
           comms=NS(can_communicate=True, signal_strength=0.8), orbit=NS(body=NS(name="Mun")))
    sh = em._scan_ship(v)
    assert sh["res"]["LF"] == (50.0, 100.0) and sh["res"]["EC"] == (10.0, 200.0)
    assert sh["stage_res"] == {"LF": (5.0, 50.0)} and sh["next_stage"] == (2, {"engines": 1, "decouplers": 1,
                                                                              "chutes": 0, "other": 0})
    assert sh["gimbals"] == [(True, False, 80.0)] and sh["legs"] == 3 and sh["comms"] == (True, 0.8)
    assert sh["body"] == "Mun" and sh["sas"] is True
