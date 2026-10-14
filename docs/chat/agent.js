// WyneAI agent: LLM plans + calls tools (Qwen <tool_call> JSON protocol, with repair), deterministic fast path/fallback.
// Only the Beta7 pipeline (Chat.turn via make_preset/change_preset) ever sets preset fields.
import { GAMES, GPUS } from "./kb.js";
import { Chat, WHY } from "./presetai.js";
import { gamesIn, gpusIn, COST, TIER, norm } from "./assistant.js";

export const TOOLS = [
  { name: "make_preset", description: "Start a NEW FrameFX preset from the user's description (game, GPU, fps now/target, resolution, laptop, preferences). Replaces any current preset.", parameters: { request: "string: the user's words describing game/GPU/fps, e.g. 'Cyberpunk on a 3060, 45 fps, want 120'" } },
  { name: "change_preset", description: "Change the CURRENT preset with a follow-up, e.g. 'what about 4K', 'make it quieter', 'turn frame gen off', 'actually I have a 4070', 'add ray tracing'.", parameters: { request: "string: the change in plain words" } },
  { name: "lookup_game", description: "Facts about a game from the FrameFX knowledge base (exe, how heavy, anti-cheat, competitive, HUD).", parameters: { game: "string" } },
  { name: "lookup_gpu", description: "Facts about a GPU from the knowledge base (tier, rough performance score, vendor, FSR 4 support).", parameters: { gpu: "string" } },
  { name: "compare_gpus", description: "Compare two GPUs' rough performance.", parameters: { gpu_a: "string", gpu_b: "string" } },
  { name: "explain_preset", description: "Explain the current preset's settings and the reasons behind them.", parameters: {} },
  { name: "export_preset", description: "Export the current preset's FrameFX GameProfile JSON: copy to clipboard or download a file.", parameters: { mode: "'copy' or 'download'" } },
  { name: "reset_session", description: "Clear the current preset and start over.", parameters: {} },
  { name: "web_search", description: "Search the web for current or factual information. Returns titles, URLs and snippets. Cite the URLs you use.", parameters: { query: "string" } },
  { name: "fetch_page", description: "Fetch the readable text of a web page (e.g. a search result) to read details. Cite its URL.", parameters: { url: "string" } },
  { name: "create_file", description: "Create a file for the user to save. For ONE file pass filename + content (+ optional mime). For SEVERAL pass files:[{filename,content}] and they are delivered together as a zip. Put the COMPLETE file content in the call. Max ~2 MB total. Use this whenever the user asks to make/save a file, script, document or code.", parameters: { filename: "string (single file)", content: "string (single file full text)", files: "array of {filename, content} for multiple files" } },
];
export let API_BASE = "";
export function setApiBase(chatUrl) { API_BASE = chatUrl.replace(/\/api\/chat$/, ""); }
async function webApi(body) { const r = await fetch(API_BASE + "/api/search", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  if (r.status === 429) return { ok: false, error: "rate limited, try again in a minute" }; return r.json(); }
const NAMES = new Set(TOOLS.map(t => t.name));
// desktop: register extra tools (name, description, JSON-schema properties, required)
export function registerTools(defs) { for (const d of defs) { TOOLS.push({ name: d.name, description: d.description, parameters: d.props }); PROPS[d.name] = d.props; REQ[d.name] = d.required || Object.keys(d.props); NAMES.add(d.name); }
  TOOL_SCHEMAS.length = 0; TOOL_SCHEMAS.push(...buildSchemas()); }

// ---------- parser ----------
function scanJSON(s, i) { // balanced {...} starting at s[i]=='{'
  let d = 0, q = false, e = false;
  for (let j = i; j < s.length; j++) { const c = s[j];
    if (q) { if (e) e = false; else if (c === "\\") e = true; else if (c === '"') q = false; continue; }
    if (c === '"') q = true; else if (c === "{") d++; else if (c === "}" && --d === 0) return s.slice(i, j + 1); }
  return null; }
function repairJSON(t) { return t.replace(/[“”]/g, '"').replace(/,\s*([}\]])/g, "$1").replace(/([{,]\s*)([A-Za-z_]\w*)\s*:/g, '$1"$2":').replace(/:\s*'([^']*)'/g, ': "$1"'); }
function normCall(o) { if (!o || typeof o !== "object") return null;
  if (o.function && typeof o.function === "object") o = { name: o.function.name, arguments: o.function.arguments };
  const name = o.name || o.tool || o.tool_name; let args = o.arguments ?? o.args ?? o.parameters ?? o.input ?? {};
  if (typeof args === "string") { try { args = JSON.parse(args); } catch { args = { request: args }; } }
  return name ? { name: String(name), arguments: args || {} } : null; }
export function parseToolCalls(text) {
  const calls = [], errors = []; let rest = text;
  const tryParse = raw => { for (const t of [raw, repairJSON(raw)]) { try { return JSON.parse(t); } catch {} } return undefined; };
  const blocks = [...text.matchAll(/<tool_call>([\s\S]*?)(<\/tool_call>|$)/g)];
  let cands = blocks.map(m => m[1]);
  if (!blocks.length) { const fence = [...text.matchAll(/```(?:json)?\s*([\s\S]*?)```/g)].map(m => m[1]); cands = fence.length ? fence : [];
    if (!cands.length && /"name"\s*:/.test(text)) { const i = text.indexOf("{"); if (i >= 0) cands = [text.slice(i)]; } }
  for (const c of cands) { let k = c.indexOf("{"); if (k < 0) { errors.push("no JSON object"); continue; }
    while (k >= 0 && k < c.length) { const raw = scanJSON(c, k); if (!raw) { errors.push("unbalanced JSON"); break; }
      const v = tryParse(raw); const list = Array.isArray(v) ? v : [v];
      for (const o of list) { const call = normCall(o); if (!call) errors.push("not a tool call: " + raw.slice(0, 80)); else if (!NAMES.has(call.name)) errors.push("unknown tool " + call.name); else calls.push(call); }
      k = c.indexOf("{", k + raw.length); } }
  for (const m of blocks) rest = rest.replace(m[0], "");
  if (!blocks.length && calls.length) rest = "";
  return { calls, errors, text: rest.trim() }; }

// ---------- tools ----------
const findGame = s => gamesIn(norm(String(s || "")))[0] || null, findGpu = s => gpusIn(norm(String(s || "")))[0] || null;
export function makeTools(assistant, ui = {}) {
  const chat = assistant.chat;
  const card = () => chat.last ? { game: chat.state().game, settings: Chat.settings(chat.last) } : null;
  const presetResult = (reply, before) => ({ ok: true, reply, preset_changed: chat.last !== null && chat.last !== before, preset: card() });
  const run = async (req, fresh) => { const before = fresh ? null : chat.last; if (fresh) chat.reset();
    const reply = await chat.turn(String(req || "")); const r = presetResult(reply, before);
    if (!r.preset_changed && !chat.last) r.ok = false; return r; };
  return {
    make_preset: a => run(a.request, true),
    change_preset: a => chat.last ? run(a.request, false) : { ok: false, error: "No current preset. Use make_preset first." },
    lookup_game: a => { const g = findGame(a.game); if (!g) return { ok: false, error: `Unknown game "${a.game}". Known: ${GAMES.length} games.` };
      return { ok: true, name: g[0], exe: g[1], load: COST[g[3]], hud_heavy: !!g[4], competitive: !!g[5], anticheat: !!g[6], framegen_allowed: !(g[5] || g[6]) }; },
    lookup_gpu: a => { const p = findGpu(a.gpu); if (!p) return { ok: false, error: `Unknown GPU "${a.gpu}".` };
      return { ok: true, name: p[0], tier: TIER(p[2]), score: p[2], score_note: "rough relative raster score, RTX 4090 = 88", vendor: p[3], fsr4: p[4] === "rx9000" }; },
    compare_gpus: a => { const x = findGpu(a.gpu_a), y = findGpu(a.gpu_b); if (!x || !y) return { ok: false, error: "Unknown GPU " + (!x ? a.gpu_a : a.gpu_b) };
      const [w, l] = x[2] >= y[2] ? [x, y] : [y, x]; const pct = Math.round((w[2] / l[2] - 1) * 100);
      return { ok: true, faster: pct < 5 ? "about equal" : w[0], percent_faster: pct, scores: { [x[0]]: x[2], [y[0]]: y[2] } }; },
    explain_preset: () => { if (!chat.last) return { ok: false, error: "No current preset." }; const o = chat.last;
      return { ok: true, preset: card(), reasons: o.why.split(",").map(w => w === "user_fg_off" ? "Frame gen off because the user asked." : WHY[w]).filter(Boolean),
        framegen_math: o.fg !== "0" ? `estimated base ~${o._base} fps, target ${o._target}, so ${o.m}x` : null }; },
    export_preset: async a => { const pj = chat.profileJson(); if (!pj) return { ok: false, error: "No current preset to export." };
      const json = JSON.stringify(pj, null, 2), mode = a.mode === "download" ? "download" : "copy"; let done = false;
      try { done = !!(await (mode === "download" ? ui.download : ui.copy)?.(json, Object.keys(pj)[0])); } catch {}
      return { ok: true, mode, done, note: done ? (mode === "download" ? "File downloaded." : "Copied to clipboard.") : "Export ready; user can use the Copy/Download buttons.", exe: Object.keys(pj)[0], json }; },
    reset_session: () => { chat.reset(); return { ok: true, reply: "Session cleared." }; },
    web_search: a => webApi({ query: String(a.query || "") }),
    fetch_page: a => webApi({ url: String(a.url || "") }),
    create_file: a => ui.createFile ? ui.createFile(a) : { ok: false, error: "file creation isn't available here" },
  }; }
export async function runTool(tools, call) { const f = tools[call.name]; if (!f) return { ok: false, error: "unknown tool" };
  try { return await f(call.arguments || {}); } catch (e) { return { ok: false, error: String(e.message || e) }; } }

// ---------- deterministic planner (fast path + fallback) ----------
const SPLIT = /\s*(?:,?\s*(?:and )?then\b|;|\.\s+|,\s*(?:and\s+)?(?=(?:make|turn|switch|add|set|change|export|copy|download|explain|compare|reset|kill|bring|use|what about|actually)\b)|\s+and\s+(?=(?:export|copy|download|explain|reset)\b))\s*/i;
export function planDeterministic(text, hasPreset) {
  const parts = text.split(SPLIT).map(s => s.trim()).filter(Boolean); const steps = []; let have = hasPreset;
  for (const p of parts) { const t = norm(p);
    if (/^(export|copy|download|save)\b|\b(export|copy|download) (it|the preset|the json)\b/.test(t)) { steps.push({ name: "export_preset", arguments: { mode: /download|save|file/.test(t) ? "download" : "copy" } }); continue; }
    if (/^(explain|why)\b|explain (it|the preset|my preset)/.test(t)) { steps.push({ name: "explain_preset", arguments: {} }); continue; }
    if (/^(reset|start over|clear)\b/.test(t)) { steps.push({ name: "reset_session", arguments: {} }); have = false; continue; }
    const gp = gpusIn(t); if (gp.length >= 2 && /compare|vs|versus|better|faster/.test(t)) { steps.push({ name: "compare_gpus", arguments: { gpu_a: gp[0][0], gpu_b: gp[1][0] } }); continue; }
    if (gamesIn(t).length && (!have || /set up|setup|make|preset for|new|switch to|^\w/.test(t)) && !/^(what about|actually|make it)\b/.test(t)) { steps.push({ name: have ? "change_preset" : "make_preset", arguments: { request: p } }); have = true; continue; }
    if (have) { steps.push({ name: "change_preset", arguments: { request: p } }); continue; }
    steps.push({ name: "make_preset", arguments: { request: p } }); have = true; }
  return steps; }
export const isPlainPreset = (text, assistant) => !assistant.route(text) && !/\b(then|export|copy|download|explain|compare|why|what is|whats|who|how)\b/i.test(text);

// ---------- agent loop ----------
export function systemPrompt(presetCtx) {
  return `You are WyneAI, the AI assistant built into FrameFX (Universal FrameFX), a Windows app adding frame generation, upscaling (CSR 1.3/2.0/AI, FSR 1-4, XeSS), SSGI, RT lighting and CPU boost to games via per-game presets. You were made for FrameFX; if asked who/what model you are or who made you, say you are WyneAI, made for FrameFX, and never name another model or company. You can chat about anything, but say so instead of guessing about recent events.

You can use tools. NEVER invent preset values yourself: presets are only created/changed by make_preset/change_preset. For multi-step requests, call tools one after another. To call a tool, output ONLY:
<tool_call>
{"name": "<tool>", "arguments": {...}}
</tool_call>
You may put several <tool_call> blocks in one reply. After you get <tool_response> results, either call more tools or answer the user in plain friendly text (no JSON). Keep final answers short.

Tools:
${TOOLS.map(t => `- ${t.name}: ${t.description} Arguments: ${JSON.stringify(t.parameters)}`).join("\n")}

${presetCtx}`; }
export class Agent {
  constructor(assistant, llm, ui = {}, maxSteps = 5) { this.a = assistant; this.llm = llm; this.ui = ui; this.max = maxSteps; this.tools = makeTools(assistant, ui); this.history = []; }
  async execute(steps, on) { const results = [];
    for (const c of steps) { const r = await runTool(this.tools, c); results.push({ call: c, result: r }); on.step?.(c, r); } return results; }
  summarize(results) { // deterministic final text (fallback / basic mode)
    const out = []; for (const { call, result: r } of results) {
      if (!r.ok) out.push(`${call.name}: ${r.error || "no change (" + (r.reply || "") + ")"}`);
      else if (r.reply) out.push(r.reply);
      else if (call.name === "explain_preset") out.push(["Why these settings:", ...(r.framegen_math ? ["Frame gen: " + r.framegen_math + "."] : []), ...r.reasons].join("\n"));
      else if (call.name === "export_preset") out.push(r.note);
      else if (call.name === "lookup_game") out.push(`${r.name}: ${r.load} to run${r.anticheat ? ", anti-cheat (frame gen stays off)" : r.competitive ? ", competitive (frame gen off)" : ""}.`);
      else if (call.name === "lookup_gpu") out.push(`${r.name}: ${r.tier} (score ${r.score})${r.fsr4 ? ", supports FSR 4" : ""}.`);
      else if (call.name === "compare_gpus") out.push(r.faster === "about equal" ? "They're about equal." : `${r.faster} is ~${r.percent_faster}% faster.`);
      else if (call.name === "create_file") out.push(r.file ? `Created ${r.file.name} (${r.file.size} bytes).` : ""); }
    return out.join("\n\n"); }
  async deterministic(text, on) { const steps = planDeterministic(text, !!this.a.chat.last); const res = await this.execute(steps, on); const t = this.summarize(res); this.remember(text, t); return { text: t, results: res, mode: "fast" }; }
  remember(u, a) { this.history.push({ role: "user", content: String(u).slice(0, 100000) }, { role: "assistant", content: String(a ?? "").slice(0, 100000) }); // token window scaled per tier (≈4 chars/token), max 500 messages
    const tok = m => Math.ceil(String(m.content || "").length / 4) + 4; let t = this.history.reduce((s, m) => s + tok(m), 0); const budget = this.ctxBudget || 120000;
    while (this.history.length > 2 && (t > budget || this.history.length > 498)) { t -= tok(this.history[0]) + tok(this.history[1]); this.history.splice(0, 2); } }
  async run(text, on = {}) {
    if (!this.llm || isPlainPreset(text, this.a) && planDeterministic(text, !!this.a.chat.last).length === 1 && (await this.probePreset(text))) {
      if (this._probed) { const r = this._probed; this._probed = null; on.step?.(r.call, r.result); const t = this.summarize([r]); this.remember(text, t); return { text: t, results: [r], mode: "fast" }; }
      return this.deterministic(text, on); }
    const msgs = [{ role: "system", content: systemPrompt(this.a.presetContext()) }, ...this.history, { role: "user", content: text }];
    const all = []; let repaired = false;
    try {
      for (let step = 0; step < this.max; step++) {
        const out = await this.llm(msgs, on.token); const p = parseToolCalls(out);
        if (!p.calls.length && p.errors.length && !repaired) { repaired = true; msgs.push({ role: "assistant", content: out }, { role: "user", content: `Your tool call was invalid (${p.errors.join("; ")}). Reply again with valid JSON exactly like <tool_call>{"name": "make_preset", "arguments": {"request": "..."}}</tool_call>, or answer in plain text.` }); on.retry?.(); continue; }
        if (!p.calls.length) { const t = p.text || out.trim(); this.remember(text, t); return { text: t, results: all, mode: "llm" }; }
        on.clear?.(); const res = await this.execute(p.calls, on); all.push(...res);
        msgs.push({ role: "assistant", content: out }, { role: "user", content: res.map(({ call, result }) => `<tool_response>\n${JSON.stringify({ name: call.name, result: call.name === "export_preset" ? { ...result, json: "(omitted)" } : result })}\n</tool_response>`).join("\n") });
      }
      const t = this.summarize(all) || "I ran out of steps."; this.remember(text, t); return { text: t, results: all, mode: "llm-maxsteps" };
    } catch (e) { on.error?.(e); if (all.length) { const t = this.summarize(all); this.remember(text, t); return { text: t, results: all, mode: "llm-error" }; } return this.deterministic(text, on); } }
  async probePreset(text) { // fast path: plain preset request handled by Beta7 directly if it changes the preset
    const fresh = !this.a.chat.last; const r = await this.a.tryPreset(text); if (!r) return false;
    this._probed = { call: { name: fresh ? "make_preset" : "change_preset", arguments: { request: text } }, result: { ok: true, reply: r.text, preset_changed: r.preset, preset: this.a.chat.last ? { game: this.a.chat.state().game, settings: Chat.settings(this.a.chat.last) } : null } };
    return true; }
}

// ---------- native OpenAI-style tools (cloud model) ----------
const REQ = {};
const PROPS = {
  make_preset: { request: { type: "string", description: "Verbatim user words (no added details) for game, GPU, fps now/target, resolution, laptop, preferences" } },
  change_preset: { request: { type: "string", description: "The change in plain words, e.g. 'make it quieter', 'what about 4K', 'turn frame gen off'" } },
  lookup_game: { game: { type: "string" } }, lookup_gpu: { gpu: { type: "string" } },
  compare_gpus: { gpu_a: { type: "string" }, gpu_b: { type: "string" } }, explain_preset: {},
  export_preset: { mode: { type: "string", enum: ["copy", "download"] } }, reset_session: {}, web_search: { query: { type: "string" } }, fetch_page: { url: { type: "string" } },
  create_file: { filename: { type: "string", description: "name for a single file, e.g. hello.py" }, content: { type: "string", description: "the single file complete text" }, mime: { type: "string" }, files: { type: "array", description: "for multiple files (zipped)", items: { type: "object", properties: { filename: { type: "string" }, content: { type: "string" } }, required: ["filename", "content"] } } } };
REQ.create_file = [];
const buildSchemas = () => TOOLS.map(t => ({ type: "function", function: { name: t.name, description: t.description,
  parameters: { type: "object", properties: PROPS[t.name], required: REQ[t.name] || Object.keys(PROPS[t.name]).filter(k => k !== "mode") } } }));
export const TOOL_SCHEMAS = buildSchemas();
export function nativeSystemPrompt(presetCtx) {
  return `You are WyneAI, the AI assistant built into FrameFX (Universal FrameFX), a Windows app that adds frame generation, upscaling (CSR 1.3/2.0/AI, FSR 1-4, XeSS), SSGI, RT lighting and CPU boost to games through per-game presets. You can chat helpfully about anything (general knowledge, tech, games), concisely and in a friendly tone. You can use web_search (and fetch_page for details) for current events, news, prices, recent releases or anything you're unsure of; usually one search is enough: answer from the result titles and snippets, and only use fetch_page when you truly need more detail. Cite sources as markdown links [title](url). Don't invent URLs.
Use the tools for FrameFX work. NEVER invent preset values: presets are only created or changed by make_preset / change_preset (pass the user's exact words for that step, copied verbatim; never add details like resolution, fps or preferences the user didn't say). For multi-step requests call the tools in order (e.g. make_preset, then change_preset, then export_preset). Use lookup_game / lookup_gpu / compare_gpus for knowledge-base facts and explain_preset to explain the current preset. Frame generation always stays off for anti-cheat and competitive games. When the user asks you to make or save a file, script, document or code, call create_file with the complete content (use the files array for several files; they come back as a download or zip). After tools finish, give a short summary in markdown (headings, lists, tables and fenced code blocks are supported) using only values the tools returned; don't paste raw JSON.
${presetCtx}`; }
Agent.prototype.runNative = async function (text, on = {}) {
  if (isPlainPreset(text, this.a) && planDeterministic(text, !!this.a.chat.last).length === 1 && (await this.probePreset(text))) {
    const r = this._probed; this._probed = null; on.step?.(r.call, r.result); const t = this.summarize([r]); this.remember(text, t); return { text: t, results: [r], mode: "fast" }; }
  const msgs = [{ role: "system", content: nativeSystemPrompt(this.a.presetContext()) + (this.extraPrompt ? "\n\n" + this.extraPrompt() : "") }, ...this.history, { role: "user", content: text }];
  const all = [];
  try {
    for (let step = 0; step < this.max; step++) {
      if (this.stopped) { const t = (this.summarize(all) ? this.summarize(all) + "\n\n" : "") + "Stopped."; this.remember(text, t); return { text: t, results: all, mode: "stopped" }; }
      const r = await this.llm(msgs, TOOL_SCHEMAS, on); // -> {content, tool_calls}
      const calls = (r.tool_calls || []).filter(c => c?.function?.name);
      if (!calls.length) { const t = (r.content || "").trim() || this.summarize(all); this.remember(text, t); return { text: t, results: all, mode: "cloud" }; }
      on.clear?.(); msgs.push({ role: "assistant", content: r.content || null, tool_calls: calls.map(c => ({ id: c.id, type: "function", function: { name: c.function.name, arguments: c.function.arguments || "{}" } })) });
      for (const c of calls) { let args = {}; try { args = JSON.parse(c.function.arguments || "{}"); } catch { try { args = JSON.parse(repairJSON(c.function.arguments || "{}")); } catch { args = {}; } }
        if (this.stopped) break;
        const call = { name: c.function.name, arguments: args }; const res = NAMES.has(call.name) ? await runTool(this.tools, call) : { ok: false, error: "unknown tool" };
        all.push({ call, result: res }); on.step?.(call, res);
        msgs.push({ role: "tool", tool_call_id: c.id, content: JSON.stringify(call.name === "export_preset" ? { ...res, json: "(omitted)" } : call.name === "create_file" ? { ok: res.ok, error: res.error, file: res.file } : res).slice(0, 6000) }); } }
    const t = this.summarize(all) || "I ran out of steps."; this.remember(text, t); return { text: t, results: all, mode: "cloud-maxsteps" };
  } catch (e) { on.error?.(e); if (all.length) { const t = this.summarize(all); this.remember(text, t); return { text: t, results: all, mode: "cloud-error" }; } throw e; } };
export function cloudLLM(url = "/api/chat", getSignal = () => undefined) {
  // One streamed request. `base` is text already shown (for stitched continuations) so on.token stays cumulative.
  async function once(messages, { noSearch, continuation, maxTokens, base, on }) {
    const res = await fetch(url, { method: "POST", signal: getSignal(), headers: { "content-type": "application/json" }, body: JSON.stringify({ messages, tools: once._tools, max_tokens: maxTokens || 32768, no_search: noSearch, continuation }) });
    if (!res.ok) { let m = res.status; try { m = (await res.json()).error || m; } catch {} const e = new Error(String(m)); e.status = res.status; throw e; }
    const rd = res.body.getReader(), dec = new TextDecoder(); let buf = "", content = "", reasoning = ""; const tc = []; let finish = null, sawDone = false;
    for (;;) { const { value, done } = await rd.read(); if (done) break; buf += dec.decode(value, { stream: true }); let i;
      while ((i = buf.indexOf("\n\n")) >= 0) { const ev = buf.slice(0, i).trim(); buf = buf.slice(i + 2); if (!ev.startsWith("data:")) continue; const d = ev.slice(5).trim(); if (d === "[DONE]") { sawDone = true; continue; }
        let j; try { j = JSON.parse(d); } catch { continue; }
        if (j.type === "search") { on.search?.(j); continue; }
        if (j.finish_reason) finish = j.finish_reason;
        if (j.reasoning) { reasoning += j.reasoning; on.reasoning?.(reasoning); }
        if (j.content) { content += j.content; if (!tc.length) on.token?.(base + content); }
        for (const t of j.tool_calls || []) { const k = t.index ?? tc.length; tc[k] ||= { id: "", function: { name: "", arguments: "" } };
          if (t.id) tc[k].id = t.id; if (t.function?.name) tc[k].function.name += t.function.name; if (t.function?.arguments) tc[k].function.arguments += t.function.arguments; } } }
    tc.forEach((c, i) => { if (!c.id) c.id = "call_" + i; });
    // truncated if the model hit its length cap, or the stream died (e.g. Vercel's 300s limit) before a clean [DONE]
    const truncated = finish === "length" || (!sawDone && !tc.length && content.length > 0);
    return { content, reasoning, tool_calls: tc.filter(Boolean), truncated }; }
  return async (messages, tools, cb) => {
    const on = typeof cb === "function" ? { token: cb } : (cb || {});
    once._tools = tools; let full = "", reasoning = "";
    for (let attempt = 0; attempt < 6; attempt++) {
      let r; const msgs = attempt === 0 ? messages : [...messages, { role: "assistant", content: full }, { role: "user", content: "Continue exactly where you left off. Do not repeat anything already written; pick up mid-sentence if needed." }];
      try { r = await once(msgs, { noSearch: attempt > 0 ? true : on.noSearch, continuation: attempt > 0, maxTokens: on.maxTokens, base: full, on }); }
      catch (e) { if (attempt === 0) throw e; break; } // keep what we have if a continuation fails
      full += r.content; if (r.reasoning) reasoning = r.reasoning;
      if (r.tool_calls.length) return { content: full, reasoning, tool_calls: r.tool_calls };
      if (!r.truncated || !r.content) break;
      on.continued?.(attempt + 1);
    }
    return { content: full, reasoning, tool_calls: [] }; }; }
