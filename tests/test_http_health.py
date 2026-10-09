"""GET /health stays lightweight (no kRPC on the hot path)."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kspchat.http_server import Handler  # noqa: E402


class _FakeHandler(Handler):
    def __init__(self):
        self._code = 0
        self._body = b""

    def _send(self, code, body, text=False, model="", name=None):
        self._code = code
        self._body = (body if text else json.dumps(body)).encode("utf-8")


def test_health_has_no_scene_and_no_krpc(monkeypatch):
    def boom():
        raise AssertionError("kRPC must not run on /health")

    import kspchat.ksp_actions as ka
    monkeypatch.setattr(ka, "_scene", boom)
    h = _FakeHandler()
    h.path = "/health"
    h.do_GET()
    data = json.loads(h._body.decode())
    assert h._code == 200 and data.get("ok") is True
    assert "scene" not in data
