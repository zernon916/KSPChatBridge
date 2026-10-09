"""Native mode must never open a socket to the bridge: every :8765 request goes through BridgeHttp.Create (gated on
BridgeLauncher.UseBridge). Only BridgeLauncher's own health/shutdown (bridge-mode lifecycle) may use the raw URL."""
import os, re
MOD = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "KSPChatMod")


def test_no_raw_bridge_requests():
    bad = []
    for f in os.listdir(MOD):
        if not f.endswith(".cs") or f == "BridgeHttp.cs":
            continue
        src = open(os.path.join(MOD, f), encoding="utf-8").read()
        for m in re.finditer(r"WebRequest\.Create\(([^)]*)\)", src):
            arg = m.group(1)
            if "8765" in arg or "BridgeUrl" in arg:
                if f == "BridgeLauncher.cs" and arg.strip() in ('BridgeUrl + "health"', 'BridgeUrl + "shutdown"'):
                    continue
                bad.append(f"{f}: {arg}")
    assert not bad, bad


def test_bridge_lifecycle_gated():
    src = open(os.path.join(MOD, "BridgeLauncher.cs"), encoding="utf-8").read()
    assert "BridgeHttp.Allowed = () => UseBridge" in src
    assert "|| !UseBridge) return;" in src                      # watchdog never polls/starts the bridge natively
    assert "!enabled && UseBridge && Healthy(" in src


def test_chat_post_and_landing_panel_gated():
    src = open(os.path.join(MOD, "ChatWindow.cs"), encoding="utf-8").read()
    assert "if (!BridgeHttp.Allowed())" in src
    assert 'if (!BridgeLauncher.UseBridge) { Notice("Landing: "' in src
