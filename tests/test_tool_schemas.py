"""tools/gen_tool_schemas.py output must be committed and in sync (in-mod chat tool list)."""
import importlib.util, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _gen():
    spec = importlib.util.spec_from_file_location("gen", os.path.join(ROOT, "tools", "gen_tool_schemas.py"))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    return m


def test_schemas_in_sync():
    m = _gen()
    cur = open(os.path.join(ROOT, "KSPChatMod", "NativeToolSchemas.cs"), encoding="utf-8").read()
    assert cur == m.render(), "run python tools/gen_tool_schemas.py"


def test_every_ported_tool_has_schema():
    m = _gen()
    tools, missing = m.schemas()
    names = {t["function"]["name"] for t in tools}
    assert not missing
    assert {"land", "takeoff", "transfer_to", "circularize", "land_at_spot"} <= names
