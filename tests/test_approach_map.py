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



def test_turn_audit_flags_short_leg_before_big_turn():
    pts = [('A', 0.2, -75.0), ('B', 0.0, -74.99), ('C', 0.0, -74.8), ('THR', 0.0, -74.7)]
    a, r = am.turn_audit(pts, 150)
    assert 6000 < r < 6500 and a[0]['tight'] and a[0]['turn'] > 80
    a2, _ = am.turn_audit([('A', 0.0, -76.0), ('B', 0.0, -75.5), ('THR', 0.0, -74.7)], 150)
    assert not a2[0]['tight']



def test_generator_embeds_terrain(tmp_path):
    import json, subprocess, sys
    t = tmp_path / 'terrain_kerbin.json'; t.write_text(json.dumps({'body': 'Kerbin', 'sites': [{'name': 'KSC', 'lat0': 0, 'lon0': 0, 'dlat': .01, 'dlon': .01, 'n': 2, 'h': [1, 2, 3, 4]}]}))
    out = tmp_path / 'm.html'
    subprocess.run([sys.executable, str(ROOT / 'tools' / 'approach_map.py'), str(tmp_path), str(out), str(t)], check=True)
    html = out.read_text(encoding='utf-8')
    assert '"h": [1, 2, 3, 4]' in html and 'function terr(' in html



def test_gui_window_ids_unique():
    import re
    ids = {}
    for f in (ROOT / 'KSPChatMod').glob('*.cs'):
        for m in re.finditer(r'(\w+)\s*=\s*0x([0-9A-Fa-f]{8})', f.read_text(encoding='utf-8-sig')):
            v = int(m.group(2), 16); assert v not in ids, (f.name, m.group(1), ids.get(v))
            ids[v] = (f.name, m.group(1))
    menu = next(v for v, n in ids.items() if n[1] == 'MenuId')
    assert all(not (menu < v <= menu + 32) for v in ids if v != menu), 'id inside the menu panel range'

