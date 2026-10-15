// WrenbyAI auth + chat-history UI. Self-contained ES module.
// Enables itself only when /api/auth/me reports { configured:true } (the DB flag).
// Stores a bearer token in localStorage (for GitHub Pages and Tauri where the
// session cookie is cross-site) and also sends credentials for same-origin cookies.
const LS = "wy.token";
const S = { base: "", token: null, user: null, configured: false, chats: [], chatId: null, guest: [], opts: {}, els: {} };

function api(path, { method = "GET", body } = {}) {
  const headers = {};
  if (body !== undefined) headers["content-type"] = "application/json";
  if (S.token) headers["authorization"] = "Bearer " + S.token;
  return fetch(S.base + path, { method, headers, credentials: "include", body: body !== undefined ? JSON.stringify(body) : undefined });
}

export async function initAuth(opts) {
  S.opts = opts || {}; S.base = opts.base || ""; S.token = localStorage.getItem(LS) || null;
  let me; try { me = await (await api("/api/auth/me")).json(); } catch { me = { configured: false }; }
  S.configured = !!me.configured;
  if (!S.configured) return session();        // backend not ready: no auth UI at all
  injectStyle(); buildChrome();
  S.user = me.user || null; render();
  if (S.user) await refreshChats();
  return session();
}

function session() {
  return {
    get enabled() { return S.configured; },
    get signedIn() { return !!S.user; },
    get chatId() { return S.chatId; },
    startNewChat() { S.chatId = null; markActive(); },
    async saveTurn(turn) { return saveTurn(turn); },
  };
}

// ---- persistence ----
async function saveTurn({ user, assistant, files, mode }) {
  if (!S.configured) return;
  const msgs = [{ role: "user", content: user }, { role: "assistant", content: assistant, files: files || null }];
  if (!S.user) { S.guest.push(...msgs); return; }            // guest: buffer for import on sign-in
  try {
    if (!S.chatId) {
      const r = await api("/api/chats", { method: "POST", body: { action: "create", title: titleOf(user), mode: mode || "forge", messages: msgs } });
      const j = await r.json(); if (j.chat) S.chatId = j.chat.id;
    } else {
      await api("/api/chats", { method: "POST", body: { action: "append", id: S.chatId, messages: msgs } });
    }
    await refreshChats();
  } catch (e) { console.warn("saveTurn failed", e); }
}
const titleOf = (t) => (String(t || "New chat").replace(/\s+/g, " ").trim().slice(0, 60)) || "New chat";

async function refreshChats() {
  try { const j = await (await api("/api/chats")).json(); S.chats = j.chats || []; renderList(); } catch {}
}

// ---- UI chrome ----
function buildChrome() {
  const header = document.querySelector("header");
  // hamburger (mobile) + auth control, appended to header
  const hamb = el("button", "wy-hamb", "\u2630"); hamb.title = "Chats"; hamb.onclick = () => S.els.bar.classList.toggle("open");
  header.insertBefore(hamb, header.firstChild);
  const slot = el("span", "wy-authslot"); slot.style.marginLeft = "8px"; header.appendChild(slot); S.els.slot = slot;
  // sidebar
  const bar = el("aside"); bar.id = "histbar"; bar.hidden = true;
  bar.innerHTML = '<div class="hhead"><span class="htitle">Chats</span><button class="hnew">New chat</button></div><div class="hlist"></div>';
  document.body.insertBefore(bar, document.body.firstChild);
  bar.querySelector(".hnew").onclick = () => { S.chatId = null; if (S.opts.onNewChat) S.opts.onNewChat(); markActive(); if (window.innerWidth <= 860) bar.classList.remove("open"); };
  S.els.bar = bar; S.els.list = bar.querySelector(".hlist");
}

function render() {
  const slot = S.els.slot; slot.innerHTML = "";
  if (S.user) {
    S.els.bar.hidden = false;
    const av = el("button", "wy-avatar", (S.user.email[0] || "?").toUpperCase());
    av.title = S.user.email; av.onclick = toggleMenu; slot.appendChild(av);
  } else {
    S.els.bar.hidden = true;
    const b = el("button", "", "Sign in"); b.onclick = openSignin; slot.appendChild(b);
  }
}

function toggleMenu() {
  let m = document.querySelector(".wy-menu"); if (m) { m.remove(); return; }
  m = el("div", "wy-menu");
  m.innerHTML = '<div class="wy-email"></div>';
  m.querySelector(".wy-email").textContent = S.user.email;
  const out = el("button", "", "Log out"); out.onclick = logout; m.appendChild(out);
  document.body.appendChild(m);
  setTimeout(() => document.addEventListener("click", function h(e) { if (!m.contains(e.target) && !e.target.classList.contains("wy-avatar")) { m.remove(); document.removeEventListener("click", h); } }), 0);
}

async function logout() {
  try { await api("/api/auth/logout", { method: "POST" }); } catch {}
  localStorage.removeItem(LS); S.token = null; S.user = null; S.chats = []; S.chatId = null;
  document.querySelector(".wy-menu")?.remove(); render(); renderList();
  if (S.opts.onNewChat) S.opts.onNewChat();
}

function renderList() {
  const list = S.els.list; if (!list) return; list.innerHTML = "";
  if (!S.chats.length) { const e = el("div", "hempty", "No chats yet"); e.style.cssText = "padding:10px;color:var(--muted);font-size:12px"; list.appendChild(e); return; }
  for (const c of S.chats) {
    const row = el("div", "hitem" + (c.id === S.chatId ? " on" : ""));
    const name = el("span", "hname", c.title || "New chat"); row.appendChild(name);
    const act = el("span", "hact");
    const rn = el("button", "", "\u270e"); rn.title = "Rename"; rn.onclick = (e) => { e.stopPropagation(); renameChat(c); };
    const dl = el("button", "", "\u00d7"); dl.title = "Delete"; dl.onclick = (e) => { e.stopPropagation(); deleteChat(c); };
    act.append(rn, dl); row.appendChild(act);
    row.onclick = () => loadChat(c.id);
    list.appendChild(row);
  }
}
function markActive() { document.querySelectorAll(".hitem").forEach(r => r.classList.remove("on")); renderList(); }

async function loadChat(id) {
  try {
    const j = await (await api("/api/chats?id=" + encodeURIComponent(id))).json();
    if (!j.chat) return;
    S.chatId = id; markActive();
    if (S.opts.onLoadChat) S.opts.onLoadChat(j.messages || [], j.chat);
    if (window.innerWidth <= 860) S.els.bar.classList.remove("open");
  } catch (e) { console.warn("loadChat failed", e); }
}

async function renameChat(c) {
  const t = prompt("Rename chat", c.title || ""); if (t == null) return;
  try { await api("/api/chats", { method: "POST", body: { action: "rename", id: c.id, title: t } }); await refreshChats(); } catch {}
}
async function deleteChat(c) {
  if (!confirm("Delete this chat?")) return;
  try { await api("/api/chats", { method: "POST", body: { action: "delete", id: c.id } });
    if (S.chatId === c.id) { S.chatId = null; if (S.opts.onNewChat) S.opts.onNewChat(); }
    await refreshChats(); } catch {}
}

// ---- sign-in dialog ----
function openSignin() {
  const dlg = document.createElement("dialog"); dlg.className = "wy-dialog";
  dlg.innerHTML = `<div class="wy-dh">Sign in to WrenbyAI</div><div class="wy-db">
    <div class="wy-hint">We'll email you a 6-digit code. No password needed.</div>
    <input class="wy-email-in" type="email" placeholder="you@example.com" autocomplete="email">
    <input class="wy-code-in" type="text" inputmode="numeric" maxlength="6" placeholder="6-digit code" hidden>
    <div class="wy-err"></div>
    <div class="wy-df"><button class="wy-cancel">Cancel</button><button class="primary wy-go">Send code</button></div></div>`;
  document.body.appendChild(dlg); dlg.showModal();
  const emailIn = dlg.querySelector(".wy-email-in"), codeIn = dlg.querySelector(".wy-code-in");
  const err = dlg.querySelector(".wy-err"), go = dlg.querySelector(".wy-go");
  dlg.querySelector(".wy-cancel").onclick = () => dlg.close();
  dlg.addEventListener("close", () => dlg.remove());
  let stage = "email", email = "";
  emailIn.focus();
  go.onclick = async () => {
    err.textContent = ""; go.disabled = true;
    try {
      if (stage === "email") {
        email = emailIn.value.trim().toLowerCase();
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) { err.textContent = "Enter a valid email."; go.disabled = false; return; }
        const r = await api("/api/auth/start", { method: "POST", body: { email } });
        if (!r.ok) { err.textContent = (await r.json().catch(() => ({}))).error || "Could not send code."; go.disabled = false; return; }
        stage = "code"; emailIn.disabled = true; codeIn.hidden = false; codeIn.focus(); go.textContent = "Verify";
      } else {
        const code = codeIn.value.trim();
        const r = await api("/api/auth/verify", { method: "POST", body: { email, code } });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) { err.textContent = j.error || "Incorrect code."; go.disabled = false; return; }
        S.token = j.token || null; if (S.token) localStorage.setItem(LS, S.token);
        S.user = j.user; dlg.close(); render(); await refreshChats();
        await offerImport();
      }
    } catch (e) { err.textContent = "Something went wrong. Try again."; }
    go.disabled = false;
  };
  codeIn.addEventListener("keydown", e => { if (e.key === "Enter") go.click(); });
  emailIn.addEventListener("keydown", e => { if (e.key === "Enter") go.click(); });
}

async function offerImport() {
  if (!S.guest.length) return;
  if (!confirm("Import your current guest chat into your account?")) { S.guest = []; return; }
  try {
    const first = S.guest.find(m => m.role === "user");
    const r = await api("/api/chats", { method: "POST", body: { action: "create", title: titleOf(first && first.content), mode: (S.opts.getMode && S.opts.getMode()) || "forge", messages: S.guest } });
    const j = await r.json(); S.guest = []; await refreshChats();
    if (j.chat) S.chatId = j.chat.id, markActive();
  } catch (e) { console.warn("import failed", e); }
}

// ---- helpers ----
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

function injectStyle() {
  if (document.getElementById("wy-auth-style")) return;
  const s = document.createElement("style"); s.id = "wy-auth-style";
  s.textContent = `
#histbar{flex:0 0 240px;display:flex;flex-direction:column;border-right:1px solid var(--rule);background:var(--bg);height:100vh;overflow:hidden}
#histbar[hidden]{display:none!important}
.hhead{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid var(--rule);min-height:56px}
.hhead .htitle{font-weight:600;font-size:13px;color:var(--muted)}.hnew{margin-left:auto}
.hlist{flex:1;overflow:auto;padding:6px}
.hitem{display:flex;align-items:center;gap:6px;padding:7px 8px;border-radius:4px;cursor:pointer;font-size:13px;color:var(--fg)}
.hitem:hover{background:var(--card)}.hitem.on{background:var(--card);border:1px solid var(--rule)}
.hitem .hname{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.hitem .hact{opacity:0;display:flex;gap:2px;flex:none}.hitem:hover .hact{opacity:1}
.hact button{height:22px;width:22px;padding:0;font-size:12px;border:0;background:transparent;color:var(--muted)}
.hact button:hover{color:var(--fg);background:transparent}
.wy-avatar{width:30px;height:30px;border-radius:50%;background:var(--accent);color:#111;font-weight:700;border:0;display:grid;place-items:center;cursor:pointer;padding:0;font-size:13px}
.wy-avatar:hover{background:#ff7d45}
.wy-menu{position:absolute;right:16px;top:54px;background:var(--card);border:1px solid var(--rule);border-radius:6px;min-width:200px;z-index:80;padding:6px;box-shadow:0 8px 30px rgba(0,0,0,.5)}
.wy-menu .wy-email{font-size:12px;color:var(--muted);padding:8px 10px;border-bottom:1px solid var(--rule);overflow:hidden;text-overflow:ellipsis}
.wy-menu button{width:100%;text-align:left;border:0;background:transparent;height:34px;border-radius:4px}
.wy-menu button:hover{background:#2a2a2a}
.wy-hamb{display:none}
.wy-dialog{border:1px solid var(--rule);border-radius:8px;background:var(--card);color:var(--fg);padding:0;width:340px;max-width:92vw}
.wy-dialog::backdrop{background:rgba(0,0,0,.5)}
.wy-dh{padding:16px 18px;border-bottom:1px solid var(--rule);font-weight:600}
.wy-db{padding:18px}.wy-hint{font-size:12px;color:var(--muted);margin-bottom:12px}
.wy-dialog input{width:100%;background:var(--bg);border:1px solid var(--rule);color:var(--fg);border-radius:4px;height:40px;padding:0 12px;font:inherit;outline:none;margin-bottom:10px}
.wy-dialog input:focus{border-color:#555}.wy-dialog input:disabled{opacity:.6}
.wy-err{color:#e06a5a;font-size:12px;min-height:16px;margin-bottom:8px}
.wy-df{display:flex;gap:8px;justify-content:flex-end}
.wy-dialog .primary{background:var(--accent);border:0;color:#111;font-weight:600}.wy-dialog .primary:hover{background:#ff7d45}
@media(max-width:860px){.wy-hamb{display:inline-flex}#histbar{position:fixed;top:0;left:0;bottom:0;width:260px;z-index:70;flex-basis:260px;transform:translateX(-100%);transition:transform .2s;box-shadow:8px 0 30px rgba(0,0,0,.5)}#histbar.open{transform:none}}
`;
  document.head.appendChild(s);
}

// Used by cloud tasks.
export const authInfo = () => ({ base: S.base, token: S.token, user: S.user });
