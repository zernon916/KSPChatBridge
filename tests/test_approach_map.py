import importlib.util, pathlib, re
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("am", ROOT / "tools" / "approach_map.py"); am = importlib.util.module_from_spec(spec); spec.loader.exec_module(am)

def test_all_four_ends_with_3deg_fixes():
    e = am.ends()
    assert set(e) == {"KSC 09", "KSC 27", "Island 09", "Island 27"}
    k = e["KSC 09"]; faf = next(f for f in k["fixes"] if f["role"] == "faf"); sf = next(f for f in k["fixes"] if f["role"] == "sf")
    assert abs(faf["alt"] - (69.1 + 629)) < 2 and abs(sf["alt"] - (69.1 + 210)) < 2
    assert abs(e["KSC 09"]["course"] - 90.4) < 1 and abs(e["KSC 27"]["course"] - 270.4) < 1

def test_roles_match_the_mod():
    cs = (ROOT / "KSPChatMod" / "FlightMissions.cs").read_text(encoding="utf-8")
    roles = set(re.search(r'Roles = \{([^}]*)\}', cs).group(1).replace('"', '').replace(' ', '').split(','))
    assert {f["role"] for f in am.ends()["KSC 27"]["fixes"]} | {"wp"} == roles


def test_final_fixes_last_and_sides():
    fx = am.ends()['KSC 09']['fixes']
    assert [f['role'] for f in fx[-2:]] == ['faf', 'sf']
    assert all(f['side'] in ('left', 'right', 'both') for f in fx)

