"""Playstyle memory (playstyle_notes.md) and capped, rotating chat-session logs."""
import difflib
import json
import threading
import time

from . import config

_lock = threading.Lock()
HEADER = "# Luke's KSP playstyle notes\n# One '- ' bullet per preference. Injected into the system prompt. Max %d notes.\n" % config.MAX_NOTES


def load_notes():
    if not config.NOTES_FILE.exists():
        example = config.ROOT / "playstyle_notes.example.md"
        if not example.exists():
            return []
        config.NOTES_FILE.write_text(example.read_text(encoding="utf-8"), encoding="utf-8")
    lines = config.NOTES_FILE.read_text(encoding="utf-8").splitlines()
    return [l[2:].strip() for l in lines if l.startswith("- ") and l[2:].strip()]


def _save(notes):
    config.NOTES_FILE.write_text(HEADER + "".join(f"- {n}\n" for n in notes), encoding="utf-8")


def remember(note: str) -> str:
    note = " ".join(str(note).split())[:300]
    if len(note) < 3:
        return "Note too short, nothing saved."
    with _lock:
        notes = load_notes()
        low = note.lower()
        for i, n in enumerate(notes):
            if n.lower() == low or difflib.SequenceMatcher(None, n.lower(), low).ratio() > 0.85:
                notes[i] = note  # refresh wording, keep one copy
                _save(notes)
                return f"Updated existing note: {note}"
        notes.append(note)
        dropped = notes[:-config.MAX_NOTES] if len(notes) > config.MAX_NOTES else []
        notes = notes[-config.MAX_NOTES:]
        _save(notes)
    return f"Remembered: {note}" + (f" (dropped {len(dropped)} oldest)" if dropped else "")


def notes_block():
    notes = load_notes()
    if not notes:
        return ""
    return "Luke's known playstyle preferences (respect these):\n" + "\n".join(f"- {n}" for n in notes)


def log_turn(record: dict):
    """Append one JSON line to logs/sessions-*.jsonl; rotate by size, keep N files."""
    try:
        config.LOG_DIR.mkdir(exist_ok=True)
        with _lock:
            files = sorted(config.LOG_DIR.glob("sessions-*.jsonl"))
            cur = files[-1] if files else None
            if cur is None or cur.stat().st_size > config.SESSION_LOG_MAX_BYTES:
                cur = config.LOG_DIR / time.strftime("sessions-%Y%m%d-%H%M%S.jsonl")
                files.append(cur)
            for old in files[:-config.SESSION_LOG_MAX_FILES]:
                old.unlink(missing_ok=True)
            with cur.open("a", encoding="utf-8") as f:
                f.write(json.dumps(record, ensure_ascii=False, default=str)[:200_000] + "\n")
    except Exception:
        pass  # logging must never break chat
