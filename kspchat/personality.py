"""Per-kerbal personality (Luke 2026-10-08): a temperament, two likes, one dislike (often job-ironic) - generated ONCE per kerbal name
(seeded by the name, nudged by KSP's real stats where kRPC exposes them: courage, stupidity, badass, veteran) and
persisted in kerbal_personalities.json next to bridge_settings.json (gitignored). Used in that kerbal's intercom
prompt: 'You are Bob, a nervous scientist who loves rocks and snacks and hates heights ...'."""
import json
import random
import threading
import zlib

from . import config

# full phrases, mostly positive / fun (balanced against one dislike)
LIKES = ["loves snacks", "loves explosions", "collects moon rocks", "loves going fast", "loves the view",
         "hums while working", "adores Jeb", "loves science", "tells bad puns", "loves shiny buttons",
         "loves boosters", "collects struts", "loves stargazing", "loves duct tape", "waves at every cloud",
         "names every rock", "loves a good sandwich", "keeps a lucky sock", "loves dad jokes"]
# full phrases (some job-ironic on purpose: a crew member who hates flying)
DISLIKES = ["hates flying", "is afraid of heights", "gets airsick", "hates rockets", "is terrified of space",
            "hates loud noises", "hates turbulence", "hates paperwork", "hates being upside down", "hates landings",
            "hates staging", "hates G-forces", "hates waiting", "hates cold coffee"]
CALM = ["chatty", "cheerful", "optimistic", "upbeat", "curious", "dry-witted", "grumpy", "fussy"]
_lock = threading.Lock()
_mem = {}


def path():
    return config.SETTINGS_FILE.with_name("kerbal_personalities.json")


def _load():
    try:
        return json.loads(path().read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return {}


def generate(name, stats=None):
    """Pure: name (+ stats {courage, stupidity, badass, veteran}) -> {temper, likes, dislikes, stats}."""
    rng = random.Random(zlib.crc32(str(name).encode()))
    st = {k: v for k, v in (stats or {}).items() if v is not None}
    temper = []
    if st.get("badass"):
        temper.append("unflappable")
    elif st.get("courage") is not None and float(st["courage"]) < 0.3:
        temper.append(rng.choice(["nervous", "panicky", "jumpy"]))
    if st.get("stupidity") is not None and float(st["stupidity"]) > 0.7:
        temper.append(rng.choice(["goofy", "scatterbrained"]))
    if st.get("veteran"):
        temper.append("seasoned")
    if not temper:
        temper.append(rng.choice(CALM))
    return {"temper": temper[:2], "likes": rng.sample(LIKES, 2), "dislikes": rng.sample(DISLIKES, 1), "stats": st}


def ensure(name, stats_fn=None):
    """The kerbal's personality, generated and saved on first sight (stats_fn() is only called then)."""
    if not name:
        return None
    key = str(name)
    with _lock:
        if key in _mem:
            return _mem[key]
        data = _load()
        if key not in data:
            stats = None
            if stats_fn is not None:
                try:
                    stats = stats_fn()
                except Exception:  # noqa: BLE001
                    stats = None
            data[key] = generate(key, stats)
            try:
                path().write_text(json.dumps(data, indent=1), encoding="utf-8")
            except Exception:  # noqa: BLE001
                pass
        _mem[key] = data[key]
        return data[key]


def describe(name, trait):
    """'a nervous scientist who loves rocks and snacks and hates heights'."""
    p = ensure(name) or {}
    t = " ".join(p.get("temper") or []) + " " if p.get("temper") else ""
    likes, dis = p.get("likes") or [], p.get("dislikes") or []
    out = f"a {t}{trait}"
    if likes:
        out += " who " + ", ".join(likes[:-1]) + (" and " if len(likes) > 1 else "") + likes[-1]
    if dis:
        out += (" and" if likes else " who") + " " + " and ".join(dis)
    return out


def likes(name, phrase):
    """True if this kerbal's personality has that like (e.g. 'loves dad jokes')."""
    try:
        return phrase in ((ensure(name) or {}).get("likes") or [])
    except Exception:  # noqa: BLE001
        return False


def stats_of(c):
    """kRPC CrewMember -> {courage, stupidity, badass, veteran} (whatever this kRPC exposes)."""
    out = {}
    for k in ("courage", "stupidity", "badass", "veteran"):
        try:
            out[k] = getattr(c, k)
        except Exception:  # noqa: BLE001
            out[k] = None
    return out


def reset_cache():
    _mem.clear()