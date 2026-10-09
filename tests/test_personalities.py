"""personalities.txt -> PersonalityPrompts.cs must be in sync; the file ships in the package."""
import importlib.util, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def test_personalities_in_sync():
    spec = importlib.util.spec_from_file_location("gp", os.path.join(ROOT, "tools", "gen_personalities.py"))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    assert len(m.parse()) == 36
    assert open(os.path.join(ROOT, "KSPChatMod", "PersonalityPrompts.cs"), encoding="utf-8").read() == m.render()


def test_personalities_shipped():
    assert "personalities.txt" in open(os.path.join(ROOT, "tools", "package_release.ps1"), encoding="utf-8").read()


def test_personalities_per_save():
    src = open(os.path.join(ROOT, "KSPChatMod", "NativeCrew.cs"), encoding="utf-8").read()
    assert "class AicsCrewScenario : ScenarioModule" in src and "AddToAllGames" in src
