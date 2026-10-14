# FrameFX Assistant (agent): design

**Goal:** a built-in agentic AI in the FrameFX app that chats about anything, searches the web, launches allowed apps, and controls FrameFX (presets, settings, start/stop). It runs **fully local** and is always called "FrameFX Assistant" in the UI.

## Components
| Piece | File | What it does |
|---|---|---|
| Local model | (downloaded) `models/framefx-assistant.gguf` | Qwen2.5-**3B**-Instruct, Q4_K_M, 1.93 GB (Apache-2.0, see LICENSES/). Never named in the UI or the system prompt. |
| Server | `LlamaServerHost.cs` | Runs the bundled `llama-server.exe` (llama.cpp **Vulkan** build, AMD + NVIDIA) on 127.0.0.1 and a random port, with `--jinja` for native tool calls. Waits for `/health` and walks a fallback ladder if a start fails. |
| Context sizing | `ContextSizer.cs` | Picks the biggest context that fits the free VRAM (≤128k), the KV-cache type (q8_0, then q4_0), GPU layers, and YaRN. |
| Protocol | `ChatProtocol.cs` | Streaming OpenAI `/v1/chat/completions`. Joins tool-call fragments, reads usage, and falls back to parsing `<tool_call>{…}</tool_call>` if the server leaves those tags in the text. |
| Agent loop | `AgentLoop.cs` | history + tools → stream → run tool calls → append results → repeat. **Step limit 8.** The last step is sent without tools, which forces an answer. Summarizes history near the window. |
| Budget | `TokenBudget.cs` | Context window (per request) + **256k session budget** (prompt + completion summed over the chat). |
| Tools | `ToolRegistry.cs`, `Tools/*` | Registry runs Describe → **Confirm** → Execute. Tools can't skip the confirm step. |
| UI | `FrameFX.Agent.UI/` | `AgentChatForm` (streamed chat, a "used tool" row per call with details, Stop, New chat, token status), `ConfirmDialog`, `DxgiVramProbe`. |
| App glue | `FrameFX.Agent.UI/AppIntegration/` | `PresetAiEngine` (Beta7 ChatSession, model-first), `FrameFxHostAdapter` (MainForm), `AgentBootstrap`. These get copied into the app project. |

## Context: model numbers and the honest per-GPU table
Qwen2.5-3B has 36 layers and GQA with 2 KV heads × 128, so the KV cache is small: **f16 36 KB/token, q8_0 19.1 KB/token, q4_0 10.1 KB/token**. At 128k that's 4.5 / 2.4 / 1.3 GB. Native context is **32k**. Above that we turn on YaRN (`--rope-scaling yarn --rope-scale 2|4 --yarn-orig-ctx 32768`), the method the Qwen docs give for long context. It's static scaling, so it only goes on when context > 32k.

Planner output (estimate = weights + KV + compute buffer ≈ 350 MB + 3 KB/token). The default reserve is 0.9 GB; the "gaming" reserve, used when a game + FrameFX are already on the GPU, is 3 GB:

| GPU | Idle desktop | While a game + FrameFX run |
|---|---|---|
| RX 9070 XT 16 GB | **128k**, q8 cache, full offload, ~4.9 GB | 128k, q8 |
| 8–12 GB cards | 128k, q8 | 128k, q8 |
| RTX 3060 Laptop 6 GB | **128k**, q8, full offload, ~4.9 GB | **~48–64k**, q4 cache |
| 4 GB | 64k, q4 | 16k, partial offload |
| 2–3 GB | 16k, partial offload | CPU only, 4k |

**Honest caveats:**
- These are **estimates, not measured.** Nothing has run on Mica's GPUs yet. If the real compute buffer is bigger, the server fails to load and the ladder steps down on its own (smaller context → q4 → fewer layers → f16 without flash attention at 8k/4k).
- **Fitting 128k isn't the same as using 128k well.** Qwen officially trained and validated the 3B at 32k. YaRN to 128k works but recall gets worse past about 32k, and static YaRN slightly hurts short prompts. Recommendation: let the planner allocate, but the chat summarizes at 70% of the window anyway.
- **Speed (rough expectations to verify):** generation ~60–90 tok/s on a 9070 XT and ~35–60 tok/s on a 3060 Laptop at short context. Prompt processing is the slow part at long context: filling 100k+ tokens can take **1–3+ minutes** on the 3060. Running alongside a game costs game FPS and VRAM. Recommend gaming mode = smaller context.
- A quantized V cache needs flash attention. If a Vulkan driver/build lacks it, the server fails and the last ladder rung (f16, no `-fa`) catches it.
- **3B quality:** good for chat, short explanations, summarizing a fetched page, and simple tool chains. It's weaker than 7B at multi-step planning and makes factual mistakes without web_search. The system prompt tells it to use search for facts.

## Token limits (Mica: "256k token limit, 128k window")
- **Window** = `ContextPlan.ContextTokens` (≤128k, per request).
- **Session budget** = 256k tokens total (`AgentSettings.SessionTokenLimit`, default 262,144), counted from the server's `usage` (an estimate if that's missing). When it runs out the chat says so and the user starts a new one.
- **Summarization:** when history + reply reserve > 70% of the window, everything except the system prompt and the last ~6 messages is folded into one "Summary of the earlier conversation" system message (the model writes it, ≤250 words). Tool results are never split from their calls.

## Tools and safety
| Tool | Side effect | Confirm shows |
|---|---|---|
| `web_search` | none | — (DuckDuckGo HTML by default; SearXNG URL, Brave or Tavily key optional) |
| `fetch_page` | none | — (http/https only; blocks localhost/private/link-local incl. after DNS; 2 MB cap; text only) |
| `launch_app` | starts a program | program name, full .exe path, "Arguments: none", whether it gets added to the allowlist |
| `make_preset` | none | — (Beta7 ONNX model-first pipeline, returns a proposal) |
| `apply_preset` | saves profile | game, **Before** and **After** settings, anti-cheat note |
| `set_setting` | saves profile | game, key → value, Before/After. Whitelisted keys only |
| `start_framefx` / `stop_framefx` | starts/stops output | game + settings used |
| `get_framefx_state` | none | — |

Rules: **no shell tool exists.** `launch_app` only starts allow-listed full-path `.exe`s, with no arguments and no shell, and refuses cmd/powershell/wscript/mshta/rundll32/regsvr32/reg/wsl/msiexec. Only the user's own file picker adds to the allowlist. Confirm defaults to Cancel, Enter doesn't confirm, closing the dialog = no, and a decline is reported to the model as "don't retry". Anti-cheat games: frame gen can't be turned on (tool refuses, and MainForm.ApplyAssistantProfile also forces it off). Web text is untrusted, but it can only reach read-only tools by itself. Every side effect still needs a human click.

## Out of scope / later
GPU-accelerated preset model, agent memory across sessions, voice, running tools while a fullscreen game has focus (the Confirm dialog needs the desktop), DPAPI-encrypting the optional search API key.
