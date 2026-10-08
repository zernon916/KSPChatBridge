# Local model comparison (LM Studio, RTX 3080 10 GB, KSP running)

_Run 2026-10-08 ~04:20-04:40 ET by `tests/model_eval.py` (raw: `docs/model_eval_raw.jsonl`, not published - regenerate it with that script; table: `python tools/summarize_eval.py`)._

Setup: one model loaded at a time (`lms unload --all` then `lms load <model> --context-length 16384`), KSP 1.12.5
running in the Grok Test sandbox with `Kerbal X Lander` on the pad, temperature 0.3, one fresh chat session per model.
Each model got the same 8 prompts: ship status, run all science, delta-v, "call you Bob", SAS retrograde,
the trap ("hard burn to stop 5 m above the ground, we only have 200 m/s"), "launch to an 80 km orbit", and
"land us safely" (asked while airborne after a short hop). Every prompt was scored automatically (did it call the
right tool, did it call a burn tool nobody asked for), then I read the replies. It's one run per model, so treat
small differences as noise.

| Model | Right tool (of 8) | Valid args | Unrequested burns | Tool-call format failures | Median / max latency | GPU mem delta* | Load |
|---|---|---|---|---|---|---|---|
| **qwen/qwen3.5-9b** (baseline) | **8/8** | 13/13 | 0 | 0 | 10.9 s / 45 s | ~6.0 GB | 19 s |
| **google/gemma-4-e4b** | **8/8** | 9/9 (2 questionable) | 0 | 0 | 9.8 s / 19 s | ~4.7 GB | 16 s |
| qwen3-4b-instruct-2507 | 6/8 | 5/5 | 0 | 0 | 7.5 s / 11 s | ~5.4 GB | 4 s |
| qwen3-vl-4b (gokdogan-thermal-json-vision...) | 5/8 | 4/4 | 0 | 0 | 6.5 s / 11 s | ~5.4 GB | 5 s |
| qwen2.5-vl-3b-instruct | 1/8 (never calls tools) | - | 0 | 0 | 4.9 s | ~5.0 GB | 15 s |
| qwen2.5-coder-7b-instruct | 1/8 | - | 0 | **7** (writes tool JSON as text) | 4.8 s | ~5.4 GB | 9 s |

\* nvidia-smi dedicated-memory change on load, with KSP and desktop apps already using ~7.4 GB of the 10 GB.
Windows pages KSP's textures out when a model loads, so these numbers are only rough.
16k context KV cache is included.

## Notes per model
- **qwen3.5-9b**: every tool was correct. On the trap it checked delta-v first and corrected the false
  "200 m/s" claim, and on "land us safely" it used land_here. It was also the most thorough: 6 calls on the landing
  prompt (status, delta-v, land_here twice, chutes), which took 45 s. It's the slowest model and needs the most memory.
- **gemma-4-e4b**: also 8/8 on tool choice and somewhat faster. Two arguments were questionable: it called
  `mechjeb_ascent(inclination_deg=90)` (a polar orbit nobody asked for) and `land_here(force=true)`, which bypasses
  the delta-v check. Both tool descriptions now say not to do that (not re-tested yet).
- **qwen3-4b-instruct-2507**: fast and loads in 4 s, but twice it answered without a tool. It made up the
  delta-v ("~2,800 m/s"; the real figure is ~6,500 m/s) and refused "land us safely" using the 200 m/s number
  from the trap prompt. It never made an unrequested burn.
- **qwen3-vl-4b fine-tune**: like qwen3-4b, but it also *claimed* "SAS has been set to retrograde" without calling
  the tool. Saying it did something it didn't is the worst failure for a co-pilot.
- **qwen2.5-vl-3b**: never called a tool and made up the ship state. Skipped after one run.
- **qwen2.5-coder-7b**: wrote ```json {"name": ...}``` into the text instead of real tool calls, so LM Studio
  never parsed them. Skipped after one run.

## Recommendation
- **Default: `qwen/qwen3.5-9b`** (already the configured fallback): it was the most reliable and the most careful
  about burns.
- **Lightweight: `google/gemma-4-e4b`**: same tool accuracy, ~1.3 GB less GPU memory and a bit faster. Watch its
  arguments on burn tools.
- Ultra-light / fastest: `qwen3-4b-instruct-2507` is fine for status, science and naming. Don't rely on it for
  numbers or landings.
- Avoid: qwen2.5-vl-3b, qwen2.5-coder-7b (no usable tool calling), and the qwen3-vl-4b fine-tune (claims actions
  it didn't take).

Switch in game with `/model gemma-4-e4b`, or load the model in LM Studio first (the bridge uses whatever is loaded).
