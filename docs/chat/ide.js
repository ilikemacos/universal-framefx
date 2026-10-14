// WyneAI code artifacts + in-browser runner (HTML/CSS/JS). Monochrome, local-only. Uses self.fflate for zip.
let H = {};                       // hooks: { onFix, runShell, isDesktop }
let els = {};                     // panel elements
const files = new Map();          // name -> { content, versions: [{content, at}] }
let active = null;
let lastError = null;

const $ = (t, c, txt) => { const e = document.createElement(t); if (c) e.className = c; if (txt != null) e.textContent = txt; return e; };
const extOf = n => (n.split(".").pop() || "").toLowerCase();
const HLANG = { py:"python", js:"javascript", mjs:"javascript", ts:"typescript", jsx:"javascript", tsx:"typescript", sh:"bash", json:"json", html:"xml", htm:"xml", xml:"xml", css:"css", md:"markdown", yml:"yaml", yaml:"yaml", c:"c", cpp:"cpp", cs:"csharp", java:"java", rs:"rust", go:"go", sql:"sql" };
const fmtBytes = b => b < 1024 ? b + " B" : b < 1048576 ? (b/1024).toFixed(b<10240?1:0)+" KB" : (b/1048576).toFixed(2)+" MB";
const bytesOf = s => new TextEncoder().encode(s).length;
const runnable = n => /\.(html?|m?js|ts|jsx|tsx|css)$/i.test(n);
const hasHTML = () => [...files.keys()].find(n => /\.html?$/i.test(n));

export function initIDE(hooks) { H = hooks || {}; buildPanel();
  window.addEventListener("message", e => { const d = e.data; if (!d || d.__wy !== 1) return; appendConsole(d.text, d.level); if (d.level === "error") { lastError = d.text; showFix(); } }); }

function buildPanel() {
  const side = $("aside"); side.id = "side"; side.hidden = true;
  side.innerHTML = '<div class="shead"><span class="stitle">Artifacts</span><span class="sgrow"></span><button class="sx" title="Close">\u00d7</button></div>'
    + '<div class="stabs"></div>'
    + '<div class="stool"><button class="brun primary">\u25b6 Run</button><button class="bcopy">Copy</button><button class="bdl">Download</button><button class="bzip" hidden>Download all (zip)</button><span class="sgrow"></span><label class="vlabel">Version <select class="vsel"></select></label><button class="bdiff" hidden>Diff</button></div>'
    + '<div class="sbody"><textarea class="seditor" spellcheck="false" wrap="off"></textarea><div class="sdiff" hidden></div></div>'
    + '<div class="spreviewwrap" hidden><iframe class="spreview" sandbox="allow-scripts allow-modals" title="Preview"></iframe></div>'
    + '<div class="sconsolewrap" hidden><div class="scbar">Console<button class="scclear">Clear</button><button class="bfix" hidden>Fix with WyneAI</button></div><div class="sconsole"></div></div>';
  document.body.appendChild(side);
  els = { side, tabs: side.querySelector(".stabs"), editor: side.querySelector(".seditor"), diff: side.querySelector(".sdiff"),
    run: side.querySelector(".brun"), copy: side.querySelector(".bcopy"), dl: side.querySelector(".bdl"), zip: side.querySelector(".bzip"),
    vsel: side.querySelector(".vsel"), vlabel: side.querySelector(".vlabel"), diffBtn: side.querySelector(".bdiff"),
    previewWrap: side.querySelector(".spreviewwrap"), preview: side.querySelector(".spreview"),
    consoleWrap: side.querySelector(".sconsolewrap"), console: side.querySelector(".sconsole"), fix: side.querySelector(".bfix") };
  side.querySelector(".sx").onclick = closePanel;
  els.run.onclick = runActive;
  els.copy.onclick = e => { const f = files.get(active); if (!f) return; navigator.clipboard.writeText(els.editor.value).then(() => { e.target.textContent = "Copied"; setTimeout(() => e.target.textContent = "Copy", 1200); }).catch(() => {}); };
  els.dl.onclick = () => downloadOne(active);
  els.zip.onclick = downloadZip;
  els.editor.oninput = () => { const f = files.get(active); if (f) f.content = els.editor.value; };
  els.vsel.onchange = () => showVersion(els.vsel.value);
  els.diffBtn.onclick = toggleDiff;
  side.querySelector(".scclear").onclick = () => { els.console.textContent = ""; lastError = null; els.fix.hidden = true; };
  els.fix.onclick = () => { if (H.onFix && lastError) H.onFix(active, lastError); };
}

export function openPanel() { els.side.hidden = false; document.body.classList.add("side-open"); }
export function closePanel() { els.side.hidden = true; document.body.classList.remove("side-open"); }

export function setFiles(list, { open = true } = {}) {
  if (!Array.isArray(list) || !list.length) return;
  for (const it of list) { const name = String(it.name || "file"); const content = String(it.content ?? "");
    const ex = files.get(name);
    if (ex) { if (ex.content !== content) { ex.versions.push({ content: ex.content, at: Date.now() }); ex.content = content; } }
    else files.set(name, { content, versions: [] }); }
  active = String(list[0].name || active || [...files.keys()][0]);
  renderTabs(); showActive(); if (open) openPanel();
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
  return "[The user is editing this file in the WyneAI editor. Current contents of " + active + ":]\n```\n" + f.content.slice(0, 8000) + "\n```\n[When they ask for changes, return the full updated file with create_file.]"; }
export function hasFiles() { return files.size > 0; }

function renderTabs() { els.tabs.innerHTML = "";
  for (const name of files.keys()) { const t = $("button", "stab" + (name === active ? " on" : ""), name); t.type = "button";
    t.onclick = () => selectFile(name); els.tabs.appendChild(t); }
  els.zip.hidden = files.size < 2; }
function selectFile(name) { if (!files.has(name)) return; active = name; renderTabs(); showActive(); }

function showActive() { const f = files.get(active); if (!f) return;
  els.editor.value = f.content; els.editor.hidden = false; els.diff.hidden = true;
  // version dropdown
  els.vsel.innerHTML = ""; const cur = $("option", null, "Current"); cur.value = "cur"; els.vsel.appendChild(cur);
  f.versions.forEach((v, i) => { const o = $("option", null, "v" + (i + 1)); o.value = String(i); els.vsel.appendChild(o); });
  els.vsel.value = "cur"; els.vlabel.style.display = f.versions.length ? "" : "none"; els.diffBtn.hidden = !f.versions.length;
  els.diffBtn.textContent = "Diff"; updateRunUI(); }
function showVersion(v) { const f = files.get(active); if (!f) return;
  if (v === "cur") { els.editor.value = f.content; els.editor.readOnly = false; els.diff.hidden = true; els.editor.hidden = false; return; }
  const old = f.versions[+v]; if (!old) return; els.editor.value = old.content; els.editor.readOnly = true; }

function updateRunUI() { const can = runnable(active) || hasHTML(); els.run.disabled = !can; els.run.style.display = can ? "" : "none"; }

function appendConsole(text, level) { els.consoleWrap.hidden = false;
  const line = $("div", "cl " + (level || "log"), text); els.console.appendChild(line); els.console.scrollTop = 1e9; }
function showFix() { els.fix.hidden = !H.onFix; }

const FORWARDER = "(function(){function s(l,a){try{parent.postMessage({__wy:1,level:l,text:a.map(function(x){try{return typeof x==='object'?JSON.stringify(x):String(x)}catch(e){return String(x)}}).join(' ')},'*')}catch(e){}}['log','info','warn','error','debug'].forEach(function(k){var o=console[k]?console[k].bind(console):function(){};console[k]=function(){o.apply(null,arguments);s(k==='debug'?'log':k,[].slice.call(arguments))}});window.addEventListener('error',function(e){s('error',[e.message+(e.filename&&e.lineno?' ('+e.lineno+':'+e.colno+')':'')])});window.addEventListener('unhandledrejection',function(e){s('error',['Unhandled rejection: '+((e.reason&&e.reason.stack)||e.reason)])});})();";

function runActive() { lastError = null; els.fix.hidden = true; els.console.textContent = "";
  if (/\.(m?js|ts|jsx|tsx)$/i.test(active)) runJS(files.get(active).content);
  else if (/\.html?$/i.test(active) || /\.css$/i.test(active) || hasHTML()) runHTML();
  else appendConsole("No in-browser runner for this file type yet.", "muted"); }

function runHTML() { const htmlName = /\.html?$/i.test(active) ? active : hasHTML();
  let doc = files.get(htmlName).content;
  const css = [...files].filter(([n]) => /\.css$/i.test(n)).map(([, f]) => f.content).join("\n");
  const js = [...files].filter(([n]) => /\.m?js$/i.test(n)).map(([, f]) => f.content).join("\n;\n");
  const head = "<script>" + FORWARDER + "<\/script>" + (css ? "<style>" + css + "<\/style>" : "");
  doc = /<\/head>/i.test(doc) ? doc.replace(/<\/head>/i, head + "</head>") : head + doc;
  if (js && !/\.m?js/i.test(active)) { const tag = "<script>" + js + "<\/script>"; doc = /<\/body>/i.test(doc) ? doc.replace(/<\/body>/i, tag + "</body>") : doc + tag; }
  els.previewWrap.hidden = false; els.consoleWrap.hidden = false; els.preview.srcdoc = doc;
  appendConsole("Rendered " + htmlName + ".", "muted"); }

function runJS(code) { const doc = "<!doctype html><meta charset=utf-8><script>" + FORWARDER + "<\/script><script>try{\n" + code + "\n}catch(e){parent.postMessage({__wy:1,level:'error',text:(e&&e.stack)||String(e)},'*')}<\/script>";
  els.previewWrap.hidden = true; els.consoleWrap.hidden = false; els.preview.srcdoc = doc; }

function downloadOne(name) { const f = files.get(name); if (!f) return; dl(new Blob([f.content], { type: "text/plain" }), name); }
function downloadZip() { const dict = {}; for (const [n, f] of files) dict[n] = new TextEncoder().encode(f.content);
  const zip = self.fflate.zipSync(dict, { level: 6 }); dl(new Blob([zip], { type: "application/zip" }), "artifacts.zip"); }
function dl(blob, name) { const u = URL.createObjectURL(blob); const a = $("a"); a.href = u; a.download = name; document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(u), 5000); }

// --- tiny line diff (LCS) for version view ---
function lineDiff(a, b) { const A = a.split("\n"), B = b.split("\n"), n = A.length, m = B.length;
  const dp = Array.from({ length: n + 1 }, () => new Array(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) dp[i][j] = A[i] === B[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const out = []; let i = 0, j = 0;
  while (i < n && j < m) { if (A[i] === B[j]) { out.push([" ", A[i]]); i++; j++; } else if (dp[i + 1][j] >= dp[i][j + 1]) { out.push(["-", A[i++]]); } else { out.push(["+", B[j++]]); } }
  while (i < n) out.push(["-", A[i++]]); while (j < m) out.push(["+", B[j++]]); return out; }
function toggleDiff() { const f = files.get(active); if (!f || !f.versions.length) return;
  if (!els.diff.hidden) { els.diff.hidden = true; els.editor.hidden = false; els.diffBtn.textContent = "Diff"; return; }
  const v = els.vsel.value === "cur" ? f.versions.length - 1 : +els.vsel.value; const prev = f.versions[v] ? f.versions[v].content : "";
  els.diff.innerHTML = ""; for (const [sign, line] of lineDiff(prev, f.content)) { const d = $("div", "dl" + (sign === "+" ? " add" : sign === "-" ? " del" : ""), (sign === " " ? "  " : sign + " ") + line); els.diff.appendChild(d); }
  els.diff.hidden = false; els.editor.hidden = true; els.diffBtn.textContent = "Hide diff"; }
