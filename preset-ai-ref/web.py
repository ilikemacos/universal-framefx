"""Tiny local web UI: python web.py -> http://127.0.0.1:8765 (binds localhost only)."""
import json, html, http.server, urllib.parse
from assist import answer, profile, explain, load_model
import os
MODE=os.environ.get('PRESET_MODEL','r3'); from extract import extract
from evaluate3 import mfeats, hybrid, settle
from rules import SLOTS
def answer3w(q):
    if M is None: return answer(q,None)
    o=settle(hybrid(extract(q),mfeats(M.gen(q)))); return ({k:o[k] for k in SLOTS} if o else None),'rules + r3 model'
M=None if MODE=='rules' else load_model('ckpt_r3.pt')  # default: r3 hybrid
PAGE="""<html><body style="font-family:sans-serif;max-width:760px;margin:2em auto"><h2>FrameFX preset assistant</h2>
<form><input name=q style="width:80%%" value="%s" placeholder="Cyberpunk on a 3060 laptop, want 120 fps, hate blur"><button>Ask</button></form><pre>%s</pre></body></html>"""
import uuid
from chat import Chat
SESS={}
CHAT_PAGE="""<html><head><meta charset=utf-8><title>FrameFX preset chat</title></head><body style="font-family:sans-serif;max-width:760px;margin:2em auto">
<h2>FrameFX preset chat</h2><div id=log style="border:1px solid #ccc;padding:1em;height:420px;overflow:auto;white-space:pre-wrap"></div>
<form id=f><input id=q style="width:70%" autocomplete=off placeholder="Cyberpunk on a 3060, 45 fps, want 120"><button>Send</button>
<button type=button id=j>Show preset JSON</button><button type=button id=r>Start over</button></form>
<script>
const log=document.getElementById('log');function add(w,t){const d=document.createElement('div');d.style.margin='.5em 0';d.innerHTML='<b>'+w+':</b> ';d.appendChild(document.createTextNode(t));log.appendChild(d);log.scrollTop=1e9}
async function send(t){const r=await fetch('/chat/api',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({q:t})});return (await r.json()).reply}
document.getElementById('f').onsubmit=async e=>{e.preventDefault();const q=document.getElementById('q');const t=q.value.trim();if(!t)return;q.value='';add('You',t);add('Assistant',await send(t))};
document.getElementById('j').onclick=async()=>add('Preset JSON',await send('__json__'));
document.getElementById('r').onclick=async()=>{log.innerHTML='';add('Assistant',await send('start over'))};
add('Assistant','Hi! Tell me your game, GPU, and fps (current vs. wanted). Then refine: "what about 4K", "make it quieter", "turn frame gen off"...');
</script></body></html>"""
class H(http.server.BaseHTTPRequestHandler):
    def _sid(s):
        c=s.headers.get("Cookie","") or ""; sid=next((p.split("=",1)[1] for p in c.split("; ") if p.startswith("sid=")),None)
        return sid if sid in SESS else None
    def do_POST(s):
        if urllib.parse.urlparse(s.path).path!="/chat/api": s.send_response(404); s.end_headers(); return
        n=int(s.headers.get("Content-Length",0)); q=json.loads(s.rfile.read(n) or b"{}").get("q","")[:500]
        sid=s._sid(); new=sid is None
        if new: sid=uuid.uuid4().hex; SESS[sid]=Chat(M)
        c=SESS[sid]; rep=json.dumps(c.profile_json(),indent=2) if q=="__json__" else c.turn(q)
        s.send_response(200); s.send_header("Content-Type","application/json")
        if new: s.send_header("Set-Cookie",f"sid={sid}; Path=/; HttpOnly; SameSite=Strict")
        s.end_headers(); s.wfile.write(json.dumps({"reply":rep}).encode())
    def do_GET(s):
        if urllib.parse.urlparse(s.path).path=="/chat":
            s.send_response(200); s.send_header("Content-Type","text/html; charset=utf-8"); s.end_headers(); s.wfile.write(CHAT_PAGE.encode()); return
        q=urllib.parse.parse_qs(urllib.parse.urlparse(s.path).query).get("q",[""])[0]; out=""
        if q:
            d,src=answer3w(q); out="I don't know that game yet." if d is None else explain(d)+"\n\n"+json.dumps({d["exe"]:profile(d)},indent=2)+f"\n[source: {src}]"
        s.send_response(200); s.send_header("Content-Type","text/html; charset=utf-8"); s.end_headers()
        s.wfile.write((PAGE%(html.escape(q),html.escape(out))).encode())
http.server.HTTPServer(("127.0.0.1",8765),H).serve_forever()
