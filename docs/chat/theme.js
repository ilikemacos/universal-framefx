// WrenbyAI minimal dark theme: near-black, centered rounded composer pill with the mode picker inside,
// right-aligned grey user bubbles, plain assistant text, slim collapsible chat sidebar. Loaded after the page builds.
import { modeToggle, injectCSS } from "./cloudtask.js";
const CSS = `
:root{--bg:#0b0b0b;--fg:#ececec;--muted:#8f8f8f;--rule:#1f1f1f;--card:#161616;--accent:#f2f2f2;--sans:"Inter",ui-sans-serif,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
html,body{background:var(--bg)!important;color:var(--fg);font-family:var(--sans)!important;-webkit-font-smoothing:antialiased}
header{background:transparent!important;border-bottom:0!important;min-height:52px!important;padding:8px 18px!important}
header h1{font-size:15px!important;font-weight:600}header .badge{display:none!important}#status{color:var(--muted);font-size:12px}
header button,header select{border-radius:999px!important;border-color:#262626!important;background:transparent}header button:hover{background:#1a1a1a!important}
#log{padding:24px 0 8px!important}.msg{max-width:780px!important;padding:10px 24px!important}
.msg.u{flex-direction:row-reverse}.msg.u .av{display:none}.msg.u .bub{flex:0 1 auto!important;max-width:78%;background:#1f1f1f;color:#f3f3f3!important;border-radius:22px;padding:10px 16px;white-space:pre-wrap}
.msg.a .bub{background:transparent;line-height:1.65}.msg.a .av img{width:22px!important;height:22px!important;border-radius:50%!important}
form#f{max-width:780px!important;margin:0 auto 6px!important;padding:8px 10px 8px 20px!important;background:#171717;border:1px solid #2a2a2a;border-radius:28px;display:flex;align-items:center;gap:8px;box-shadow:0 8px 30px #0008;width:calc(100% - 32px)!important}
form#f:focus-within{border-color:#3a3a3a}
form#f #q{background:transparent!important;border:0!important;height:44px!important;padding:0!important;font-size:15px;outline:none;flex:1;min-width:0}
form#f select#tier{border:1px solid #2c2c2c!important;border-radius:999px!important;height:34px!important;background:#1f1f1f!important;color:#ddd;font-size:12px!important;padding:0 10px!important}
form#f #send,form#f #stop{border-radius:999px!important;height:38px!important;min-width:38px;padding:0 16px!important;background:#f2f2f2!important;color:#111!important;border:0!important;font-weight:600}
form#f .ctmode{flex:none}#tierhint{display:none!important}
.foot{font-size:11px!important;color:#6b6b6b!important;text-align:center!important;max-width:780px!important;width:calc(100% - 32px)!important;margin:4px auto 10px!important;padding:0 12px!important;box-sizing:border-box;align-self:center}
#histbar{background:#0f0f0f!important;border-right:1px solid #1c1c1c!important;width:250px}#histbar .hitem{border-radius:10px!important;border:0!important}#histbar .hitem.on{background:#1c1c1c!important}
#histbar .hnew{border-radius:999px!important}.wy-hamb{border-radius:999px!important}
.fcard,.think,pre{border-radius:14px!important}.fcard{border-color:#262626!important;background:#141414!important}.zipall,.fhead .primary{background:#f2f2f2!important;color:#111!important;border-radius:999px!important}
button{border-radius:10px}a{color:#cfcfcf}
@media(max-width:640px){header{padding:6px 10px!important;gap:6px!important;flex-wrap:nowrap!important}header #status{display:none}form#f{flex-wrap:wrap;border-radius:22px;padding:6px 8px 6px 14px!important;width:calc(100% - 16px)!important}form#f #q{flex-basis:100%}.msg{padding:8px 14px!important}.msg.u .bub{max-width:88%}form#f{padding-left:14px!important}form#f .ctmode .ctm{padding:0 7px}form#f select#tier{max-width:96px}}
`;
export function applyTheme() { injectCSS(); if (!document.getElementById("wtheme")) { const s = document.createElement("style"); s.id = "wtheme"; s.textContent = CSS; document.head.appendChild(s); }
  const f = document.getElementById("f"), q = document.getElementById("q"), tier = document.getElementById("tier");
  if (f && q) { if (tier && tier.parentElement !== f) f.insertBefore(tier, q.nextSibling); if (!f.querySelector(".ctmode")) f.insertBefore(modeToggle(), (tier && tier.parentElement === f ? tier : q).nextSibling); }
  document.title = document.title.replace(/·.*$/, "").trim() || "WrenbyAI"; }
