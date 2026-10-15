// Cloud tasks: hard Forge/Zenith requests (signed in) run in their own fresh cloud sandbox via /api/task.
// The browser drives the agent one step per request and renders a live card with Stop and approvals.
import { authInfo } from "./auth.js";
export const LOOSE = /\b(run|build|test|tests|install|npm|pip|yarn|cargo|compile|execute|project|app|cli|server|scaffold|benchmark|multi[- ]?file)\b/i;
const MKEY = "wrenby-cloud-mode";
export const getMode = () => localStorage.getItem(MKEY) === "auto" ? "auto" : "ask";
export const setMode = m => localStorage.setItem(MKEY, m === "auto" ? "auto" : "ask");
const API_DEFAULT = "https://preset-ai-chat.vercel.app";
async function api(body, signal) { const a = authInfo(); const base = a.base == null ? API_DEFAULT : a.base;
  const r = await fetch(base + "/api/task", { method: "POST", signal, credentials: base ? "omit" : "same-origin", headers: { "content-type": "application/json", ...(a.token ? { authorization: "Bearer " + a.token } : {}) }, body: JSON.stringify(body) });
  const j = await r.json().catch(() => ({})); if (!r.ok) { const e = new Error(j.error || "HTTP " + r.status); e.status = r.status; e.body = j; throw e; } return j; }
export const signedIn = () => !!authInfo().user;
export const classifyRemote = (prompt, tier) => api({ action: "classify", prompt, tier });
const el = (tag, cls, txt) => { const d = document.createElement(tag); if (cls) d.className = cls; if (txt != null) d.textContent = txt; return d; };
// A small segmented "Ask before running / Auto-run" toggle that remembers the choice.
export function modeToggle() { const w = el("span", "ctmode"); w.title = "Cloud task permissions";
  for (const [m, label] of [["ask", "Ask before running"], ["auto", "Auto-run"]]) { const b = el("button", "ctm" + (getMode() === m ? " on" : "")); b.type = "button"; b.title = label; b.append(el("span", "ctl-long", label), el("span", "ctl-short", m === "ask" ? "Ask" : "Auto"));
    b.onclick = () => { setMode(m); w.querySelectorAll(".ctm").forEach(x => x.classList.toggle("on", x === b)); }; w.appendChild(b); } return w; }
// Returns null when the server says it's not a hard task (caller falls back to normal chat).
export async function runCloudTask(bub, prompt, tier, { signal, onStatus } = {}) {
  let mode = getMode();
  const st = await api({ action: "start", prompt, tier, mode }, signal); if (!st.hard) return null;
  const card = el("div", "ctask");
  card.innerHTML = `<div class="cthead"><span class="ctdot"></span><b>Running in cloud sandbox</b><span class="ctstat"></span><span class="fgrow"></span><span class="ctbadge"></span><button class="ctstop" type="button">Stop</button></div><div class="ctmeta"></div><div class="ctlog"></div><div class="ctask-approve" hidden></div>`;
  bub.appendChild(card); const $ = s => card.querySelector(s), logEl = $(".ctlog"), stat = $(".ctstat"), badge = $(".ctbadge"), ap = $(".ctask-approve");
  const showMode = () => { badge.textContent = mode === "auto" ? "Auto-run" : "Ask before running"; badge.className = "ctbadge " + mode; };
  showMode(); $(".ctmeta").textContent = `sandbox ${st.sandboxId} · ${tier} · up to ${st.maxSteps} steps · ${st.usedToday}/${st.limits.perDay} today`;
  let stopped = false, wake = null; const stop = async () => { stopped = true; $(".ctstop").disabled = true; wake?.(null); try { await api({ action: "stop", id: st.id }); } catch {} };
  $(".ctstop").onclick = stop; signal?.addEventListener("abort", stop);
  const scroll = () => { logEl.scrollTop = 1e9; const lg = bub.closest("#log"); if (lg) lg.scrollTop = 1e9; };
  const line = (cls, txt) => { const d = el("div", "ctl " + cls, txt); logEl.appendChild(d); scroll(); return d; };
  const render = evs => { for (const e of evs || []) {
    if (e.type === "say") { said.push(e.text); line("say", e.text); }
    else if (e.type === "cmd") { line("cmd", "$ " + e.cmd); if (e.output?.trim()) line("out" + (e.exitCode ? " bad" : ""), e.output.trim()); line("dim", "exit " + e.exitCode); }
    else if (e.type === "write") line("dim", "wrote " + e.path + " (" + e.bytes + " bytes)");
    else if (e.type === "read") line("dim", "read " + e.path + (e.cached ? " (unchanged)" : ""));
    else if (e.type === "denied") line("err", "denied: " + e.text);
    else if (e.type === "error" || e.type === "limit") line("err", e.text); } };
  // Ask mode: show the pending call and wait for Approve / Deny / Always allow this task.
  const askUser = p => new Promise(res => { wake = res; ap.hidden = false; ap.innerHTML = "";
    ap.append(el("div", "ctaq", p.tool === "run_command" ? "Run this command?" : "Write this file?"), el("pre", "ctacmd", p.tool === "run_command" ? "$ " + (p.command || "") : (p.path || "") + (p.preview ? "\n\n" + p.preview : "")));
    const row = el("div", "ctabtns"); const btn = (label, cls, v) => { const b = el("button", cls, label); b.type = "button"; b.onclick = () => res(v); row.appendChild(b); };
    btn("Approve", "primary", { allow: true }); btn("Deny", "", { allow: false }); btn("Always allow this task", "", { allow: true, always: true }); ap.appendChild(row); scroll();
    onStatus?.("Waiting for your approval"); stat.textContent = "· waiting for approval"; });
  let res = { status: "running" }, said = [], retries = 0;
  while (!stopped && (res.status === "running" || res.status === "needs_approval")) {
    try {
      if (res.status === "needs_approval") { const d = await askUser(res.pending); ap.hidden = true; wake = null; if (stopped || !d) break;
        if (d.always) { mode = "auto"; showMode(); } stat.textContent = "· running"; res = await api({ action: "approve", id: st.id, ...d }); }
      else { stat.textContent = `· step ${(res.step || 0) + 1}/${st.maxSteps}`; onStatus?.(`Cloud sandbox · step ${(res.step || 0) + 1}`); res = await api({ action: "step", id: st.id }); }
    } catch (e) { if (stopped) break;
      if (res.status === "running" && e.body?.retryable && (retries = (retries || 0) + 1) <= 2) { line("dim", "temporary error, retrying…"); await new Promise(r => setTimeout(r, 2500 * retries)); continue; }
      line("err", "error: " + e.message + (e.body?.ref ? " (ref " + e.body.ref + ")" : "")); res = { status: "failed" }; api({ action: "stop", id: st.id }).catch(() => {}); break; }
    retries = 0; if (res.mode && res.mode !== mode) { mode = res.mode; showMode(); } render(res.events); }
  const final = stopped ? "stopped" : res.status; card.classList.add("end", final); ap.hidden = true;
  stat.textContent = "· " + (final === "done" ? "finished, sandbox shut down" : final === "stopped" ? "stopped, sandbox shut down" : "failed"); $(".ctstop").remove();
  return { status: final, summary: res.summary || (stopped ? "Stopped." : said.at(-1) || ""), files: (res.files || []).map(f => ({ name: "/" + f.name, content: f.content })) };
}
// Simple file cards + zip for surfaces without their own card renderer.
export function fileCards(bub, files) { if (!files.length) return; const box = el("div", "files");
  const dl = (name, content) => { const a = document.createElement("a"); a.href = URL.createObjectURL(new Blob([content], { type: "text/plain" })); a.download = name.split("/").pop(); document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 4000); };
  for (const f of files) { const c = el("div", "fcard"); const h = el("div", "fhead"); const n = el("span", "fname", f.name.replace(/^\//, "")); const sz = el("span", "fsize", (new Blob([f.content]).size / 1024).toFixed(1) + " KB");
    const b = el("button", "primary fdl", "Download"); b.type = "button"; b.onclick = () => dl(f.name, f.content); h.append(n, sz, el("span", "fgrow"), b);
    const pre = el("pre", "fpreview"); const code = el("code", "", f.content.split("\n").slice(0, 12).join("\n")); pre.appendChild(code); c.append(h, pre); box.appendChild(c); }
  if (files.length > 1 && window.fflate) { const z = el("button", "zipall", `Download all (${files.length} files, .zip)`); z.type = "button";
    z.onclick = () => { const enc = new TextEncoder(), o = {}; for (const f of files) o[f.name.replace(/^\/+/, "")] = enc.encode(f.content); const a = document.createElement("a"); a.href = URL.createObjectURL(new Blob([window.fflate.zipSync(o)], { type: "application/zip" })); a.download = "wrenbyai-files.zip"; document.body.appendChild(a); a.click(); a.remove(); };
    box.appendChild(z); }
  bub.appendChild(box); }
export const CSS = `.ctask{margin-top:10px;border:1px solid #2a2a2a;border-radius:14px;background:#171717;max-width:680px;overflow:hidden}.cthead{display:flex;align-items:center;gap:8px;padding:10px 12px;font-size:13px;flex-wrap:wrap}.cthead .fgrow,.fhead .fgrow{flex:1}.ctstat,.ctmeta{color:#8a8a8a;font-size:12px}.ctmeta{padding:0 12px 8px;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px}.ctdot{width:8px;height:8px;border-radius:50%;background:#e8e8e8;animation:ctp 1s infinite alternate}.ctask.end .ctdot{animation:none;background:#4ade80}.ctask.end.stopped .ctdot,.ctask.end.failed .ctdot{background:#f59e0b}@keyframes ctp{to{opacity:.3}}.ctstop{height:28px;padding:0 12px;font-size:12px;border-radius:999px}.ctbadge{font-size:11px;padding:2px 8px;border-radius:999px;border:1px solid #333;color:#bbb}.ctbadge.auto{color:#f59e0b;border-color:#5a4210}.ctlog{max-height:340px;overflow:auto;border-top:1px solid #262626;padding:8px 12px;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px;white-space:pre-wrap;word-break:break-word}.ctl.cmd{color:#e8e8e8;margin-top:6px}.ctl.out{color:#b5b5b5}.ctl.out.bad,.ctl.err{color:#f59e0b}.ctl.dim{color:#7a7a7a}.ctl.say{font-family:inherit;margin:6px 0;color:#ddd}.ctask-approve{border-top:1px solid #262626;padding:10px 12px;background:#1d1d1d}.ctaq{font-size:13px;font-weight:600;margin-bottom:6px}.ctacmd{background:#0e0e0e;border:1px solid #2a2a2a;border-radius:10px;padding:8px 10px;font-size:12px;max-height:220px;overflow:auto;white-space:pre-wrap;margin:0 0 8px}.ctabtns{display:flex;gap:8px;flex-wrap:wrap}.ctabtns button{height:30px;padding:0 14px;border-radius:999px;font-size:12px}.ctabtns .primary{background:#f2f2f2;color:#111;border:0}.ctmode{display:inline-flex;border:1px solid #333;border-radius:999px;overflow:hidden;font-size:11px}.ctmode .ctm{border:0;border-radius:0;height:26px;padding:0 10px;background:transparent;color:#999;font-size:11px}.ctmode .ctm.on{background:#2c2c2c;color:#f2f2f2}.ctmode .ctl-short{display:none}@media(max-width:640px){.ctmode .ctl-long{display:none}.ctmode .ctl-short{display:inline}}.cthint{margin-top:8px;font-size:12px;color:#9a9a9a}`;
export function injectCSS() { if (document.getElementById("ctask-css")) return; const s = document.createElement("style"); s.id = "ctask-css"; s.textContent = CSS; document.head.appendChild(s); }
