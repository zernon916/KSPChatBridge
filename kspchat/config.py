"""Central settings. Override with environment variables."""
import os
import sys
from pathlib import Path

# Source run: everything lives in the repo folder. Frozen run (PyInstaller AICSBridge.exe): bundled read-only files
# come from sys._MEIPASS; user data (.env, settings, notes, logs) goes to a writable folder: GameData/KSPChatBridge/
# PluginData/ when the exe sits in GameData/KSPChatBridge/Bridge/, else next to the exe. KSPCHAT_DATA_DIR overrides.
FROZEN = bool(getattr(sys, "frozen", False))
if FROZEN:
    _exe_dir = Path(sys.executable).resolve().parent
    RES_DIR = Path(getattr(sys, "_MEIPASS", _exe_dir))
    _data = _exe_dir.parent / "PluginData" if _exe_dir.name.lower() == "bridge" else _exe_dir
else:
    RES_DIR = Path(__file__).resolve().parent.parent
    _data = RES_DIR
ROOT = Path(os.environ.get("KSPCHAT_DATA_DIR") or _data)   # writable data dir
try:
    ROOT.mkdir(parents=True, exist_ok=True)
except OSError:
    pass
ENV_FILE = ROOT / ".env"


def _load_dotenv(path):
    """KEY=VALUE lines from the git-ignored repo .env (API keys). Real env vars win. A .env avoids the stale-env
    problem: a key set with setx isn't seen by an already-running Steam -> KSP -> autostarted bridge."""
    try:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                k, v = k.strip().removeprefix("export ").strip(), v.strip().strip('"').strip("'")
                if k and not os.environ.get(k):
                    os.environ[k] = v
    except OSError:
        pass


_load_dotenv(ENV_FILE)
LOG_DIR = ROOT / "logs"  # bridge.log + sessions-*.jsonl
NOTES_FILE = ROOT / "playstyle_notes.md"
SETTINGS_FILE = ROOT / "bridge_settings.json"   # shared runtime settings (science watcher mode)
CRAFT_NOTES_FILE = ROOT / "craft_notes.json"    # per-craft trim / cruise notes (craft_notes.py)
CHAT_QUEUE_FILE = ROOT / "chatgpt_chat.json"     # in-game <-> ChatGPT desktop (MCP) message queue (mcp_chat.py)

HTTP_HOST = os.environ.get("KSPCHAT_HOST", "127.0.0.1")
HTTP_PORT = int(os.environ.get("KSPCHAT_PORT", "8765"))

KRPC_ADDRESS = os.environ.get("KRPC_ADDRESS", "127.0.0.1")
KRPC_RPC_PORT = int(os.environ.get("KRPC_RPC_PORT", "50000"))
KRPC_STREAM_PORT = int(os.environ.get("KRPC_STREAM_PORT", "50001"))

LMSTUDIO_URL = os.environ.get("LMSTUDIO_URL", "http://localhost:1234/v1")
LMSTUDIO_MODEL = os.environ.get("LMSTUDIO_MODEL", "")  # empty = use the model currently loaded in LM Studio
LMSTUDIO_FALLBACK_MODEL = os.environ.get("LMSTUDIO_FALLBACK_MODEL", "qwen/qwen3.5-9b")  # loaded if none is
LMSTUDIO_CONTEXT = int(os.environ.get("LMSTUDIO_CONTEXT", "16384"))
LMS_EXE = Path(os.environ.get("LMS_EXE", Path.home() / ".lmstudio" / "bin" / "lms.exe"))

OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://localhost:11434/v1")
OLLAMA_MODEL = os.environ.get("OLLAMA_MODEL", "")  # empty = first installed model

OPENAI_URL = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1")
OPENAI_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4.1-mini")

# Gemini (Google AI Studio key) via its OpenAI-compatible endpoint; key = GEMINI_API_KEY or GOOGLE_API_KEY
GEMINI_URL = os.environ.get("GEMINI_BASE_URL", "https://generativelanguage.googleapis.com/v1beta/openai")
GEMINI_MODEL = os.environ.get("GEMINI_MODEL", "gemini-flash-latest")  # alias of the current Flash model

# Free-tier clouds (OpenAI-compatible). Keys: GROQ_API_KEY, OPENROUTER_API_KEY, HF_TOKEN. Skipped cleanly if unset.
GROQ_URL = os.environ.get("GROQ_BASE_URL", "https://api.groq.com/openai/v1")
GROQ_MODEL = os.environ.get("GROQ_MODEL", "openai/gpt-oss-120b")
OPENROUTER_URL = os.environ.get("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1")
OPENROUTER_MODEL = os.environ.get("OPENROUTER_MODEL", "openrouter/free")  # random tool-capable free model
HF_URL = os.environ.get("HF_BASE_URL", "https://router.huggingface.co/v1")
HF_MODEL = os.environ.get("HF_MODEL", "openai/gpt-oss-120b:fastest")
CLOUD_RETRY_MAX_WAIT = int(os.environ.get("CLOUD_RETRY_MAX_WAIT", "30"))  # s; honour Retry-After on 429 up to this

# Any other OpenAI-compatible chat API (OpenRouter, Groq, Mistral, DeepSeek, xAI, vLLM, Anthropic compat, ...)
CUSTOM_AI_URL = os.environ.get("CUSTOM_AI_URL", "").rstrip("/")   # e.g. https://openrouter.ai/api/v1
CUSTOM_AI_MODEL = os.environ.get("CUSTOM_AI_MODEL", "")           # key (optional): CUSTOM_AI_KEY

MAX_TOOL_ROUNDS = 8          # tool-call iterations per user message
HISTORY_MESSAGES = 20        # chat turns kept per session (user+assistant)
LLM_CHAT_TIMEOUT_S = int(os.environ.get("LLM_CHAT_TIMEOUT_S", "60"))  # model HTTP timeout (/chat + flight-plan draft)
MAX_NOTES = 100              # playstyle notes cap
SESSION_LOG_MAX_BYTES = 2_000_000
SESSION_LOG_MAX_FILES = 10
