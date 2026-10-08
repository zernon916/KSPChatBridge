"""Language option (Luke 2026-10-08): '/language filter on|off' (bridge_settings.json 'language_filter', default ON).

ON: the AI prompts carry a no-cursing line and replies / crew lines go through the profanity filter (cuss words
masked). OFF: no no-cursing line, no profanity filter. Slurs and hate are ALWAYS refused / removed either way.
"""
import re

SETTING = "language_filter"
NO_CURSING = "Keep it clean: no cursing or swear words (Kerbal-style 'Oh snacks!' is fine)."
ALWAYS = "Never use slurs or hateful language about any group."
# common cuss words (whole-word stems, with the usual endings)
_PROFANE = re.compile(r"\b(?:f+u+c+k\w*|motherf\w*|sh+i+t+\w*|bull?sh\w*|damn\w*|goddam\w*|hell|bitch\w*|bastard\w*|"
                      r"ass(?:hole)?s?|dick(?:head)?s?|piss\w*|crap\w*|wtf|stfu|bloody)\b", re.I)
# slurs (stems; always removed). Kept short and to clear-cut terms.
_SLURS = re.compile(r"\b(?:n+[i1]+gg\w*|f+a+gg?\w*|f+a+g+s?|r+e+t+a+r+d\w*|k+i+k+e+s?|sp+i+c+s?|ch+i+n+k+s?|"
                    r"tr+a+nn+(?:y|ie)s?|wetback\w*|g+o+o+k+s?)\b", re.I)


def enabled():
    try:
        from . import settings
        v = settings.get(SETTING)
        return True if v is None else bool(v)
    except Exception:  # noqa: BLE001
        return True


def command(arg):
    a = " ".join(str(arg or "").lower().split()).replace("filter", "").strip()
    from . import settings
    if a in ("on", "off"):
        settings.put(SETTING, a == "on")
        return (f"Language filter {a}." + ("" if a == "on" else " (Slurs and hate stay blocked.)"))
    return f"Language filter is {'on' if enabled() else 'off'}. Use /language filter on|off."


def prompt_lines():
    """System-prompt lines for the current setting."""
    return (NO_CURSING + " " + ALWAYS) if enabled() else ALWAYS


def _mask(m):
    w = m.group(0)
    return w[0] + "*" * (len(w) - 1)


def clean(text, on=None):
    """Slurs always removed; cuss words masked ('s***') when the filter is on."""
    if not text:
        return text
    t = _SLURS.sub("[removed]", str(text))
    if enabled() if on is None else on:
        t = _PROFANE.sub(_mask, t)
    return t