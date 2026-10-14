// FrameFX preset chatbot (Beta7) — JS port of chat.py / extract.py / rules.py / numwords.py / evaluate3.py / assist.py.
import { GAMES, GPUS } from "./kb.js";
const CH = []; for (let i = 32; i < 127; i++) CH.push(String.fromCharCode(i)); CH.push("\t", "\n");
const STOI = {}, ITOS = {}; CH.forEach((c, i) => { STOI[c] = i + 1; ITOS[i + 1] = c; });
const enc = s => Array.from(s).map(c => STOI[c] ?? STOI["?"]);
const dec = ids => ids.map(i => ITOS[i] ?? "").join("");
const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const setOf = a => new Set(a);
const minus = (a, b) => new Set([...a].filter(x => !b.has(x)));
const union = (a, b) => new Set([...a, ...b]);
function pyRound(x) { const f = Math.floor(x), d = x - f; if (d > 0.5) return f + 1; if (d < 0.5) return f; return f % 2 === 0 ? f : f + 1; }

// ---------- model ----------
export class Model {
  constructor(ort, session) { this.ort = ort; this.s = session; }
  static async load(ort, url) { return new Model(ort, await ort.InferenceSession.create(url, { executionProviders: ["wasm"] })); }
  async gen(prompt, maxn = 160) {
    const ids = enc(Array.from(prompt).slice(-300).join("") + "\t"); const start = ids.length;
    for (let k = 0; k < maxn; k++) {
      const w = ids.slice(-448);
      const t = new this.ort.Tensor("int64", BigInt64Array.from(w.map(BigInt)), [1, w.length]);
      const out = await this.s.run({ input_ids: t }); const lo = out.logits; const V = lo.dims[2]; const d = lo.data; const off = (w.length - 1) * V;
      let n = 0, b = -Infinity; for (let j = 0; j < V; j++) if (d[off + j] > b) { b = d[off + j]; n = j; }
      if (n === STOI["\n"]) break; ids.push(n);
    }
    return dec(ids).split("\t").slice(1).join("\t");
  }
}

// ---------- numwords ----------
const ONES = {}; "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen".split(" ").forEach((w, i) => ONES[w] = i);
const TENS = {}; "_ _ twenty thirty forty fifty sixty seventy eighty ninety".split(" ").forEach((w, i) => { if (w !== "_") TENS[w] = 10 * i; });
const W = "(?:" + [...Object.keys(ONES), ...Object.keys(TENS), "hundred", "a", "and"].sort((a, b) => b.length - a.length).join("|") + ")";
function two(t) { if (!t.length) return [null, 0]; const x = t[0];
  if (x in TENS) { if (t.length > 1 && t[1] in ONES && ONES[t[1]] > 0 && ONES[t[1]] < 10) return [TENS[x] + ONES[t[1]], 2]; return [TENS[x], 1]; }
  if (x in ONES) return [ONES[x], 1]; return [null, 0]; }
function nwparse(t) { let i = 0, v = null;
  if (t[0] === "a" && t[1] === "hundred") { v = 100; i = 2; }
  else { const [a, k] = two(t); if (a === null) return [null, 0]; i = k;
    if (a < 10 && t[i] === "hundred") { v = a * 100; i += 1; }
    else if (a < 10 && k === 1 && i < t.length && t[i] in TENS) { const [b, k2] = two(t.slice(i)); return [a * 100 + b, i + k2]; }
    else return [a, i]; }
  if (t[i] === "and") i += 1;
  const [b] = two(t.slice(i)); const k = two(t.slice(i))[1];
  if (b !== null) return [v + b, i + k];
  if (i > 0 && t[i - 1] === "and") i -= 1;
  return [v, i]; }
export function convert(text) {
  return text.replace(new RegExp("(?<![\\w])" + W + "(?:[\\s-]+" + W + ")*(?![\\w])", "gi"), s => {
    const toks = s.toLowerCase().trim().split(/[\s-]+/); const out = []; let i = 0;
    while (i < toks.length) { const [v, k] = nwparse(toks.slice(i));
      if (v === null || k === 0 || (k === 1 && v < 15 && toks[i] in ONES)) { out.push(toks[i]); i++; } else { out.push(String(v)); i += k; } }
    return out.join(" "); }); }

// ---------- extract ----------
const norm = t => t.toLowerCase().replace(/[’'`]/g, "").replace(/\s+/g, " ");
function find(items, names, t) { let best = null;
  for (const it of items) for (let a of names(it)) { a = norm(a); const m = new RegExp("(?<![\\w])" + esc(a) + "(?![\\w])").exec(t);
    if (m && (best === null || a.length > best[0])) best = [a.length, it, [m.index, m.index + m[0].length]]; }
  return best; }
const PREF = { blur: /blur|smear|ghost|sharp/, latency: /latency|input lag|input delay|responsive|low lag/,
  quality: /max(imum)? quality|best quality|high(est)? quality|gorgeous|eye candy|look(s)? (as good|amazing|great|incredible|stunning)|best look|max settings|ultra quality|pretty as possible/,
  battery: /battery|unplugged|quiet|cool and quiet|plugged out/ };
const ASK = { fsr3fg: /fsr ?3 (frame ?gen|fg)|amd frame gen/, fsr4: /fsr ?4/, fsr1: /fsr ?1(?!\d)/, fsr2: /fsr ?2(?!\d)/, fsr3: /fsr ?3(?! ?(frame|fg))/,
  xess: /xess/, ssgi: /ssgi/, rt: /ray.?trac|\brt lighting/, cpu: /cpu boost|boost (my |the )?cpu/, csr12: /csr ?1\.2/ };
const CUR = "(getting|get|at|sitting at|around|about|roughly|like|currently|runs at|running at|base|baseline|i'?m at|stuck at|only|now|hovering)";
const TGT = "(run at|can (i|we) get|i'?d take|push it to|want|target|targeting|aim|aiming( for)?|need|to|for|happy with|love|hit|reach|get me|->|→|into)";
const reTgtBefore = new RegExp("(i'?d|would) (like|love|be happy with)\\s*$|" + TGT + "\\s*(like |about |around )?$");
const reCurBefore = new RegExp(CUR + "\\s*$");
export function extract(text) {
  const t = norm(convert(text));
  const f = { game: null, gpu: null, laptop: /laptop|mobile|notebook/.test(t), res: null, fps: null, target: null, prefs: new Set(), asks: new Set() };
  const g = find(GAMES, g => [g[0], ...g[2], g[1].slice(0, -4)], t), p = find(GPUS, g => [g[0], ...g[1]], t);
  let s = t;
  for (const hit of [g, p]) if (hit) { f[hit === g ? "game" : "gpu"] = hit[1]; const [a, b] = hit[2]; s = s.slice(0, a) + " ".repeat(b - a) + s.slice(b); }
  for (const [k, pat] of [["1080", /1080p|\bfhd\b|\b1080\b/], ["1440", /1440p|\bqhd\b|\b1440\b|\b2k\b/], ["4k", /\b4k\b|2160p|\buhd\b/]]) if (pat.test(s)) f.res = k;
  s = s.replace(/1080p?|1440p?|2160p?|\d+ ?hz|\d+ ?gb|\b20\d\d\b/g, "  ");
  for (const k in PREF) if (PREF[k].test(s)) f.prefs.add(k);
  for (const k in ASK) if (ASK[k].test(s)) f.asks.add(k);
  const nums = [...s.matchAll(/(?<![\w.])(\d{2,3})(?!\d|\.\d)/g)].map(m => [m.index, parseInt(m[1])]).filter(([, n]) => n >= 15 && n <= 500);
  let cur = null, tgt = null;
  for (const [pos, n] of nums) { const before = s.slice(Math.max(0, pos - 22), pos), after = s.slice(pos, pos + 30);
    if (reTgtBefore.test(before) && !/(runs at|at|getting|sitting at|currently)\s*(like )?$/.test(before)) tgt = n;
    else if (reCurBefore.test(before) || /^\d+\s*(fps)?\s*(now|currently|base|right now|locked|->|→|to \d)/.test(after)) cur = n;
    else if (/^\d+\s*(fps)?\s*would be (ideal|nice|great)/.test(after)) tgt = n;
    else if (tgt === null && cur !== null && n !== cur) tgt = n;
    else if (tgt === null) tgt = n; }
  f.fps = cur; f.target = tgt; return f; }

// ---------- rules ----------
export const SLOTS = ["exe", "p", "fg", "m", "b", "r", "gi", "rt", "cpu", "why"];
function baseFps(game, gpu, laptop, res) { const s = gpu[2] * (laptop ? 0.75 : 1.0); const rf = { "1080": 1.0, "1440": 0.65, "4k": 0.32 }[res] ?? 0.8;
  return Math.max(8, pyRound(s * rf * 6.0 / Math.pow(game[3], 1.1))); }
export function decide(f) {
  const g = f.game, gpu = f.gpu, prefs = f.prefs, asks = f.asks, why = [];
  const base = f.fps || baseFps(g, gpu, f.laptop, f.res); const target = f.target || (g[5] ? 144 : 120);
  const o = { exe: g ? g[1].toLowerCase() : "?", p: "0", fg: "0", m: "0", b: "0", r: "0", gi: "0", rt: "0", cpu: "0" };
  o.r = { "1080": "4", "1440": "1", "4k": "2" }[f.res] ?? "0";
  if (prefs.has("quality") && !prefs.has("battery")) { o.p = "1"; why.push("quality"); }
  else if (gpu[2] >= 45 && !f.laptop && base >= target / 2) { o.p = "1"; why.push("headroom"); }
  else why.push("perf");
  if (f.laptop) why.push("laptop");
  if (g[5] || g[6]) { why.push(g[6] ? "anticheat" : "competitive"); if (g[6] && !g[5]) why.push("ac_warn"); }
  else if (prefs.has("latency")) why.push("latency");
  else if (base >= target * 0.9) why.push("at_target");
  else { let m = Math.ceil(target / base); m = Math.min(Math.max(m, 2), 4);
    if (prefs.has("blur") && m > 2) { m = 2; why.push("blur"); }
    o.fg = "1"; o.m = String(m);
    if (asks.has("fsr3fg")) { o.fg = "3"; why.push("asked_fsr3fg"); }
    if (base < 35) why.push("lowbase");
    if (g[4]) why.push("hud"); }
  for (const [k, v] of [["fsr1", "3"], ["fsr2", "4"], ["fsr3", "5"], ["xess", "7"], ["csr12", "1"]]) if (asks.has(k)) { o.b = v; why.push("asked_" + k); }
  if (asks.has("fsr4") || (prefs.has("quality") && gpu[4] === "rx9000")) { if (gpu[4] === "rx9000") { o.b = "6"; why.push("fsr4"); } else why.push("no_fsr4"); }
  if (asks.has("ssgi")) { o.gi = "1"; why.push("ssgi"); }
  if (asks.has("rt")) { o.rt = "1"; o.gi = "0"; why.push("rt"); }
  if (asks.has("cpu")) { o.cpu = "1"; why.push("cpu"); }
  o.why = why.join(","); o._base = base; o._target = target; return o; }
function parseLine(line) { const d = {}; for (const part of line.trim().split(";")) { const i = part.indexOf("="); if (i >= 0) d[part.slice(0, i).trim()] = part.slice(i + 1).trim(); } return d; }

// ---------- difflib (SequenceMatcher.ratio, no junk) ----------
function ratio(a, b) {
  const b2j = {}; [...b].forEach((c, i) => (b2j[c] ||= []).push(i));
  // autojunk: popular chars when len(b)>=200 — names are short, so never applies
  function longest(alo, ahi, blo, bhi) { let bi = alo, bj = blo, bs = 0, j2len = {};
    for (let i = alo; i < ahi; i++) { const nl = {}; for (const j of b2j[a[i]] || []) { if (j < blo) continue; if (j >= bhi) break; const k = (j2len[j - 1] || 0) + 1; nl[j] = k; if (k > bs) { bi = i - k + 1; bj = j - k + 1; bs = k; } } j2len = nl; }
    return [bi, bj, bs]; }
  const q = [[0, a.length, 0, b.length]]; let M = 0;
  while (q.length) { const [alo, ahi, blo, bhi] = q.pop(); const [i, j, k] = longest(alo, ahi, blo, bhi);
    if (k) { M += k; if (alo < i && blo < j) q.push([alo, i, blo, j]); if (i + k < ahi && j + k < bhi) q.push([i + k, ahi, j + k, bhi]); } }
  const T = a.length + b.length; return T ? 2 * M / T : 1; }
function snap(v, tab) { if (v == null || v === "" || v === "-") return null; if (v in tab) return tab[v];
  let best = null; for (const k in tab) { const r = ratio(v, k); if (r >= 0.85 && (!best || r > best[0] || (r === best[0] && k > best[1]))) best = [r, k]; }
  return best ? tab[best[1]] : null; }
const GBY = {}; GAMES.forEach(g => GBY[g[1].toLowerCase()] = g); const PBY = {}; GPUS.forEach(g => PBY[g[0].toLowerCase()] = g);
export function mfeats(line) {
  const d = parseLine(line); const n = v => v && /^\d+$/.test(v) ? parseInt(v) : null; const S = v => (v == null || v === "" || v === "-") ? new Set() : new Set(v.split(","));
  const inter = (s, ok) => new Set([...s].filter(x => ok.includes(x)));
  return { game: snap(d.g, GBY), gpu: snap(d.gpu, PBY), laptop: d.lap === "1", res: ["1080", "1440", "4k"].includes(d.res) ? d.res : null,
    fps: n(d.fps), target: n(d.tgt), prefs: inter(S(d.pref), ["blur", "latency", "quality", "battery"]),
    asks: inter(S(d.ask), ["fsr1", "fsr2", "fsr3", "xess", "fsr4", "fsr3fg", "ssgi", "rt", "cpu", "csr12"]) }; }
const ASK_EVID = { fsr1: /fsr/, fsr2: /fsr/, fsr3: /fsr/, fsr4: /fsr/, fsr3fg: /fsr|amd/, xess: /xess/, ssgi: /ssgi/, rt: /ray|\brt\b/, cpu: /cpu/, csr12: /csr/ };
export const prep = text => text; // Beta7: number-word conversion OFF for model input
function validFeats(mf, text) { const f = { ...mf };
  if (text != null) f.asks = new Set([...f.asks].filter(a => ASK_EVID[a].test(text.toLowerCase())));
  for (const k of ["fps", "target"]) if (f[k] != null && !(f[k] >= 10 && f[k] <= 500)) f[k] = null;
  if (f.fps && f.target && f.target <= f.fps) f.target = null; return f; }

// ---------- assist ----------
export const BACK = ["CSR 1.3", "CSR 1.2", "Bilinear", "FSR 1", "FSR 2", "FSR 3", "FSR 4", "XeSS", "CSR 2.0", "CSR AI"];
export const WHY = { quality: "Quality preset because you asked for the best look.", headroom: "Quality preset: your GPU has headroom for it.",
  perf: "Performance preset (CSR 1.3 low-cost path) to keep GPU time down.", laptop: "Laptop GPU: assumed ~25% less performance.",
  anticheat: "WARNING: this game uses anti-cheat. Frame generation is left off; overlays can trip some anti-cheats, use at your own risk.",
  ac_warn: "", competitive: "Competitive shooter: frame generation off to keep input latency low.",
  latency: "You care about latency, so frame generation is off.", at_target: "You're already near your target fps, so no frame generation needed.",
  blur: "Capped at 2x frame generation because you dislike blur/ghosting.", lowbase: "Base fps is low (<35); generated frames will show more artifacts. Lowering in-game settings helps.",
  hud: "HUD-heavy game: watch for HUD shimmer with frame generation.", fsr4: "FSR 4 upscaler: you asked for max quality on an RX 9000 card.",
  no_fsr4: "FSR 4 needs an RX 9000 card, so CSR 1.3 is used instead.", asked_fsr3fg: "FSR 3 frame generation, as requested.",
  ssgi: "SSGI on, as requested (experimental).", rt: "Ray-traced lighting on, as requested (experimental, replaces SSGI).", cpu: "CPU boost options on, as requested." };
export function profile(d) { const fg = parseInt(d.fg), c = d.cpu === "1";
  return { Preset: parseInt(d.p), FrameGen: fg !== 0, FgSelect: fg, FgMultiplier: fg ? parseInt(d.m) : 4, Ssgi: d.gi === "1", SsgiPreset: 0, SsgiTemporal: false,
    Ssrt: d.rt === "1", SsrtPreset: 0, SsrtTemporal: false, Backend: parseInt(d.b), Res: parseInt(d.r),
    Cpu: { PowerPlan: c, MinProcessorState100: false, DisableCoreParking: c, BoostMode: c ? 1 : 0, HighPriority: c, PreferFastCores: c,
      AvoidSmtSiblings: false, TimerResolution1ms: false, AllPhysicalCores: false, LowerFrameFxPriority: c, BackgroundProcesses: [], DisablePowerThrottling: c },
    FgV2HudMask: null, FgV2HudMaskW: 64, FgV2HudMaskH: 36 }; }

// ---------- chat ----------
export const FG = ["Off", "CSR 1.3", "CSR 2.0", "FSR 3", "CSR AI"], PRESET = ["Performance", "Quality", "Balanced"];
export const RES = { "0": "native", "1": "1440p", "2": "4K", "3": "720p", "4": "1080p" };
export const OFFTOPIC = "I only help with game presets for FrameFX. Tell me a game and your GPU (e.g. \"Cyberpunk on a 3060, want 120 fps\") and I'll build one.";
const GAME_ALIASES = GAMES.map(gg => [gg[0], ...gg[2]].sort((a, b) => b.length - a.length));
export class Chat {
  constructor(model = null, policy = "modelfirst") { this.m = model; this.policy = policy; this.reset(); }
  reset() { this.f = { game: null, gpu: null, laptop: false, res: null, fps: null, target: null, prefs: new Set(), asks: new Set() }; this.fg_off = false; this.last = null; }
  _intents(t) { t = t.toLowerCase(); const ch = [];
    if (/\b(start over|reset|new game|forget it)\b/.test(t)) { this.reset(); return ["reset"]; }
    if (/(frame ?gen\w*|fg|generated frames)\W+(off|disabled?)|(turn|switch|shut) off (the )?(frame ?gen|fg)|no (frame ?gen\w*|fg)|disable (frame ?gen|fg)|(kill|drop|remove|ditch|lose|stop) (the )?(frame ?gen\w*|fg)|without (frame ?gen\w*|fg)/.test(t)) { this.fg_off = true; ch.push("fg_off"); }
    else if (/(frame ?gen|fg)\W+(on|back)|(turn|switch) (on|back on) (the )?(frame ?gen|fg)|enable (frame ?gen|fg)|bring (back )?(the )?(frame ?gen|fg)( back)?|(frame ?gen|fg) back/.test(t)) { this.fg_off = false; ch.push("fg_on"); }
    if (/quiet|cooler|less (noise|heat|power)|fans? (are |is |r )?(\w+ )?(too |so |really )?loud|save power|running hot|tone it down/.test(t)) { this.f.prefs = union(minus(this.f.prefs, setOf(["quality"])), setOf(["battery"])); ch.push("quiet"); }
    if (/prettier|better looking|more quality|look better|max(imum)? quality|looks? (bad|ugly)/.test(t)) { this.f.prefs = union(minus(this.f.prefs, setOf(["battery"])), setOf(["quality"])); ch.push("quality"); }
    if (/(don'?t|do not|no longer) care about (blur|ghosting)|blur is (fine|ok)/.test(t)) { this.f.prefs.delete("blur"); ch.push("blur_off"); }
    if (/(don'?t|do not|no longer) care about (latency|input lag|lag)/.test(t)) { this.f.prefs.delete("latency"); ch.push("lat_off"); }
    if (/\b(desktop|not a laptop|plugged in)\b/.test(t)) { this.f.laptop = false; this.f.prefs.delete("battery"); ch.push("desktop"); }
    return ch; }
  async turn(text) { const r = await this._turn(text); return r; }
  async _turn(text) {
    const s = this; const intents = s._intents(text); const tlow = text.toLowerCase();
    if (/^(?:\W*(hi|hello|hey|yo|help|can you help( me)?|what can you do)\W*(can you help( me)?)?\W*)$/.test(tlow))
      return "Hi! I build FrameFX presets. Tell me your game, GPU, and what fps you get vs. want (e.g. \"Elden Ring on a 3060, 50 fps, want 100\").";
    if (intents.length === 1 && intents[0] === "reset") return "OK, starting over. Which game, and what GPU do you have?";
    let kw = extract(text);
    const has_num = /\d|hundred|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety/.test(tlow);
    const changed = [];
    const on_topic = ["game", "gpu", "res", "fps", "target"].some(k => kw[k] !== null) || kw.prefs.size || kw.asks.size || kw.laptop || /\b(fps|frames?|gpu|preset|settings|frame ?gen|upscal\w*|rtx|gtx|rx|arc)\b/.test(tlow);
    let mf = null; s.lastModelLine = null;
    if (s.m && on_topic) { s.lastModelLine = await s.m.gen(prep(text)); mf = mfeats(s.lastModelLine); }
    let tl = tlow; for (const al of GAME_ALIASES) for (const a of al) tl = tl.split(a.toLowerCase()).join(" ");
    const gpu_hint = /\b(rtx|gtx|rx|arc|radeon|geforce|b580|a770)\b|\b(?!1440\b|2160\b)\d{2}[0-9]0\b(?!\s?(p|fps|hz)\b)/.test(tl);
    if (mf && s.policy === "modelfirst") {
      const vf = validFeats(mf, text); kw = { ...kw };
      for (const k of ["game", "gpu", "res", "fps", "target"]) { let mv = vf[k];
        if (k === "game" && mv !== null && s.f.game !== null && kw.game === null && !/switch|instead|now|what about|how about|play|do |set up|same for|next|also/.test(tlow)) mv = null;
        if ((k === "fps" || k === "target") && !has_num) mv = null;
        if (k === "gpu" && !gpu_hint) mv = null;
        if (mv !== null) kw[k] = mv; }
      for (const [k, o] of [["fps", "target"], ["target", "fps"]]) if (vf[k] === null && kw[k] !== null && kw[k] === vf[o] && has_num) kw[k] = null;
      kw.laptop = kw.laptop || vf.laptop; kw.prefs = union(kw.prefs, vf.prefs); kw.asks = union(kw.asks, vf.asks); mf = null; }
    for (const k of ["game", "gpu", "res", "fps", "target"]) { let v = kw[k];
      if (v === null && mf) { if (k === "game" && s.f.game === null) v = mf.game; else if ((k === "fps" || k === "target") && has_num) v = mf[k]; else if (k === "gpu" && gpu_hint) v = mf[k]; }
      if (v !== null && v !== undefined && v !== s.f[k]) { s.f[k] = v; changed.push(k); } }
    if (kw.laptop && !s.f.laptop) { s.f.laptop = true; changed.push("laptop"); }
    for (const p of minus(kw.prefs, s.f.prefs)) { s.f.prefs.add(p); changed.push("pref:" + p); if (p === "quality") s.f.prefs.delete("battery"); if (p === "battery") s.f.prefs.delete("quality"); }
    for (const a of minus(kw.asks, s.f.asks)) { s.f.asks.add(a); changed.push("ask:" + a); }
    if (s.f.fps && s.f.target && s.f.target <= s.f.fps) {
      if (changed.includes("fps") && !changed.includes("target")) s.f.target = null; else if (changed.includes("target") && !changed.includes("fps")) s.f.fps = null; }
    if (!changed.length && !intents.length)
      return s.f.game === null ? OFFTOPIC : "I didn't catch a change there. You can say things like \"what about 4K\", \"make it quieter\", \"turn frame gen off\" or \"actually I have a 4070\".";
    if (s.f.game === null) return "Got it. Which game is this for?" + (s.f.gpu ? "" : " And what GPU do you have?");
    const f = { ...s.f }; const assumed = f.gpu === null; if (assumed) f.gpu = GPUS.find(g => g[0] === "RTX 3060");
    const o = decide(f);
    if (s.fg_off && o.fg !== "0") { o.fg = "0"; o.m = "0"; o.why += ",user_fg_off"; }
    const prev = s.last; s.last = o; s.assumed = assumed; return s._reply(o, prev, [...changed, ...intents], assumed); }
  static settings(o) { return { Preset: PRESET[+o.p], "Frame gen": FG[+o.fg] + (o.fg !== "0" ? ` ${o.m}x` : ""), Upscaler: BACK[+o.b], Resolution: RES[o.r],
    SSGI: o.gi === "1" ? "on" : "off", "RT lighting": o.rt === "1" ? "on" : "off", "CPU boost": o.cpu === "1" ? "on" : "off" }; }
  _reply(o, prev, changed, assumed) {
    const g = GAMES.find(x => x[1].toLowerCase() === o.exe); const f = this.f; const heard = [];
    const PM = { blur: "you dislike blur", latency: "latency matters to you", quality: "you want max quality", battery: "you want it quiet / battery-friendly" };
    for (const c of changed) {
      if (c === "game") heard.push(`game ${g[0]}`);
      else if (c === "gpu") heard.push(`GPU ${f.gpu[0]}` + (f.laptop ? " (laptop)" : ""));
      else if (c === "res") heard.push(`resolution ${f.res}`.replaceAll("4k", "4K"));
      else if (c === "fps") heard.push(`you're getting ~${f.fps} fps`);
      else if (c === "target") heard.push(`target ${f.target} fps`);
      else if (c === "laptop") heard.push("it's a laptop");
      else if (c.startsWith("pref:")) heard.push(PM[c.slice(5)]);
      else if (c.startsWith("ask:")) heard.push(`you asked for ${c.slice(4)}`);
      else if (c === "fg_off") heard.push("frame generation off");
      else if (c === "fg_on") heard.push("frame generation allowed again");
      else if (c === "quiet") heard.push("quieter / cooler");
      else if (c === "quality") heard.push("better looking");
      else if (c === "desktop") heard.push("desktop, not laptop");
      else if (c === "blur_off" || c === "lat_off") heard.push("dropping that preference"); }
    let lines = [heard.length ? "Noted: " + heard.join(", ") + "." : ""];
    const now = Chat.settings(o); const items = Object.entries(now); const fmt = n => items.slice(0, n).map(([k, v]) => `${k} ${v}`).join(", ");
    if (prev === null) lines.push(`Here's a preset for ${g[0]}: ` + fmt(4) + ".");
    else { const pv = Chat.settings(prev);
      if (prev.exe !== o.exe) lines.push(`Switched to ${g[0]}: ` + fmt(4) + ".");
      else { const diff = Object.keys(now).filter(k => pv[k] !== now[k]).map(k => `${k}: ${pv[k]} → ${now[k]}`);
        lines.push(diff.length ? "Changes: " + diff.join("; ") + "." : "No settings change needed. The current preset already covers that: " + fmt(3) + "."); } }
    const why = o.why.split(",").filter(Boolean);
    if (why.includes("user_fg_off")) lines.push("Frame generation is off because you asked.");
    for (const w of why) {
      if (["perf", "quality", "headroom"].includes(w) && prev !== null && prev.p === o.p) continue;
      if (WHY[w] && (w.startsWith("anticheat") || w.startsWith("competitive") || prev === null || prev.exe !== o.exe || !prev.why.split(",").includes(w))) lines.push(WHY[w]); }
    if (why.includes("user_fg_off")) lines = lines.filter(l => l !== WHY.at_target);
    if (assumed) lines.push("(I assumed an RTX 3060 since you didn't say your GPU. Tell me yours for a better fit.)");
    return lines.filter(Boolean).join("\n"); }
  profileJson() { if (this.last === null) return null; const d = {}; for (const k of SLOTS) d[k] = this.last[k]; return { [this.last.exe]: profile(d) }; }
  state() { const f = this.f; return { game: f.game ? f.game[0] : null, gpu: f.gpu ? f.gpu[0] : null, laptop: f.laptop, res: f.res, fps: f.fps, target: f.target, prefs: [...f.prefs].sort(), asks: [...f.asks].sort(), fg_off: this.fg_off }; }
}
