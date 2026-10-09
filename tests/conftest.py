"""pytest: collect only the OFFLINE tests. The live harnesses below fly / command the real vessel through kRPC (and
test_mcp.py sets the throttle), so `pytest` must never pick them up; run them by hand: python tests/<name>.py."""
collect_ignore = ["test_mcp.py", "plane_test.py", "landing_test.py", "circuit_test.py"]

import time  # noqa: E402
import urllib.request  # noqa: E402

import sys  # noqa: E402
from pathlib import Path  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import tempfile  # noqa: E402
from kspchat import config  # noqa: E402
# NEVER touch the live bridge_settings.json: the running bridge reads it (autopilot_stop aborts its autopilots,
# plane_hold targets steer the holds). Every test process gets its own empty settings file.
config.SETTINGS_FILE = Path(tempfile.mkdtemp(prefix="kspchat-test-")) / "bridge_settings.json"
config.SETTINGS_FILE.write_text("{}", encoding="utf-8")
config.CRAFT_NOTES_FILE = config.SETTINGS_FILE.with_name("craft_notes.json")
config.CRAFT_NOTES_FILE.write_text("{}", encoding="utf-8")
config.NOTES_FILE = config.SETTINGS_FILE.with_name("playstyle_notes.md")      # remember_preference
config.CHAT_QUEUE_FILE = config.SETTINGS_FILE.with_name("chatgpt_chat.json")  # mcp_chat queue
from kspchat import emergency, keys, ksp_actions, protect  # noqa: E402
protect.AUTO = False  # never start the kRPC G/heat watcher thread in offline tests
emergency.AUTO = False  # nor the emergency watcher
keys.ENV_FILE = config.SETTINGS_FILE.with_name(".env")                        # never the real .env

try:  # no test may talk to the live KSP: kRPC connections fail loudly (tests mock _vessel / conn instead)
    import krpc  # noqa: E402

    def _no_krpc(*a, **k):
        raise ConnectionRefusedError("kRPC is disabled in offline tests")
    krpc.connect = _no_krpc
except ImportError:
    pass

import pytest  # noqa: E402


def _no_net(*a, **k):
    import urllib.error
    raise urllib.error.URLError("network is disabled in offline tests (no LM Studio / cloud AI calls)")


@pytest.fixture(autouse=True)
def _fresh_pilot_cache(monkeypatch):
    # no test may reach a real AI backend (LM Studio on this PC, cloud APIs); tests that need replies fake _post/urlopen
    monkeypatch.setattr(urllib.request, "urlopen", _no_net)
    ksp_actions._PILOT.update(t=0.0, name=None)
    ksp_actions._AIR.update(t=0.0, v=False)  # aircraft cache (tool hiding)
    protect.AUTO = False  # no kRPC watcher thread in offline tests
    protect.reset()
    emergency.AUTO = False
    emergency.reset()
    from kspchat import reversers
    reversers._ENGAGED.clear()
    yield

_ORIG = (urllib.request.urlopen, time.sleep, ksp_actions.call_tool)


def pytest_collectstart(collector):
    """The script-style tests patch globals at import (test_backends replaces urllib's urlopen with an always-429 fake,
    time.sleep and ksp_actions.call_tool); pytest imports them all in one process, so restore those before the next
    file (and before the tests run)."""
    urllib.request.urlopen, time.sleep, ksp_actions.call_tool = _ORIG


def pytest_collection_finish(session):
    urllib.request.urlopen, time.sleep, ksp_actions.call_tool = _ORIG
