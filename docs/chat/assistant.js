// FrameFX Assistant: intent router + conversational layer around the unchanged Beta7 preset pipeline (Chat in presetai.js).
import { GAMES, GPUS } from "./kb.js";
import { Chat, OFFTOPIC, BACK, WHY } from "./presetai.js";
const pick = a => a[Math.floor(Math.random() * a.length)];
export const norm = t => t.toLowerCase().replace(/[’'`]/g, "").replace(/\s+/g, " ").trim();
const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
function findAll(items, names, t) { const out = [];
  for (const it of items) { let best = null; for (const a of names(it)) { const m = new RegExp("(?<![\\w])" + esc(norm(a)) + "(?![\\w])").exec(t); if (m && (!best || a.length > best[1])) best = [m.index, a.length]; }
    if (best) out.push([best[0], best[1], it]); }
  out.sort((x, y) => x[0] - y[0] || y[1] - x[1]); const res = []; let end = -1;
  for (const [i, l, it] of out) if (i >= end) { res.push(it); end = i + l; } return res; }
export const gamesIn = t => findAll(GAMES, g => [g[0], ...g[2], g[1].slice(0, -4)], t);
export const gpusIn = t => findAll(GPUS, g => [g[0], ...g[1]], t);
export const COST = ["", "very light", "light", "medium", "heavy", "very heavy"];
export const TIER = s => s >= 65 ? "high-end" : s >= 45 ? "upper mid-range" : s >= 30 ? "mid-range" : s >= 20 ? "entry-level" : "older / low-end";

// Presets must only trigger on an EXPLICIT request: a preset/FrameFX-settings ask,
// not a bare game mention. wantsPreset gates new presets; wantsChange gates follow-up
// tweaks once a preset already exists.
const PRESET_WORDS = /\b(presets?|frame ?gen\w*|framegen|upscal\w*|fsr ?\d?|xess|csr ?\d?(\.\d)?|ssgi|ray.?trac\w*|dlss|cpu ?boost|power plan|anti.?cheat)\b/;
export function wantsPreset(raw) { const t = norm(raw);
  if (/\bpresets?\b/.test(t)) return true;                                   // the word "preset"
  if (/\bpreset for\b/.test(t)) return true;                                 // "preset for X"
  if (/\b(make|build|give|create|gen(erate)?|set ?up|do|whip up|cook up|configure|optimi[sz]e|tune|dial in) (me )?(a |an |the )?(new )?(framefx )?(preset|profile|config|configuration)\b/.test(t)) return true;
  if (/\b\d{2,3}\s*(fps|hz)\b/.test(t) || /\b(want|target|aim(ing)? for|hit|reach|get to)\b[^.?!]*\bfps\b/.test(t)) return true; // an fps target = tuning
  if (/\b(set ?up|optimi[sz]e|tune|tuning|configure|config|dial in)\b[^.?!]*\b(for|on|in)\b/.test(t)) return true; // "optimize/tune/set up ... for/on X"
  if (/\b(best|good|recommended|optimal|ideal)\b[^.?!]*\bsettings?\b/.test(t)) return true; // a settings ask
  if (/\b(set ?up|optimi[sz]e|tune|configure|dial in)\b/.test(t) && gamesIn(t).length) return true; // "set up/optimize/tune <game>"
  if (PRESET_WORDS.test(t)) return true;                                        // naming a FrameFX feature/setting
  return false; }
export function wantsChange(raw) { const t = norm(raw);
  return /^(what about|how about|actually|instead|also|and|now)\b/.test(t)
    || /^(make it|turn|switch|add|set|change|bring|use|kill|push|lower|raise|increase|reduce|drop|enable|disable|remove|boost|cap)\b/.test(t)
    || /\b(4k|1440p?|1080p?|native|quieter|louder|smoother|sharper|crisper|higher|lower)\b/.test(t); }

const TERMS = [
  [/frame ?gen\w*|\bfg\b|generated frames|interpolat/, "Frame generation inserts AI/optical-flow frames between real ones, so 50 fps can look like 100. It smooths motion but doesn't reduce input lag (it adds a little), and it works best when your base fps is already 40+. FrameFX offers CSR 1.3, CSR 2.0, FSR 3 and CSR AI frame gen at 2x–4x."],
  [/multiplier|\b[234]x\b/, "The multiplier is how many frames you see per real frame: 2x doubles, 3x triples, 4x quadruples. Higher means smoother but more artifacts and ghosting, so I cap it at 2x if you say you hate blur."],
  [/upscal\w*|backend|\bcsr\b|\bcsr ?1\.[23]\b|bilinear/, "The upscaler renders the game at a lower resolution and scales it up, which gives you more fps. FrameFX options: CSR 1.3 (default, cheap and sharp), CSR 1.2, Bilinear, FSR 1/2/3, FSR 4 (RX 9000 only), XeSS, CSR 2.0 and CSR AI."],
  [/\bfsr ?4\b/, "FSR 4 is AMD's ML upscaler. It only runs on RX 9000 cards (9060 XT, 9070, 9070 XT), so on other GPUs I fall back to CSR 1.3."],
  [/\bfsr\b|fidelityfx/, "FSR is AMD's FidelityFX Super Resolution. FSR 1 is a simple spatial upscaler, FSR 2/3 are temporal (sharper, they use motion data), FSR 3 can also do frame gen, and FSR 4 is ML-based for RX 9000 cards. Ask for any of them by name and I'll set it."],
  [/xess/, "XeSS is Intel's upscaler. It runs on any modern GPU and looks best on Intel Arc. Say \"use XeSS\" and I'll switch to it."],
  [/anti.?cheat|\beac\b|battleye|vanguard|ban/, "Anti-cheat games (Valorant, Fortnite, Apex, Elden Ring online and so on) can flag overlays and injected frames. To be safe I always keep frame generation off for them and show a warning. Upscaling and other settings are still set, at your own risk."],
  [/competitive|esports|shooter/, "For competitive shooters I keep frame gen off, because generated frames add input latency and every millisecond counts there."],
  [/ssgi|global illumination/, "SSGI (screen-space global illumination) adds bounced light for a richer look. It's experimental in FrameFX and costs some GPU time. Say \"turn on SSGI\" to enable it."],
  [/ray.?trac|\brt\b|ssrt/, "RT lighting is FrameFX's experimental ray-traced lighting pass. It looks better than SSGI and replaces it, but it's heavier. Say \"add ray tracing\" to turn it on."],
  [/cpu boost|power plan|core parking/, "CPU boost switches on the high-performance power plan, disables core parking, raises game priority and prefers fast cores. It helps in CPU-bound games. Say \"boost my CPU\" to enable it."],
  [/latency|input lag/, "Latency is the delay between your input and what you see. Frame gen adds a bit, so if you say latency matters I turn frame gen off."],
  [/\bpresets?\b|performance|quality mode|balanced/, "A preset is a full FrameFX GameProfile: the Performance/Quality mode, frame gen type and multiplier, upscaler, resolution, SSGI, RT lighting and CPU boost. Quality mode looks better, Performance keeps GPU time down. I pick it from your game, GPU and fps target, and you can copy the JSON into FrameFX."],
  [/resolution|1440|4k|1080|native/, "Resolution is the output resolution FrameFX targets (native, 1080p, 1440p or 4K). Higher resolution costs fps, so tell me yours and I'll factor it in."],
  [/framefx|this app|universal/, "FrameFX (Universal FrameFX) is a Windows app that adds frame generation, upscaling and lighting effects to games, with per-game profiles. I'm its built-in assistant: I build and tweak those profiles for you by chatting."],
];
const CAPS = "I'm WrenbyAI. I can:\n• Build a FrameFX preset from a sentence, e.g. \"Cyberpunk on a 3060 laptop, 45 fps, want 120\".\n• Tweak it as we talk: \"what about 4K\", \"make it quieter\", \"turn frame gen off\", \"actually I have a 4070\", \"add ray tracing\".\n• Explain settings: frame gen, upscalers, FSR/XeSS, anti-cheat, SSGI, RT lighting, CPU boost.\n• Tell you what I know about a game or GPU, or compare two GPUs.\nI run fully offline in your browser with a small 1M-parameter model, so I'm not a general chatbot. Gaming and FrameFX are my thing.";

export class Assistant {
  constructor(model) { this.chat = new Chat(model); this.lastKind = null; }
  reset() { this.chat.reset(); }
  get last() { return this.chat.last; }
  async send(text) { const r = this.route(text); this.lastKind = r ? r.kind : "preset";
    if (r) return { kind: r.kind, text: r.text, preset: false };
    if (!(wantsPreset(text) || (this.chat.last && wantsChange(text)))) { this.lastKind = "chat"; return { kind: "chat", text: this.infoAnswer(text), preset: false }; }
    const before = this.chat.last; const reply = await this.chat.turn(text);
    if (reply === OFFTOPIC || reply.startsWith("I didn't catch a change")) { this.lastKind = "offtopic"; return { kind: "offtopic", text: this.offtopic(text), preset: false }; }
    return { kind: "preset", text: reply, preset: this.chat.last !== null && this.chat.last !== before }; }
  snapshot() { const f = this.chat.f; return { f: { ...f, prefs: new Set(f.prefs), asks: new Set(f.asks) }, fg_off: this.chat.fg_off, last: this.chat.last }; }
  restore(s) { this.chat.f = s.f; this.chat.fg_off = s.fg_off; this.chat.last = s.last; }
  // LLM mode: returns a preset-pipeline result, or null if the message should go to the LLM (state untouched).
  async tryPreset(text) {
    const r = this.route(text); if (r) return null;
    if (!(wantsPreset(text) || (this.chat.last && wantsChange(text)))) return null; // no explicit preset intent: let the normal answer path handle it
    const snap = this.snapshot(), before = this.chat.last; const reply = await this.chat.turn(text);
    if (reply === OFFTOPIC || reply.startsWith("I didn't catch a change") || reply.startsWith("Hi! I build")) { this.restore(snap); return null; }
    return { kind: "preset", text: reply, preset: this.chat.last !== null && this.chat.last !== before }; }
  presetContext() { const c = this.chat; if (!c.last) return "No preset has been made yet in this chat.";
    const st = c.state(), set = Chat.settings(c.last), why = c.last.why.split(",").filter(Boolean).map(w => WHY[w]).filter(Boolean);
    return `Current FrameFX preset: game ${st.game}; GPU ${st.gpu || "RTX 3060 (assumed)"}${st.laptop ? " laptop" : ""}; current fps ${st.fps ?? "unknown"}; target ${st.target ?? "auto"}; ` +
      Object.entries(set).map(([k, v]) => `${k}: ${v}`).join("; ") + (c.fg_off ? "; user turned frame gen off" : "") + ". Reasons: " + why.join(" "); }
  route(raw) {
    const t = norm(raw), q = t.replace(/[?!.]+$/, ""), ctx = this.chat.f.game;
    const isQ = /\?$/.test(t) || /^(what|whats|which|how|why|does|do|is|are|can|should|tell me|explain|whos|who|when)\b/.test(t);
    if (/^(hi|hello|hey|yo|sup|hiya|howdy|good (morning|afternoon|evening))\b[\s!.,]*(there|bot|assistant)?[\s!.]*$/.test(t))
      return { kind: "greet", text: pick(["Hey! I'm WrenbyAI. What are you playing, and on which GPU?", "Hi there! Tell me your game and GPU and I'll build a FrameFX preset, or ask me about any setting."]) };
    if (/^(thanks|thank you|thx|ty|cheers|appreciate it|nice|cool|great|awesome|perfect|ok|okay|k|got it)\b[\s!.]*(thanks|man|dude|bro)?[\s!.]*$/.test(t))
      return { kind: "thanks", text: this.chat.last ? pick(["You're welcome! Want me to tweak anything else, like resolution, frame gen or noise?", "Anytime! Hit Copy JSON on the preset card to paste it into FrameFX."]) : "No problem! Whenever you're ready, tell me a game and GPU." };
    if (/^(bye|goodbye|see ya|cya|good night|gn|later)\b/.test(t)) return { kind: "bye", text: "See you! Have fun gaming." };
    if (/^(how are you|how r u|hows it going|whats up|wassup|how you doing)\b/.test(t)) return { kind: "smalltalk", text: "Doing great, my weights are warm and ready. What game are we tuning today?" };
    if (/(who|what) are you|your name|are you (an? )?(ai|bot|human|chatgpt|gpt)|what model/.test(t)) return { kind: "about", text: "I'm WrenbyAI, a small AI trained from scratch for FrameFX that runs entirely in your browser (offline, ~1M parameters). I'm good at presets and FrameFX settings, but I'm not a general-knowledge chatbot." };
    if (/^(help|\?|menu)$|what can you do|how do (i|you) (use|work)|what do you do|how does this work/.test(t)) return { kind: "help", text: CAPS };
    if (/(supported|which|what) games|list (of )?games|games (do )?you (know|support)/.test(t))
      return { kind: "kb", text: `I know ${GAMES.length} games, including ${GAMES.slice(0, 12).map(g => g[0]).join(", ")}, plus competitive titles like Valorant, CS2 and Apex. Just name yours.` };
    if (/(supported|which|what) (gpus|graphics cards|cards)|list (of )?gpus/.test(t))
      return { kind: "kb", text: `I know ${GPUS.length} GPUs, from the GTX 1050 Ti up to the RTX 4090 and RX 9070 XT, plus Intel Arc A750/A770/B580.` };
    if (!isQ) return null;
    // explicit change requests phrased as questions go to the preset pipeline
    if (/^(what|how) about\b|^can (you|u|i|we) (make|set|turn|add|kill|bring|switch|change|get|push|do|use|run)|^(could|would) you\b|^can i get\b/.test(t)) return null;
    const games = gamesIn(t), gpus = gpusIn(t);
    if (gpus.length >= 2 && /better|faster|stronger|vs|versus|compare|or\b/.test(t)) {
      const [a, b] = gpus; const w = a[2] >= b[2] ? [a, b] : [b, a]; const pct = Math.round((w[0][2] / w[1][2] - 1) * 100);
      return { kind: "kb", text: pct < 5 ? `The ${a[0]} and ${b[0]} are about the same in raw performance by my rough scores.` : `The ${w[0][0]} is roughly ${pct}% faster than the ${w[1][0]} in raster performance (my rough scores: ${w[0][2]} vs ${w[1][2]}).${w[0][4] === "rx9000" ? " It also supports FSR 4." : ""}` }; }
    const signal = /\b\d{2,3}\s*(fps|hz)?\b|\bwant\b|target|\baim|preset|set ?up|\bmake\b|optimi|configure|build|get me|\bon (my|a|an)\b|laptop/.test(t.replace(/\b(rtx|gtx|rx)?\s?\d{4}\b/g, ""));
    const term = /what (is|are|does)|whats|explain|meaning|mean\b|how does|should i (use|turn)|why (is|did|no|off)|difference/.test(t);
    if ((games.length || gpus.length) && signal && !term) return null;
    if (games.length && !term) { const g = games[0];
      const ac = g[6] ? (g[5] ? "It's a competitive game with anti-cheat, so I keep frame generation off." : `It uses anti-cheat${g[1] === "eldenring.exe" ? " (Easy Anti-Cheat, online only)" : ""}, so I keep frame generation off and add a warning.`) : g[5] ? "It's competitive, so frame gen stays off for latency." : "No anti-cheat flagged, so frame generation is fair game.";
      if (/anti.?cheat|ban|safe|frame ?gen|fg/.test(t) || /tell me about|what about|info|demanding|heavy|run/.test(t) || isQ)
        return { kind: "kb", text: `${g[0]} (${g[1]}): ${COST[g[3]]} to run${g[4] ? ", HUD-heavy (watch for HUD shimmer with frame gen)" : ""}. ${ac} Want a preset? Tell me your GPU and fps.` }; }
    if (gpus.length && !term && /how (good|strong|fast)|tell me about|is (my |the |a )?.*(good|enough|ok)|fsr ?4|what tier/.test(t)) { const p = gpus[0];
      return { kind: "kb", text: `The ${p[0]} is a ${TIER(p[2])} card in my rough rankings (score ${p[2]}; the RTX 4090 is 88).${p[4] === "rx9000" ? " It supports FSR 4." : p[3] === "amd" ? " FSR 4 needs an RX 9000, so I'd use CSR 1.3 or FSR 3." : ""} Tell me a game and I'll build a preset for it.` }; }
    if (term || /\?$/.test(t)) {
      if (/why/.test(t) && /frame ?gen|fg/.test(t) && /off/.test(t) && this.chat.last) {
        const why = this.chat.last.why; return { kind: "explain", text: why.includes("anticheat") ? "Frame gen is off because this game uses anti-cheat; generated frames and overlays could trip it." : why.includes("competitive") ? "It's a competitive game, so frame gen is off to keep latency low." : why.includes("user_fg_off") ? "You asked me to turn it off. Say \"bring frame gen back\" any time." : why.includes("latency") ? "You said latency matters, and frame gen adds latency." : "You're already near your target fps, so frame gen isn't needed." }; }
      if (/^why\b/.test(t) && this.chat.last) { const o = this.chat.last, w = o.why.split(",");
        const lines = w.map(x => WHY[x]).filter(Boolean); if (w.includes("user_fg_off")) lines.push("Frame gen is off because you asked.");
        const fgl = o.fg !== "0" ? `Frame gen ${o.m}x: I estimated a base of ~${o._base} fps and your target is ${o._target}, so ${o.m}x gets you there (capped at 2x–4x).` : "";
        return { kind: "explain", text: ["Here's my reasoning for the current preset:", fgl, ...lines].filter(Boolean).join("\n") }; }
      for (const [re, ans] of TERMS) if (re.test(q)) return { kind: "explain", text: ans };
    }
    return null; }
  infoAnswer(raw) { const t = norm(raw); const games = gamesIn(t), gpus = gpusIn(t);
    if (games.length) { const g = games[0];
      const ac = g[6] ? (g[5] ? " It's competitive with anti-cheat, so frame generation would stay off." : " It uses anti-cheat, so frame generation would stay off.") : g[5] ? " It's competitive, so frame gen would stay off." : "";
      return `${g[0]} (${g[1]}) is ${COST[g[3]]} to run${g[4] ? ", and HUD-heavy" : ""}.${ac} I run offline and don't have general game trivia, but if you want I can build a FrameFX preset — just say "make a preset for ${g[0]}" and tell me your GPU and target fps.`; }
    if (gpus.length) { const p = gpus[0];
      return `The ${p[0]} is a ${TIER(p[2])} card in my rough rankings (score ${p[2]}).${p[4] === "rx9000" ? " It supports FSR 4." : ""} Want a FrameFX preset for a game on it? Say "make a preset for <game>".`; }
    return this.offtopic(raw); }
  offtopic(raw) { const t = norm(raw);
    if (/joke/.test(t)) return pick(["Why did the GPU break up with the CPU? It needed more space to render its feelings. Anyway, want a preset?", "I'd tell you a joke about 30 fps, but you wouldn't see the punchline in time."]);
    if (/weather|news|president|stock|score|won|capital|recipe|code|math|homework/.test(t)) return "I can't look that up, sorry. I run offline in your browser and only know FrameFX and gaming stuff. " + (this.chat.f.game ? `Want to keep tweaking your ${this.chat.f.game[0]} preset?` : "Tell me a game and GPU and I'll build you a preset.");
    return this.chat.f.game ? pick([`Not sure I follow, but I'm happy to keep tuning ${this.chat.f.game[0]}. Try "what about 4K", "make it quieter", "turn frame gen off" or "actually I have a 4070".`, `Hmm, I didn't get that one. I'm best at FrameFX stuff. You can change the resolution, GPU, fps target or frame gen for ${this.chat.f.game[0]}, or ask me what a setting does.`])
      : pick(["I'm not much of a general chatbot, sorry. I'm WrenbyAI. Tell me what you're playing and on which GPU, or ask \"what can you do\".", "That's outside what I know. I only cover FrameFX and gaming, and I run offline. Try \"Cyberpunk on a 3060, want 120 fps\" or \"what is frame gen?\""]); }
}
