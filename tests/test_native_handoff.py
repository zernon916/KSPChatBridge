from kspchat import ksp_actions as ka, http_server, guard, hold, heli, plane, lander, taxi, docking
import pytest


@pytest.fixture(autouse=True)
def reset_handoff():
    ka.NATIVE_HANDOFF = False
    yield
    ka.NATIVE_HANDOFF = False


class Request(http_server.Handler):
    def __init__(self, path): self.path = path
    def _body(self): return {}
    def _send(self, code, body, **kwargs): self.code, self.body = code, body


def test_busy_controller_prevents_handoff(monkeypatch):
    monkeypatch.setattr(guard, "busy", lambda: {"kind": "plane"})
    r = Request("/native/prepare-off"); r.do_POST()
    assert r.code == 409 and not ka.NATIVE_HANDOFF


def test_idle_handoff_blocks_new_commands_until_cancelled(monkeypatch):
    monkeypatch.setattr(guard, "busy", lambda: None)
    for module in (hold, heli, plane, lander, taxi, docking): monkeypatch.setattr(module, "active", lambda: False)
    calls = []
    monkeypatch.setitem(ka.BY_NAME, "handoff_probe", lambda: calls.append(1) or "done")
    r = Request("/native/prepare-off"); r.do_POST()
    assert r.code == 200 and ka.NATIVE_HANDOFF
    assert "handing control" in ka.call_tool("handoff_probe") and not calls
    r = Request("/native/cancel-off"); r.do_POST()
    assert r.code == 200 and not ka.NATIVE_HANDOFF
    assert ka.call_tool("handoff_probe") == "done" and calls == [1]


def test_native_control_file_blocks_steer_tools(tmp_path, monkeypatch):
    from kspchat import config
    monkeypatch.setattr(config, "ROOT", tmp_path)
    (tmp_path / "native_control.json").write_text(
        '{"busy":true,"mode":"hold","owner":"native","vessel":"abc"}', encoding="utf-8"
    )
    calls = []
    monkeypatch.setitem(ka.BY_NAME, "plane_hold", lambda **a: calls.append(1) or "should not run")
    assert "owns this vessel" in ka.call_tool("plane_hold", {"engage": True})
    assert calls == []
    (tmp_path / "native_control.json").write_text('{"busy":false,"owner":null}', encoding="utf-8")
    assert ka.call_tool("plane_hold", {"engage": True}) == "should not run" and calls == [1]


def test_native_control_blocks_throttle_bypass(tmp_path, monkeypatch):
    from kspchat import config
    monkeypatch.setattr(config, "ROOT", tmp_path)
    (tmp_path / "native_control.json").write_text(
        '{"busy":true,"mode":"hold","owner":"native","vessel":"abc"}', encoding="utf-8"
    )
    calls = []
    monkeypatch.setitem(ka.BY_NAME, "set_throttle", lambda **a: calls.append(a) or "ok")
    monkeypatch.setitem(ka.BY_NAME, "set_speed", lambda **a: calls.append(a) or "ok")
    assert "owns this vessel" in ka.call_tool("set_throttle", {"value": 0.5})
    assert "owns this vessel" in ka.call_tool("set_speed", {"speed": 100})
    assert calls == []
    monkeypatch.setitem(ka.BY_NAME, "fuel_check", lambda: "fuel ok")
    assert ka.call_tool("fuel_check") == "fuel ok"
