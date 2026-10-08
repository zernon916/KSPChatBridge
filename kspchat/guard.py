"""One autopilot at a time, across processes (bridge, MCP server, test harnesses).

A running controller (lander / plane / docking) claims the craft in bridge_settings.json ("autopilot_busy":
kind, pid, started, heartbeat) and refreshes the heartbeat every couple of seconds. New landing/docking tools
refuse while a fresh claim exists; abort() posts "autopilot_stop" which every controller polls.
"""
import os
import time

from . import settings

STALE_S = 10.0


def _alive(pid):
    """True if a process with this pid is running (Windows via OpenProcess; never sends signals)."""
    try:
        pid = int(pid)
    except Exception:
        return False
    if pid == os.getpid():
        return True
    if os.name == "nt":
        import ctypes
        k = ctypes.windll.kernel32
        h = k.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = k.GetExitCodeProcess(h, ctypes.byref(code))
        k.CloseHandle(h)
        return bool(ok) and code.value == 259  # STILL_ACTIVE
    try:
        os.kill(pid, 0)  # POSIX only: signal 0 = existence check
        return True
    except OSError:
        return False


def busy():
    """Return the active claim dict, or None if the craft is free (stale heartbeat or dead owner process)."""
    c = settings.get("autopilot_busy")
    if not c or time.time() - float(c.get("heartbeat", 0)) > STALE_S or not _alive(c.get("pid")):
        return None
    return c


def claim(kind):
    now = time.time()
    settings.put("autopilot_busy", {"kind": kind, "pid": os.getpid(), "started": now, "heartbeat": now})
    return now


def beat(kind, started, info=None):
    """Refresh the heartbeat; returns True if an abort was requested after we started."""
    stop = settings.get("autopilot_stop") or 0
    settings.put("autopilot_busy", {"kind": kind, "pid": os.getpid(), "started": started, "heartbeat": time.time(),
                                    "info": info or {}})
    return float(stop) > started


def release(started):
    c = settings.get("autopilot_busy")
    if c and abs(float(c.get("started", 0)) - started) < 1e-6:
        settings.put("autopilot_busy", None)


def request_stop():
    settings.put("autopilot_stop", time.time())


def refuse_msg():
    c = busy()
    if not c:
        return None
    mins = (time.time() - float(c["started"])) / 60
    names = {"plane": "a plane autoland", "lander": "a landing", "docking": "a rendezvous/docking"}
    return (f"Busy: {names.get(c['kind'], c['kind'])} is already flying this craft (started {mins:.0f} min ago). "
            "I won't start a second autopilot on top of it. Say 'abort' first if you want to change plans.")


def hold(kind, stop_event, info_fn=None):
    """Claim the craft for a controller thread; returns end() to call when it finishes. A heartbeat thread
    keeps the claim fresh and sets stop_event if abort() was requested from any process."""
    import threading
    started = claim(kind)
    done = threading.Event()

    def hb():
        while not done.wait(2.0):
            try:
                if beat(kind, started, info_fn() if info_fn else None):
                    stop_event.set()
            except Exception:
                pass
    threading.Thread(target=hb, daemon=True, name=f"guard-{kind}").start()

    def end():
        done.set()
        try:
            release(started)
        except Exception:
            pass
    return end
