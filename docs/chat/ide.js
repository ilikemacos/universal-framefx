// WyneAI IDE: code artifacts + file tree + CodeMirror 6 editor + in-browser runner
// (HTML/CSS/JS, TS/JSX via sucrase, Python via Pyodide) + IndexedDB projects + edit_file diffs.
// Monochrome; everything bundled/self-hosted, no CDN at runtime.
let H = {};                       // hooks: { onFix, runShell, isDesktop }
let els = {};                     // panel elements
const files = new Map();          // name -> { content, versions: [{content, at}] }
let active = null;
let lastError = null;
let cm = null;                    // CodeMirror wrapper instance
let syncing = false;              // true while we set editor text programmatically

const PY_BASE = (typeof window !== "undefined" && window.WY_PYODIDE_BASE) || "https://preset-ai-chat.vercel.app/pyodide/";

const $ = (t, c, txt) => { const e = document.createElement(t); if (c) e.className = c; if (txt != null) e.textContent = txt; return e; };
const fmtBytes = b => b < 1024 ? b + " B" : b < 1048576 ? (b/1024).toFixed(b<10240?1:0)+" KB" : (b/1048576).toFixed(2)+" MB";
const bytesOf = s => new TextEncoder().encode(s).length;
const runnable = n => /\.(html?|m?js|ts|jsx|tsx|css|py)$/i.test(n || "");
const hasHTML = () => [...files.keys()].find(n => /\.html?$/i.test(n));
const isJsLike = n => /\.(m?js|jsx|tsx?|ts)$/i.test(n || "");

export function initIDE(hooks) { H = hooks || {}; injectStyle(); buildPanel(); loadEditor(); restoreWidth();
  document.addEventListener("keydown", e => { if (e.key === "Escape" && els.side && !els.side.hidden) { closePanel(); e.stopPropagation(); } });
  window.addEventListener("message", e => { const d = e.data; if (!d || d.__wy !== 1) return; appendConsole(d.text, d.level); if (d.level === "error") { lastError = d.text; showFix(); } }); }

// ---------- lazy CodeMirror ----------
let cmMod = null;
async function loadEditor() {
  try { cmMod = await import("./cm/cm.bundle.js");
    cm = cmMod.createEditor(els.cmhost, { doc: active ? files.get(active).content : "", filename: active || "",
      onChange: v => { if (syncing) return; const f = files.get(active); if (f && !f._ro) f.content = v; } });
    els.editor.hidden = true; els.cmhost.hidden = false; if (active) showActive();
  } catch (e) { console.warn("CodeMirror failed to load, using plain editor", e); els.cmhost.hidden = true; els.editor.hidden = false; }
}

// ---------- panel ----------
function buildPanel() {
  const side = $("aside"); side.id = "side"; side.hidden = true;
  side.innerHTML = '<div class="shead"><span class="stitle">Artifacts</span><span class="sgrow"></span>'
    + '<button class="bsave" title="Save project">Save</button><select class="bopen" title="Open a saved project"><option value="">Open\u2026</option></select>'
    + '<button class="sx" title="Close">\u00d7</button></div>'
    + '<div class="stabs"></div>'
    + '<div class="stool"><button class="brun primary">\u25b6 Run</button><button class="bcopy">Copy</button><button class="bdl">Download</button><button class="bzip" hidden>Download all (zip)</button><span class="sgrow"></span><label class="vlabel">Version <select class="vsel"></select></label><button class="bdiff" hidden>Diff</button></div>'
    + '<div class="sbody"><div class="ideflex"><div class="stree"></div><div class="seditcol"><div class="cmhost"></div><textarea class="seditor" spellcheck="false" wrap="off"></textarea><div class="sdiff" hidden></div></div></div></div>'
    + '<div class="spreviewwrap" hidden><iframe class="spreview" sandbox="allow-scripts allow-modals" title="Preview"></iframe></div>'
    + '<div class="sconsolewrap" hidden><div class="scbar">Console<button class="scclear">Clear</button><button class="bfix" hidden>Fix with WyneAI</button></div><div class="sconsole"></div></div>';
  const resizer = $("div", "sresize"); resizer.title = "Drag to resize"; side.appendChild(resizer);
  document.body.appendChild(side);
  setupResizer(resizer, side);
  els = { side, tabs: side.querySelector(".stabs"), tree: side.querySelector(".stree"),
    cmhost: side.querySelector(".cmhost"), editor: side.querySelector(".seditor"), diff: side.querySelector(".sdiff"),
    run: side.querySelector(".brun"), copy: side.querySelector(".bcopy"), dl: side.querySelector(".bdl"), zip: side.querySelector(".bzip"),
    vsel: side.querySelector(".vsel"), vlabel: side.querySelector(".vlabel"), diffBtn: side.querySelector(".bdiff"),
    save: side.querySelector(".bsave"), open: side.querySelector(".bopen"),
    previewWrap: side.querySelector(".spreviewwrap"), preview: side.querySelector(".spreview"),
    consoleWrap: side.querySelector(".sconsolewrap"), console: side.querySelector(".sconsole"), fix: side.querySelector(".bfix") };
  els.cmhost.hidden = true; // until CM loads
  side.querySelector(".sx").onclick = closePanel;
  els.run.onclick = runActive;
  els.copy.onclick = e => { const f = files.get(active); if (!f) return; navigator.clipboard.writeText(f.content).then(() => { e.target.textContent = "Copied"; setTimeout(() => e.target.textContent = "Copy", 1200); }).catch(() => {}); };
  els.dl.onclick = () => downloadOne(active);
  els.zip.onclick = downloadZip;
  els.editor.oninput = () => { const f = files.get(active); if (f && !f._ro) f.content = els.editor.value; };
  els.vsel.onchange = () => showVersion(els.vsel.value);
  els.diffBtn.onclick = toggleDiff;
  els.save.onclick = doSaveProject;
  els.open.onmousedown = refreshProjects; els.open.onchange = () => { if (els.open.value) doLoadProject(els.open.value); };
  side.querySelector(".scclear").onclick = () => { els.console.textContent = ""; lastError = null; els.fix.hidden = true; };
  els.fix.onclick = () => { if (H.onFix && lastError) H.onFix(active, lastError); };
}

function injectStyle() {
  if (document.getElementById("ide-style")) return;
  const s = document.createElement("style"); s.id = "ide-style";
  s.textContent = `
  #side .ideflex{display:flex;height:100%;min-height:0}
  #side .stree{width:150px;flex:0 0 auto;overflow:auto;border-right:1px solid var(--line,#2a2a2a);font-size:12px;padding:4px 0}
  #side .stree:empty{display:none}
  #side .seditcol{flex:1 1 auto;min-width:0;position:relative;display:flex;flex-direction:column}
  #side .cmhost{flex:1 1 auto;min-height:0;overflow:hidden}
  #side .cmhost .cm-editor{height:100%}
  #side .trow{padding:2px 8px;white-space:nowrap;cursor:pointer;color:var(--fg,#ddd);overflow:hidden;text-overflow:ellipsis}
  #side .trow.tdir{color:var(--muted,#888);cursor:default}
  #side .trow.tfile:hover{background:var(--panel2,#1b1b1b)}
  #side .trow.tfile.on{background:var(--accent,#ff6a2b);color:#111}
  #side .bsave,#side .bopen{font-size:12px;margin-right:6px}`;
  document.head.appendChild(s);
}

export function openPanel() { els.side.hidden = false; document.body.classList.add("side-open"); }
export function closePanel() { els.side.hidden = true; document.body.classList.remove("side-open"); }

export function setFiles(list, { open = true, replace = false } = {}) {
  if (replace) { files.clear(); active = null; }
  if (!Array.isArray(list) || !list.length) { if (replace) { renderTabs(); renderTree(); } return; }
  for (const it of list) { const name = String(it.name || "file"); const content = String(it.content ?? "");
    const ex = files.get(name);
    if (ex) { if (ex.content !== content) { ex.versions.push({ content: ex.content, at: Date.now() }); ex.content = content; } }
    else files.set(name, { content, versions: [] }); }
  active = String(list[0].name || active || [...files.keys()][0]);
  renderTabs(); renderTree(); showActive(); if (open) openPanel();
}

// deterministic targeted edit (edit_file tool). Fixes the flaky full-file round-trip.
export function applyEdit({ filename, search, replace, all } = {}) {
  const name = filename && files.has(filename) ? filename : (files.has(String(filename)) ? String(filename) : active);
  const f = files.get(name);
  if (!f) return { ok: false, error: "no such file open: " + (filename || "(none)") };
  if (search == null || search === "") return { ok: false, error: "search text is required" };
  const before = f.content; const rep = String(replace ?? "");
  let after, replaced;
  if (all) { const parts = before.split(search); replaced = parts.length - 1; after = parts.join(rep); }
  else { const i = before.indexOf(search); if (i < 0) return { ok: false, error: "search text not found in " + name }; replaced = 1; after = before.slice(0, i) + rep + before.slice(i + search.length); }
  if (!replaced) return { ok: false, error: "search text not found in " + name };
  f.versions.push({ content: before, at: Date.now() }); f.content = after;
  active = name; renderTabs(); renderTree(); showActive();
  // auto-show the diff for this edit
  els.vsel.value = String(f.versions.length - 1); showDiff(f.versions.length - 1);
  openPanel();
  return { ok: true, file: name, replaced };
}

export function harvest(bubEl) {
  const bigs = [...bubEl.querySelectorAll("pre > code")].filter(c => c.textContent.split("\n").length > 8);
  if (!bigs.length) return;
  const made = [];
  bigs.forEach((code, i) => { const lang = (code.className.match(/language-(\w+)/) || [])[1] || "txt";
    const ext = { python:"py", javascript:"js", typescript:"ts", bash:"sh", xml:"html", markdown:"md" }[lang] || lang;
    const name = pickName(ext, i); made.push({ name, content: code.textContent.replace(/\n$/, "") });
    const chip = $("button", "artchip", "\u25a3 " + name + " \u2014 open in panel"); chip.type = "button";
    chip.onclick = () => { openPanel(); selectFile(name); }; code.parentElement.replaceWith(chip); });
  setFiles(made, { open: true });
}
function pickName(ext, i) { const base = ext === "html" ? "index" : ext === "py" ? "main" : "snippet"; let n = base + "." + ext, k = 1;
  while (files.has(n) && i > 0) n = base + "-" + (++k) + "." + ext; return files.has(n) && base !== "index" ? base + "-" + (files.size + 1) + "." + ext : n; }

export function contextForModel() { const f = files.get(active); if (!f) return "";
  return "[The user is editing this file in the WyneAI editor. Current contents of " + active + ":]\n```\n" + f.content.slice(0, 8000) + "\n```\n[To change it, call edit_file with an exact search/replace. Use create_file only for a new file or a full rewrite.]"; }
export function hasFiles() { return files.size > 0; }

function renderTabs() { els.tabs.innerHTML = "";
  for (const name of files.keys()) { const t = $("button", "stab" + (name === active ? " on" : ""), name); t.type = "button";
    t.onclick = () => selectFile(name); els.tabs.appendChild(t); }
  els.zip.hidden = files.size < 2; }

function renderTree() {
  els.tree.innerHTML = "";
  const root = { dirs: {}, files: [] };
  for (const n of files.keys()) { const parts = n.split("/"); let node = root;
    for (let i = 0; i < parts.length - 1; i++) { node.dirs[parts[i]] = node.dirs[parts[i]] || { dirs: {}, files: [] }; node = node.dirs[parts[i]]; }
    node.files.push({ label: parts[parts.length - 1], full: n }); }
  const walk = (node, depth) => {
    for (const d of Object.keys(node.dirs).sort()) { const row = $("div", "trow tdir", "\u25be " + d); row.style.paddingLeft = (depth * 12 + 6) + "px"; els.tree.appendChild(row); walk(node.dirs[d], depth + 1); }
    for (const f of node.files.sort((a, b) => a.label.localeCompare(b.label))) { const row = $("div", "trow tfile" + (f.full === active ? " on" : ""), f.label); row.style.paddingLeft = (depth * 12 + 6) + "px"; row.onclick = () => selectFile(f.full); els.tree.appendChild(row); }
  };
  walk(root, 0);
}

function selectFile(name) { if (!files.has(name)) return; active = name; renderTabs(); renderTree(); showActive(); }

function setEditor(text, ro) { syncing = true;
  if (cm) { cm.setValue(text); cm.setLang(active || ""); cm.setReadOnly(!!ro); }
  else { els.editor.value = text; els.editor.readOnly = !!ro; }
  syncing = false; }

function showActive() { const f = files.get(active); if (!f) return; f._ro = false;
  els.diff.hidden = true; if (cm) els.cmhost.hidden = false; else els.editor.hidden = false;
  setEditor(f.content, false);
  els.vsel.innerHTML = ""; const cur = $("option", null, "Current"); cur.value = "cur"; els.vsel.appendChild(cur);
  f.versions.forEach((v, i) => { const o = $("option", null, "v" + (i + 1)); o.value = String(i); els.vsel.appendChild(o); });
  els.vsel.value = "cur"; els.vlabel.style.display = f.versions.length ? "" : "none"; els.diffBtn.hidden = !f.versions.length;
  els.diffBtn.textContent = "Diff"; updateRunUI(); }

function showVersion(v) { const f = files.get(active); if (!f) return;
  els.diff.hidden = true; if (cm) els.cmhost.hidden = false; else els.editor.hidden = false;
  if (v === "cur") { f._ro = false; setEditor(f.content, false); return; }
  const old = f.versions[+v]; if (!old) return; f._ro = true; setEditor(old.content, true); }

function updateRunUI() { const can = runnable(active) || hasHTML(); els.run.disabled = !can; els.run.style.display = can ? "" : "none"; }

function appendConsole(text, level) { els.consoleWrap.hidden = false;
  const line = $("div", "cl " + (level || "log"), text); els.console.appendChild(line); els.console.scrollTop = 1e9; }
function showFix() { els.fix.hidden = !H.onFix; }

const FORWARDER = "(function(){function s(l,a){try{parent.postMessage({__wy:1,level:l,text:a.map(function(x){try{return typeof x==='object'?JSON.stringify(x):String(x)}catch(e){return String(x)}}).join(' ')},'*')}catch(e){}}['log','info','warn','error','debug'].forEach(function(k){var o=console[k]?console[k].bind(console):function(){};console[k]=function(){o.apply(null,arguments);s(k==='debug'?'log':k,[].slice.call(arguments))}});window.addEventListener('error',function(e){s('error',[e.message+(e.filename&&e.lineno?' ('+e.lineno+':'+e.colno+')':'')])});window.addEventListener('unhandledrejection',function(e){s('error',['Unhandled rejection: '+((e.reason&&e.reason.stack)||e.reason)])});})();";

async function compileJS(name, code) {
  if (!/\.(tsx?|jsx)$/i.test(name)) return code;
  try { const m = await import("./vendor/sucrase.bundle.js"); return m.transformCode(code, { jsx: true, ts: /\.tsx?$/i.test(name) }); }
  catch (e) { appendConsole("Could not load the TS/JSX compiler: " + (e.message || e), "error"); return code; }
}

async function runActive() { lastError = null; els.fix.hidden = true; els.console.textContent = "";
  const name = active;
  if (/\.py$/i.test(name)) return runPython(files.get(name).content);
  if (isJsLike(name)) return runJS(name, files.get(name).content);
  if (/\.html?$/i.test(name) || /\.css$/i.test(name) || hasHTML()) return runHTML();
  appendConsole("No in-browser runner for this file type yet.", "muted"); }

async function runHTML() { const htmlName = /\.html?$/i.test(active) ? active : hasHTML();
  let doc = files.get(htmlName).content;
  const css = [...files].filter(([n]) => /\.css$/i.test(n)).map(([, f]) => f.content).join("\n");
  const jsFiles = [...files].filter(([n]) => isJsLike(n));
  const compiled = await Promise.all(jsFiles.map(async ([n, f]) => await compileJS(n, f.content)));
  const js = compiled.join("\n;\n");
  const head = "<script>" + FORWARDER + "<\/script>" + (css ? "<style>" + css + "<\/style>" : "");
  doc = /<\/head>/i.test(doc) ? doc.replace(/<\/head>/i, head + "</head>") : head + doc;
  if (js && !isJsLike(active)) { const tag = "<script>" + js + "<\/script>"; doc = /<\/body>/i.test(doc) ? doc.replace(/<\/body>/i, tag + "</body>") : doc + tag; }
  els.previewWrap.hidden = false; els.consoleWrap.hidden = false; els.preview.srcdoc = doc;
  appendConsole("Rendered " + htmlName + ".", "muted"); }

async function runJS(name, src) { const code = await compileJS(name, src);
  const doc = "<!doctype html><meta charset=utf-8><script>" + FORWARDER + "<\/script><script>try{\n" + code + "\n}catch(e){parent.postMessage({__wy:1,level:'error',text:(e&&e.stack)||String(e)},'*')}<\/script>";
  els.previewWrap.hidden = true; els.consoleWrap.hidden = false; els.preview.srcdoc = doc; }

// ---------- Python via self-hosted Pyodide (lazy) ----------
let pyPromise = null;
function getPy() { if (pyPromise) return pyPromise;
  pyPromise = (async () => { const mod = await import(PY_BASE + "pyodide.mjs"); return mod.loadPyodide({ indexURL: PY_BASE }); })()
    .catch(e => { pyPromise = null; throw e; });
  return pyPromise; }
async function runPython(code) { els.previewWrap.hidden = true; els.consoleWrap.hidden = false;
  appendConsole("Loading Python\u2026", "muted");
  let py; try { py = await getPy(); } catch (e) { const m = "Python runtime failed to load: " + (e.message || e); appendConsole(m, "error"); lastError = m; showFix(); return; }
  try { py.setStdout({ batched: s => appendConsole(s, "log") }); py.setStderr({ batched: s => appendConsole(s, "error") });
    await py.runPythonAsync(code); appendConsole("\u2014 done \u2014", "muted"); }
  catch (e) { const m = (e && e.message) || String(e); appendConsole(m, "error"); lastError = m; showFix(); } }

// ---------- download ----------
function downloadOne(name) { const f = files.get(name); if (!f) return; dl(new Blob([f.content], { type: "text/plain" }), name); }
function downloadZip() { const dict = {}; for (const [n, f] of files) dict[n] = new TextEncoder().encode(f.content);
  const zip = self.fflate.zipSync(dict, { level: 6 }); dl(new Blob([zip], { type: "application/zip" }), "artifacts.zip"); }
function dl(blob, name) { const u = URL.createObjectURL(blob); const a = $("a"); a.href = u; a.download = name; document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(u), 5000); }

// ---------- IndexedDB projects ----------
function idb() { return new Promise((res, rej) => { const r = indexedDB.open("wyneai-ide", 1);
  r.onupgradeneeded = () => { if (!r.result.objectStoreNames.contains("projects")) r.result.createObjectStore("projects", { keyPath: "name" }); };
  r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); }); }
async function doSaveProject() { if (!files.size) { appendConsole("Nothing to save yet.", "muted"); openPanel(); return; }
  const name = (prompt("Save project as:", "project") || "").trim(); if (!name) return;
  try { const db = await idb(); await new Promise((res, rej) => { const tx = db.transaction("projects", "readwrite");
    tx.objectStore("projects").put({ name, at: Date.now(), files: [...files].map(([n, f]) => ({ name: n, content: f.content })) });
    tx.oncomplete = res; tx.onerror = () => rej(tx.error); });
    appendConsole("Saved project \u201c" + name + "\u201d (" + files.size + " files).", "muted"); openPanel(); }
  catch (e) { appendConsole("Save failed: " + (e.message || e), "error"); } }
async function refreshProjects() { try { const db = await idb();
  const list = await new Promise(res => { const tx = db.transaction("projects"); const rq = tx.objectStore("projects").getAll(); rq.onsuccess = () => res(rq.result || []); rq.onerror = () => res([]); });
  els.open.innerHTML = '<option value="">Open\u2026</option>';
  list.sort((a, b) => (b.at || 0) - (a.at || 0)).forEach(p => { const o = $("option", null, p.name + " (" + (p.files ? p.files.length : 0) + ")"); o.value = p.name; els.open.appendChild(o); }); }
  catch {} }
async function doLoadProject(name) { try { const db = await idb();
  const p = await new Promise(res => { const tx = db.transaction("projects"); const rq = tx.objectStore("projects").get(name); rq.onsuccess = () => res(rq.result); rq.onerror = () => res(null); });
  if (!p || !p.files) { appendConsole("Project not found.", "error"); return; }
  setFiles(p.files, { open: true, replace: true }); appendConsole("Opened project \u201c" + name + "\u201d.", "muted"); els.open.value = ""; }
  catch (e) { appendConsole("Open failed: " + (e.message || e), "error"); } }

// ---------- tiny line diff (LCS) for version / edit view ----------
function lineDiff(a, b) { const A = a.split("\n"), B = b.split("\n"), n = A.length, m = B.length;
  const dp = Array.from({ length: n + 1 }, () => new Array(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) dp[i][j] = A[i] === B[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const out = []; let i = 0, j = 0;
  while (i < n && j < m) { if (A[i] === B[j]) { out.push([" ", A[i]]); i++; j++; } else if (dp[i + 1][j] >= dp[i][j + 1]) { out.push(["-", A[i++]]); } else { out.push(["+", B[j++]]); } }
  while (i < n) out.push(["-", A[i++]]); while (j < m) out.push(["+", B[j++]]); return out; }
function showDiff(vIdx) { const f = files.get(active); if (!f || !f.versions[vIdx]) return;
  const prev = f.versions[vIdx].content;
  els.diff.innerHTML = ""; for (const [sign, line] of lineDiff(prev, f.content)) { const d = $("div", "dl" + (sign === "+" ? " add" : sign === "-" ? " del" : ""), (sign === " " ? "  " : sign + " ") + line); els.diff.appendChild(d); }
  els.diff.hidden = false; if (cm) els.cmhost.hidden = true; else els.editor.hidden = true; els.diffBtn.textContent = "Hide diff"; }
function toggleDiff() { const f = files.get(active); if (!f || !f.versions.length) return;
  if (!els.diff.hidden) { els.diff.hidden = true; if (cm) els.cmhost.hidden = false; else els.editor.hidden = false; els.diffBtn.textContent = "Diff"; return; }
  const v = els.vsel.value === "cur" ? f.versions.length - 1 : +els.vsel.value; showDiff(v); }


// ---------- resize / reopen ----------
function setupResizer(handle, side) {
  let startX = 0, startW = 0, dragging = false;
  const onMove = e => { if (!dragging) return; const dx = startX - (e.touches ? e.touches[0].clientX : e.clientX);
    const max = Math.max(360, window.innerWidth - 360); const w = Math.min(max, Math.max(320, startW + dx));
    side.style.flexBasis = w + "px"; side.style.width = w + "px"; };
  const onUp = () => { if (!dragging) return; dragging = false; handle.classList.remove("drag");
    document.body.style.userSelect = ""; try { localStorage.setItem("wy.sidew", String(parseInt(side.style.flexBasis) || 0)); } catch {} window.removeEventListener("mousemove", onMove); window.removeEventListener("mouseup", onUp); window.removeEventListener("touchmove", onMove); window.removeEventListener("touchend", onUp); };
  const onDown = e => { dragging = true; handle.classList.add("drag"); startX = e.touches ? e.touches[0].clientX : e.clientX; startW = side.getBoundingClientRect().width; document.body.style.userSelect = "none"; e.preventDefault();
    window.addEventListener("mousemove", onMove); window.addEventListener("mouseup", onUp); window.addEventListener("touchmove", onMove, { passive: false }); window.addEventListener("touchend", onUp); };
  handle.addEventListener("mousedown", onDown); handle.addEventListener("touchstart", onDown, { passive: false });
}
function restoreWidth() { try { const w = parseInt(localStorage.getItem("wy.sidew") || "0"); if (w >= 320 && w <= window.innerWidth - 360 && els.side) { els.side.style.flexBasis = w + "px"; els.side.style.width = w + "px"; } } catch {} }

// open a file in the panel from a chat code card
export function openFile(name) { if (name && files.has(name)) selectFile(name); openPanel(); }
