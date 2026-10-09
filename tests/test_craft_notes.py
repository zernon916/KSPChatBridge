"""craft_notes.json load/save (offline)."""
from types import SimpleNamespace

from kspchat import craft_notes, config


def test_skip_untitled_and_empty():
    assert not craft_notes.name_ok("")
    assert not craft_notes.name_ok("   ")
    assert not craft_notes.name_ok(craft_notes.DEFAULT_NAME)
    assert craft_notes.save(craft_notes.DEFAULT_NAME, pitch_trim=0.5) == {}
    assert craft_notes.load("Untitled Space Craft") == {}


def test_save_load_roundtrip_and_list():
    craft_notes.save("Aeris 3A", pitch_trim=0.12, roll_trim=-0.05, cruise_speed=220.0, quirks="likes flaps 1")
    got = craft_notes.load("Aeris 3A")
    assert got["pitch_trim"] == 0.12
    assert got["roll_trim"] == -0.05
    assert got["cruise_speed"] == 220.0
    assert got["quirks"] == "likes flaps 1"
    assert "updated" in got
    assert "Aeris 3A" in craft_notes.list_names()
    merged = craft_notes.merge("Aeris 3A", {"yaw_trim": 0.01})
    assert merged["yaw_trim"] == 0.01
    assert merged["pitch_trim"] == 0.12


def test_load_for_vessel():
    v = SimpleNamespace(name="Test Plane")
    craft_notes.save("Test Plane", pitch_trim=0.3)
    assert craft_notes.load_for_vessel(v)["pitch_trim"] == 0.3
    v2 = SimpleNamespace(name=craft_notes.DEFAULT_NAME)
    assert craft_notes.load_for_vessel(v2) == {}


def test_persists_to_config_path():
    craft_notes.save("Persist", pitch_trim=0.11)
    raw = config.CRAFT_NOTES_FILE.read_text(encoding="utf-8")
    assert "Persist" in raw and "0.11" in raw
