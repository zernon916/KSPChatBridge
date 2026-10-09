"""In-game API key entry (AICS > Settings -> POST /api_key, GET /api_keys).

Keys are written to the git-ignored repo .env (same file config.py loads at start) AND applied to this process,
so the bridge uses them immediately - no restart. Never returns or logs a full key: mask() = "••••last4".
Bridge listens on 127.0.0.1 only.
"""
import logging
import os
import threading

from . import backends, config

log = logging.getLogger("kspchat")
ENV_FILE = config.ENV_FILE
KEY_VAR = {"chatgpt": "OPENAI_API_KEY", "gemini": "GEMINI_API_KEY", "groq": "GROQ_API_KEY",
           "openrouter": "OPENROUTER_API_KEY", "huggingface": "HF_TOKEN", "custom": "CUSTOM_AI_KEY"}
_lock = threading.Lock()


def mask(v):
    v = v or ""
    return "••••" + v[-4:] if len(v) >= 8 else ("••••" if v else "")


def _clean(v):
    return "".join(c for c in str(v or "") if c >= " " and c != "\x7f").strip()


def write_env(updates, path=None):
    """Set/remove NAME=value lines in .env (value "" removes). Keeps other lines/comments. Atomic replace."""
    path = path or ENV_FILE
    try:
        lines = path.read_text(encoding="utf-8-sig").splitlines()
    except OSError:
        lines = ["# KSPChatBridge secrets (git-ignored). Written by AICS > Settings or by hand: NAME=value"]
    out, done = [], set()
    for line in lines:
        k = line.split("=", 1)[0].strip().removeprefix("export ").strip() if "=" in line else ""
        if k in updates and not line.lstrip().startswith("#"):
            if updates[k] and k not in done:
                out.append(f"{k}={updates[k]}")
            done.add(k)
            continue
        out.append(line)
    out += [f"{k}={v}" for k, v in updates.items() if v and k not in done]
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text("\n".join(out) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def _apply(updates):
    for k, v in updates.items():
        if v:
            os.environ[k] = v
        else:
            os.environ.pop(k, None)
        if k == "CUSTOM_AI_URL":
            config.CUSTOM_AI_URL = v.rstrip("/")
        elif k == "CUSTOM_AI_MODEL":
            config.CUSTOM_AI_MODEL = v


def status_text():
    """Tab lines for the mod: 'backend<TAB>1|0<TAB>masked key', then 'custom_url<TAB>..' and 'custom_model<TAB>..'."""
    ready = set(backends.configured())
    rows = [f"{b}\t{int(b in ready)}\t{mask(backends.key_for(b))}" for b in KEY_VAR]
    rows += [f"custom_url\t{config.CUSTOM_AI_URL}", f"custom_model\t{config.CUSTOM_AI_MODEL}"]
    return "\n".join(rows) + "\n"


def _verify(b):
    try:
        n = len(backends.list_models(b))
        return f" Verified: the API answered ({n} models)." if n else " The API answered (no matching models listed)."
    except backends.BackendError:
        return ""
    except Exception as e:  # urllib errors never contain the key
        code = getattr(e, "code", None)
        if code in (401, 403):
            return f" WARNING: the API rejected this key (HTTP {code}) - check it."
        return f" (Couldn't verify right now: {f'HTTP {code}' if code else e.__class__.__name__}.)"


def set_key(backend, key=None, url=None, model=None, clear=False):
    """Save/clear a backend's key (and Custom URL/model). Returns a one-line, secret-free message."""
    b = backends.normalize(backend)
    if b not in KEY_VAR:
        return f"'{backend}' takes no API key (keyed: {', '.join(KEY_VAR)})."
    label = backends.LABELS[b]
    up = {}
    if clear:
        up.update({n: "" for n in backends.KEYED[b][1]})  # incl. aliases (GOOGLE_API_KEY, HUGGINGFACE_API_KEY)
    else:
        key = _clean(key)
        if key.lower().startswith("bearer "):
            key = key[7:].strip()
        if " " in key:
            return "That doesn't look like an API key (it has spaces). Nothing saved."
        if key:
            up[KEY_VAR[b]] = key
    if b == "custom":
        if url is not None:
            url = _clean(url).rstrip("/")
            if url and not url.lower().startswith(("http://", "https://")):
                return "Custom URL must start with http:// or https:// (e.g. https://api.mistral.ai/v1). Nothing saved."
            up["CUSTOM_AI_URL"] = url
        if model is not None:
            up["CUSTOM_AI_MODEL"] = _clean(model)
    if not up:
        return "Nothing to save: paste a key first."
    with _lock:
        write_env(up)
        _apply(up)
    log.info("api key update %s: %s", b, ", ".join(f"{k}={'set ' + mask(v) if v else 'cleared'}"
                                                 for k, v in up.items() if k.endswith(("KEY", "TOKEN"))) or "url/model")
    k = backends.key_for(b)
    if clear:
        msg = f"{label} key cleared." + (f" (Another key is still set: {mask(k)}.)" if k else "")
    elif KEY_VAR[b] in up:
        msg = f"{label} key saved ({mask(k)}), active now - no restart needed."
    else:
        msg = f"{label} settings saved."
    if b == "custom":
        need = [n for n, v in (("URL", config.CUSTOM_AI_URL), ("model", config.CUSTOM_AI_MODEL)) if not v]
        msg += f" Custom still needs: {' + '.join(need)}." if need else f" Custom: {config.CUSTOM_AI_MODEL} @ {config.CUSTOM_AI_URL}."
    if b == "chatgpt" and not clear and k:
        from . import mcp_chat
        if mcp_chat.mode() != "api":
            msg += " (ChatGPT is in MCP mode; pick 'OpenAI API key' under 'ChatGPT via' to use it.)"
    if not clear and b in backends.configured():
        msg += _verify(b)
    return msg
