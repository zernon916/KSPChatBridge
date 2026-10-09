"""LLM backends (all OpenAI-compatible chat/completions + tools) and model discovery/selection.

  local    LM Studio  http://localhost:1234/v1   default model = whatever is loaded (else loads the fallback)
  ollama   Ollama     http://localhost:11434/v1  default model = OLLAMA_MODEL or first installed
  chatgpt  mode "api" (default): OpenAI API (OPENAI_API_KEY)  default model = OPENAI_MODEL
           mode "mcp" (opt-in, heavy token use): queued for the ChatGPT desktop app via MCP, no key (mcp_chat.py)
  gemini   Google Gemini API, OpenAI-compatible endpoint (GEMINI_API_KEY or GOOGLE_API_KEY) model = GEMINI_MODEL
  groq     Groq cloud, free tier (GROQ_API_KEY)               model = GROQ_MODEL (openai/gpt-oss-120b)
  openrouter OpenRouter ':free' models (OPENROUTER_API_KEY)   model = OPENROUTER_MODEL (openrouter/free router)
  huggingface Hugging Face Inference Providers router (HF_TOKEN) model = HF_MODEL  (small/no free credits)
  custom   any OpenAI-compatible HTTP API (CUSTOM_AI_URL [+ CUSTOM_AI_KEY] + CUSTOM_AI_MODEL): OpenRouter, Groq,
           Mistral, DeepSeek, Together, xAI, vLLM, Anthropic's OpenAI-compat endpoint, ...
  claude   Anthropic's official OpenAI-SDK compatibility layer (ANTHROPIC_API_KEY)
  grokbot  xAI Grok, OpenAI-compatible chat completions (XAI_API_KEY)
Keyed backends (KEYED) are skipped cleanly with a short "set X" message when their key/URL is missing.
"""
import json
import os
import subprocess
import urllib.request

from . import config

ALIASES = {"lmstudio": "local", "lm studio": "local", "lm": "local", "openai": "chatgpt", "gpt": "chatgpt",
           "chatgpt-mcp": "chatgpt", "mcp": "chatgpt", "chatgpt (mcp)": "chatgpt",
           "google": "gemini", "google gemini": "gemini", "gemini api": "gemini", "ai studio": "gemini",
           "groq cloud": "groq", "groq (free)": "groq", "or": "openrouter", "open router": "openrouter",
           "openrouter (free)": "openrouter", "hf": "huggingface", "hugging face": "huggingface",
           "openai-compatible": "custom", "openai compatible": "custom", "compat": "custom",
           "custom (openai-compatible)": "custom", "anthropic": "claude", "claude (soon)": "claude",
           "grok": "grokbot", "grok bot": "grokbot", "grok bot (soon)": "grokbot"}
BACKENDS = ("local", "ollama", "chatgpt", "gemini", "groq", "openrouter", "huggingface", "custom", "claude", "grokbot")
LABELS = {"local": "LM Studio", "ollama": "Ollama", "chatgpt": "ChatGPT", "gemini": "Gemini", "groq": "Groq",
          "openrouter": "OpenRouter", "huggingface": "Hugging Face",
          "custom": "Custom (OpenAI-compatible)", "claude": "Claude", "grokbot": "Grok Bot"}
CLOUD = ("chatgpt", "gemini", "groq", "openrouter", "huggingface", "custom",
         "claude", "grokbot")  # rate-limited HTTP APIs (429 backoff)


def _env(*names):
    return next((os.environ[n] for n in names if os.environ.get(n)), "")


# Keyed HTTP APIs: backend -> (base_url, key env vars, default model, model-list filter)
KEYED = {
    "chatgpt": (lambda: config.OPENAI_URL, ("OPENAI_API_KEY",), lambda: config.OPENAI_MODEL,
                lambda i: i.startswith(("gpt-", "o")) and "audio" not in i and "realtime" not in i),
    "gemini": (lambda: config.GEMINI_URL, ("GEMINI_API_KEY", "GOOGLE_API_KEY"), lambda: config.GEMINI_MODEL,
               lambda i: i.startswith("gemini") and not any(x in i for x in ("embed", "image", "tts", "audio", "live"))),
    "groq": (lambda: config.GROQ_URL, ("GROQ_API_KEY",), lambda: config.GROQ_MODEL,
             lambda i: not any(x in i for x in ("whisper", "orpheus", "guard", "tts"))),
    "openrouter": (lambda: config.OPENROUTER_URL, ("OPENROUTER_API_KEY",), lambda: config.OPENROUTER_MODEL,
                   lambda i: i.endswith(":free") or i == "openrouter/free"),
    "huggingface": (lambda: config.HF_URL, ("HF_TOKEN", "HUGGINGFACE_API_KEY"), lambda: config.HF_MODEL, lambda i: True),
    "custom": (lambda: config.CUSTOM_AI_URL, ("CUSTOM_AI_KEY",), lambda: config.CUSTOM_AI_MODEL, lambda i: True),
    # Anthropic OpenAI-SDK compatibility layer: POST /chat/completions with Bearer ANTHROPIC_API_KEY.
    "claude": (lambda: config.CLAUDE_URL, ("ANTHROPIC_API_KEY",), lambda: config.CLAUDE_MODEL,
               lambda i: i.startswith("claude")),
    # xAI Grok: OpenAI-compatible /v1/chat/completions with Bearer XAI_API_KEY.
    "grokbot": (lambda: config.XAI_URL, ("XAI_API_KEY", "GROK_API_KEY"), lambda: config.XAI_MODEL,
                lambda i: i.startswith("grok")),
}
KEY_HELP = {  # where to get a key (free tiers as of 2026-10; limits change, see README)
    "gemini": "GEMINI_API_KEY (free key at aistudio.google.com)",
    "groq": "GROQ_API_KEY (free key at console.groq.com, no card)",
    "openrouter": "OPENROUTER_API_KEY (free key at openrouter.ai; ':free' models, 50 requests/day)",
    "huggingface": "HF_TOKEN (huggingface.co/settings/tokens, 'Inference Providers' permission; free credits are tiny)",
    "claude": "ANTHROPIC_API_KEY (console.anthropic.com)",
    "grokbot": "XAI_API_KEY (console.x.ai)",
}


class BackendError(Exception):
    pass


def normalize(backend):
    b = (backend or "local").strip().lower()
    return ALIASES.get(b, b)


def normalize_model(model_id):
    """Gemini's OpenAI-compat /models returns 'models/gemini-...'; chat wants the bare id."""
    m = (model_id or "").strip()
    return m[len("models/"):] if m.startswith("models/") else m


def key_for(backend):
    spec = KEYED.get(normalize(backend))
    return _env(*spec[1]) if spec else ""


def configured():
    """Keyed backends that are usable right now (key, or URL for custom). Reported by GET /health (no secrets)."""
    out = [b for b in KEYED if b != "custom" and key_for(b)]
    if config.CUSTOM_AI_URL and config.CUSTOM_AI_MODEL:
        out.append("custom")
    return out


def _missing(backend):
    if backend == "chatgpt":
        return ("ChatGPT API mode needs an OpenAI key (OPENAI_API_KEY): paste it in AICS > Settings. No key? Pick another AI, "
                "or opt in to MCP mode (/chatgpt mcp; ChatGPT desktop answers, heavy token use).")
    if backend in KEY_HELP:
        return (f"{LABELS[backend]} needs {KEY_HELP[backend]}: paste it in AICS > Settings (API key field, Save) - "
                "no restart needed. Or pick another AI there.")
    return ("Custom backend needs CUSTOM_AI_URL (OpenAI-compatible base URL ending in /v1 or similar) and "
            "CUSTOM_AI_MODEL, plus CUSTOM_AI_KEY if the API wants one: fill them in AICS > Settings and Save.")


def _get(url, key=None, timeout=10):
    hdrs = {"User-Agent": "KSPChatBridge/1.0", "Accept": "application/json"}
    if key:
        hdrs["Authorization"] = f"Bearer {key}"
    req = urllib.request.Request(url, headers=hdrs)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def _lms_root():
    return config.LMSTUDIO_URL.rsplit("/v1", 1)[0]


def lmstudio_models():
    """[(id, loaded)] for LLMs known to LM Studio (native REST API shows load state)."""
    try:
        data = _get(_lms_root() + "/api/v0/models")["data"]
        return [(m["id"], m.get("state") == "loaded") for m in data if m.get("type") in ("llm", "vlm")]
    except Exception:
        data = _get(config.LMSTUDIO_URL + "/models")["data"]  # older LM Studio: no load state
        return [(m["id"], False) for m in data if "embed" not in m["id"]]


def lms_load(model):
    """Unload everything, then load `model` with our context length (only one model fits in VRAM)."""
    if not config.LMS_EXE.exists():
        return
    exe = str(config.LMS_EXE)
    subprocess.run([exe, "server", "start"], capture_output=True, timeout=60)
    subprocess.run([exe, "unload", "--all"], capture_output=True, timeout=120)
    subprocess.run([exe, "load", model, "--context-length", str(config.LMSTUDIO_CONTEXT), "-y"],
                   capture_output=True, timeout=600)


def list_models(backend):
    backend = normalize(backend)
    if backend == "local":
        return [f"{m}{'  (loaded)' if loaded else ''}" for m, loaded in lmstudio_models()]
    if backend == "ollama":
        return [m["id"] for m in _get(config.OLLAMA_URL + "/models")["data"]]
    if backend in KEYED:
        url, _, _, keep = KEYED[backend]
        key = key_for(backend)
        if (backend != "custom" and not key) or not url():
            raise BackendError(_missing(backend))
        ids = [normalize_model(m["id"]) for m in _get(url() + "/models", key or None)["data"]]
        return sorted(i for i in ids if keep(i))
    raise BackendError(f"Unknown backend '{backend}'. Use one of {BACKENDS}.")


def resolve(backend, override=None):
    """Return (base_url, api_key, model) for a backend, loading/choosing a model as needed."""
    backend = normalize(backend)
    if backend in KEYED:
        url, _, model, _ = KEYED[backend]
        key = key_for(backend)
        if (backend != "custom" and not key) or not url() or not (override or model()):
            raise BackendError(_missing(backend))
        return url().rstrip("/"), key or None, normalize_model(override or model())
    if backend == "ollama":
        model = override or config.OLLAMA_MODEL
        if not model:
            try:
                models = list_models("ollama")
            except Exception:
                raise BackendError("Can't reach Ollama at " + config.OLLAMA_URL + " (is 'ollama serve' running?).")
            if not models:
                raise BackendError("Ollama has no models. Run e.g. 'ollama pull qwen3:8b'.")
            model = models[0]
        return config.OLLAMA_URL, None, model
    if backend == "local":
        want = override or config.LMSTUDIO_MODEL  # empty = use whatever is loaded
        try:
            models = lmstudio_models()
        except Exception:
            models = []
        loaded = [m for m, l in models if l]
        if want:
            if want not in loaded:
                lms_load(want)
            return config.LMSTUDIO_URL, None, want
        if loaded:
            return config.LMSTUDIO_URL, None, loaded[0]
        lms_load(config.LMSTUDIO_FALLBACK_MODEL)
        return config.LMSTUDIO_URL, None, config.LMSTUDIO_FALLBACK_MODEL
    raise BackendError(f"Unknown backend '{backend}'. Use one of {', '.join(BACKENDS)}.")