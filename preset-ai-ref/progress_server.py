# FrameFX preset AI progress dashboard. stdlib only. Serves on :8790.
# Usage: python progress_server.py            -> serve (logs from this folder)
#        python progress_server.py --snapshot LOGDIR OUT.html
import json, os, re, sys, time, datetime, http.server, socketserver
HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 8790
WIN_START = "15:44:45"; WIN_HOURS = 2
ROUNDS = [  # earlier rounds, from box PROGRESS.md / eval logs (exact match on all settings)
 {"run":"r1 (CPU)","params":"1.06M","steps":7500,"note":"end-to-end; unseen-phrasing 0.467, free-form 25: 0.20 (rules 0.68)"},
 {"run":"r2 (CPU)","params":"1.06M","steps":6000,"note":"extractor; unseen 0.434 / hybrid 0.773; free-form 0.12 / hybrid 0.64"},
 {"run":"r3 (CPU)","params":"1.06M","steps":20000,"note":"held-out families hybrid 0.856 (rules 0.745); unseen hybrid 0.961"},
]
BLIND = {"n":85,"rules":0.918,"model":0.788,"hybrid":0.929}
BLIND_G14 = {"n":85,"rules":0.918,"model":0.941,"hybrid":0.929}
L = re.compile(r"^(\d\d:\d\d:\d\d) step (\d+) loss ([\d.]+) lr \S+ (\d+)s ([\d.]+) it/s")
E = re.compile(r"^(\d\d:\d\d:\d\d) (EVAL|END \w+) steps? (\d+) loss ([\d.]+) dev (\{.*?\}) heldout (\{.*?\}) style (\{.*?\}) r3ho (\{.*?\}) score ([\d.]+)")
S = re.compile(r"^(\d\d:\d\d:\d\d) START (\w+) params (\d+).*?MIN ([\d.]+) data (\d+)")
B = re.compile(r"^(\d\d:\d\d:\d\d) BEST ([\d.]+) step (\d+)")
def d(s): return json.loads(s.replace("'", '"'))
def parse(logdir):
    runs = {}
    for name in ["A","B","C","FINAL","CONT"]:
        p = os.path.join(logdir, name+".log")
        if not os.path.exists(p): continue
        r = {"name":name,"loss":[],"evals":[],"best":None,"start":None,"end":None,"params":None,"minutes":None,"data":None,"step":0,"its":0,"last":None}
        with open(p, encoding="utf-8", errors="replace") as f:
            for line in f:
                line=line.strip()
                m = S.match(line)
                if m: r["start"],r["params"],r["minutes"],r["data"]=m[1],int(m[3]),float(m[4]),int(m[5]); continue
                m = L.match(line)
                if m:
                    r["loss"].append([int(m[2]),float(m[3])]); r["step"]=int(m[2]); r["its"]=float(m[5]); r["last"]=m[1]; continue
                m = E.match(line)
                if m:
                    ev={"t":m[1],"step":int(m[3]),"loss":float(m[4]),"dev":d(m[5]),"heldout":d(m[6]),"style":d(m[7]),"r3ho":d(m[8]),"score":float(m[9])}
                    if m[2]=="EVAL": r["evals"].append(ev); r["step"]=max(r["step"],ev["step"]); r["last"]=m[1]
                    else: r["end"]=m[1]; r["final"]=ev
                    continue
                m = B.match(line)
                if m: r["best"]={"score":float(m[2]),"step":int(m[3])}
        if r["evals"] and (r["best"] is None):
            b=max(r["evals"],key=lambda e:e["score"]); r["best"]={"score":b["score"],"step":b["step"]}
        if len(r["loss"])>600:  # thin
            k=len(r["loss"])//600+1; r["loss"]=r["loss"][::k]+[r["loss"][-1]]
        runs[name]=r
    return {"generated":datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),"winStart":WIN_START,"winHours":WIN_HOURS,
            "runs":runs,"rounds":ROUNDS,"blind":BLIND,"blind_g14":BLIND_G14}
HTML = r"""<!doctype html><html><head><meta charset="utf-8"><title>FrameFX Preset AI - training</title>
<style>
body{margin:0;background:#0d1117;color:#e6edf3;font:14px/1.45 Segoe UI,system-ui,sans-serif}
.w{max-width:1200px;margin:0 auto;padding:20px}h1{font-size:22px;margin:0 0 4px}h2{font-size:15px;margin:0 0 10px;color:#9fb3c8}
.sub{color:#8b949e;font-size:12px}.grid{display:grid;grid-template-columns:repeat(6,1fr);gap:10px;margin:14px 0}
.card{background:#161b22;border:1px solid #30363d;border-radius:10px;padding:14px}.k{color:#8b949e;font-size:11px;text-transform:uppercase;letter-spacing:.05em}
.v{font-size:22px;font-weight:600;margin-top:2px}.row{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:12px}
canvas{width:100%;height:300px;display:block}.pill{display:inline-block;padding:2px 10px;border-radius:99px;font-size:12px;font-weight:600}
.run{background:#1f6feb33;color:#58a6ff}.done{background:#2386363a;color:#3fb950}.bar{height:8px;background:#30363d;border-radius:4px;overflow:hidden;margin-top:8px}
.bar>div{height:100%;background:linear-gradient(90deg,#1f6feb,#a371f7)}table{width:100%;border-collapse:collapse;font-size:13px}
td,th{padding:6px 8px;border-bottom:1px solid #21262d;text-align:left}th{color:#8b949e;font-weight:500}td.n{text-align:right;font-variant-numeric:tabular-nums}
.lg{font-size:12px;color:#8b949e;margin-top:6px}.lg span{margin-right:12px}.lg i{display:inline-block;width:10px;height:3px;margin-right:4px;vertical-align:middle}
.best{color:#f2cc60}ul{margin:6px 0 0 18px;padding:0}li{margin:4px 0}
</style></head><body><div class="w">
<h1>FrameFX Preset AI &mdash; training progress <span id="pill" class="pill run">...</span></h1>
<div class="sub">1.06M-param transformer, trained from scratch on ROG-G14 (RTX 3060 Laptop). <span id="gen"></span></div>
<div class="grid">
<div class="card"><div class="k">Current run</div><div class="v" id="cur">-</div></div>
<div class="card"><div class="k">Step</div><div class="v" id="step">-</div></div>
<div class="card"><div class="k">Speed</div><div class="v" id="its">-</div></div>
<div class="card"><div class="k">Elapsed (2h window)</div><div class="v" id="el">-</div></div>
<div class="card"><div class="k">Final run ends</div><div class="v" id="rem">-</div></div>
<div class="card"><div class="k">Best practice score</div><div class="v best" id="best">-</div></div>
</div>
<div class="card" style="margin-bottom:12px"><div class="k">2-hour window (from 3:44:45 PM PT)</div><div class="bar"><div id="wbar" style="width:0"></div></div></div>
<div class="row"><div class="card"><h2>Training loss (log scale)</h2><canvas id="loss"></canvas><div class="lg" id="lossLg"></div></div>
<div class="card"><h2>Practice scores over steps &mdash; FINAL run</h2><canvas id="ev"></canvas><div class="lg" id="evLg"></div></div></div>
<div class="card" style="margin-bottom:12px"><h2>Runs compared</h2><table id="tbl"></table>
<div class="lg">Round 3 blind baseline (85 hand-written requests, never trained on): rules <b>0.918</b> &middot; model <b>0.788</b> &middot; hybrid <b>0.929</b>. </div><div class="lg"><b>G14 result (one-time blind test, CONT_best, step 7,640 of the continuation run):</b> rules <b>0.918</b> &middot; model <b>0.941</b> &middot; hybrid <b>0.929</b>. The hybrid ties round 3, so the default stays the round 3 hybrid. The model on its own scored best (0.941), but it wasn't chosen beforehand, so the default isn't switched on that result.</div></div>
<div class="card"><h2>What this means</h2><ul id="mean"></ul></div>
</div><script>
const C={A:"#8b949e",B:"#58a6ff",C:"#f78166",FINAL:"#a371f7"};
function hms(s){return s.split(":").map(Number).reduce((a,b)=>a*60+b)}
function fmt(sec){sec=Math.max(0,Math.round(sec));const h=Math.floor(sec/3600),m=Math.floor(sec%3600/60);return (h?h+"h ":"")+m+"m"}
function t12(sec){sec=((sec%86400)+86400)%86400;let h=Math.floor(sec/3600),m=Math.floor(sec%3600/60);const ap=h>=12?"PM":"AM";h=h%12||12;return h+":"+String(m).padStart(2,"0")+" "+ap}
function setup(cv){const r=cv.getBoundingClientRect(),dp=devicePixelRatio||1;cv.width=r.width*dp;cv.height=r.height*dp;const x=cv.getContext("2d");x.scale(dp,dp);return [x,r.width,r.height]}
function chart(cv,series,o){const [x,W,H]=setup(cv),P={l:46,r:10,t:10,b:28};let xs=[],ys=[];series.forEach(s=>s.pts.forEach(p=>{xs.push(p[0]);ys.push(p[1])}));if(!xs.length)return;
 let x0=0,x1=Math.max(...xs),y0,y1;const lg=o.log;if(lg){ys=ys.filter(v=>v>0);y0=Math.floor(Math.log10(Math.min(...ys,1e-4)));y1=Math.ceil(Math.log10(Math.max(...ys)))}else{y0=o.y0;y1=o.y1}
 const X=v=>P.l+(v-x0)/(x1-x0||1)*(W-P.l-P.r),Y=v=>{const t=lg?Math.log10(Math.max(v,10**y0)):v;return H-P.b-(t-y0)/(y1-y0)*(H-P.t-P.b)};
 x.font="11px Segoe UI";x.fillStyle="#8b949e";x.strokeStyle="#21262d";
 const ticks=lg?[...Array(y1-y0+1).keys()].map(i=>10**(y0+i)):[0,.2,.4,.6,.8,1].filter(v=>v>=y0&&v<=y1);
 ticks.forEach(v=>{x.beginPath();x.moveTo(P.l,Y(v));x.lineTo(W-P.r,Y(v));x.stroke();x.fillText(lg?("1e"+Math.round(Math.log10(v))):v.toFixed(1),4,Y(v)+4)});
 for(let i=0;i<=5;i++){const v=x1*i/5;x.fillText(Math.round(v/1000)+"k",X(v)-8,H-8)}
 series.forEach(s=>{x.strokeStyle=s.c;x.lineWidth=s.w||1.5;x.setLineDash(s.dash||[]);x.beginPath();s.pts.forEach((p,i)=>i?x.lineTo(X(p[0]),Y(p[1])):x.moveTo(X(p[0]),Y(p[1])));x.stroke();x.setLineDash([]);
  if(s.dots)s.pts.forEach(p=>{x.fillStyle=s.c;x.beginPath();x.arc(X(p[0]),Y(p[1]),2.5,0,7);x.fill()})});
 if(o.mark){x.strokeStyle="#f2cc60";x.setLineDash([4,3]);x.beginPath();x.moveTo(X(o.mark[0]),P.t);x.lineTo(X(o.mark[0]),H-P.b);x.stroke();x.setLineDash([]);x.fillStyle="#f2cc60";x.beginPath();x.arc(X(o.mark[0]),Y(o.mark[1]),5,0,7);x.fill();x.fillText("best "+o.mark[1].toFixed(3)+" @ "+o.mark[0],Math.min(X(o.mark[0])+6,W-120),P.t+12)}}
function f3(v){return v==null?"-":v.toFixed(3)}
function render(D){const R=D.runs,F=R.FINAL,now=new Date(),nowS=now.getHours()*3600+now.getMinutes()*60+now.getSeconds();
 const ws=hms(D.winStart),we=ws+D.winHours*3600;let cur=F||R.C||R.B||R.A;const done=F&&F.end;
 const lastS=cur&&cur.last?hms(cur.last):nowS;const live=!D.snapshot&&!done;const refS=live?nowS:lastS;const stale=live&&nowS-lastS>120;
 const pill=document.getElementById("pill");pill.textContent=done?"DONE":(stale?"RUNNING? (no log for "+fmt(nowS-lastS)+")":"RUNNING");pill.className="pill "+(done?"done":"run");
 document.getElementById("gen").textContent=(D.snapshot?"Snapshot of logs at ":"Data read ")+D.generated+" PT"+(D.snapshot?"":" - refreshes every 15 s");
 document.getElementById("cur").textContent=cur?cur.name:"-";document.getElementById("step").textContent=cur?cur.step.toLocaleString():"-";
 document.getElementById("its").textContent=cur?cur.its+" it/s":"-";
 const el=Math.min(refS,we)-ws;document.getElementById("el").textContent=fmt(el)+" / 2h";document.getElementById("wbar").style.width=Math.min(100,el/(we-ws)*100)+"%";
 if(F&&F.start){const fe=hms(F.start)+F.minutes*60;document.getElementById("rem").textContent=done?"ended "+t12(hms(F.end)):t12(fe)+" ("+fmt(fe-refS)+" left)"}
 const fb=F&&F.best;document.getElementById("best").textContent=fb?fb.score.toFixed(3)+" @ "+fb.step.toLocaleString():"-";
 chart(document.getElementById("loss"),Object.values(R).map(r=>({pts:r.loss,c:C[r.name]})),{log:true});
 document.getElementById("lossLg").innerHTML=Object.values(R).map(r=>`<span><i style="background:${C[r.name]}"></i>${r.name}${r.params>2e6?" (4.9M)":""}</span>`).join("");
 if(F){const E=F.evals,S=[["score","Overall score","#f2cc60",e=>e.score,2.5],["dm","Dev - model","#58a6ff",e=>e.dev.model],["dh","Dev - hybrid","#3fb950",e=>e.dev.hybrid],["hm","Held-out families - model","#f78166",e=>e.heldout.model],["hh","Held-out families - hybrid","#ff7b72",e=>e.heldout.hybrid,1.5,[5,3]],["sm","Held-out style - model","#d2a8ff",e=>e.style.model]];
  chart(document.getElementById("ev"),S.map(s=>({pts:E.map(e=>[e.step,s[3](e)]),c:s[2],w:s[4],dash:s[5],dots:s[0]=="score"})),{y0:0,y1:1,mark:fb?[fb.step,fb.score]:null});
  document.getElementById("evLg").innerHTML=S.map(s=>`<span><i style="background:${s[2]}"></i>${s[1]}</span>`).join("")}
 let rows="<tr><th>Run</th><th>Params</th><th>Data</th><th class=n>Steps</th><th class=n>Last loss</th><th class=n>Best score</th><th class=n>Dev model / hybrid</th><th class=n>Held-out fam. m / h</th><th class=n>Style m / h</th><th>Notes</th></tr>";
 const note={A:"1M, old data (200k)",B:"1M, new varied data - won tests",C:"4.9M - 0.5 it/s, too slow, dropped",FINAL:"1M, varied data, long run"};
 Object.values(R).forEach(r=>{const e=r.final||r.evals.filter(v=>r.best&&v.step==r.best.step)[0]||r.evals[r.evals.length-1];const ll=r.loss.length?r.loss[r.loss.length-1][1]:null;
  rows+=`<tr><td><b style="color:${C[r.name]}">${r.name}</b></td><td>${(r.params/1e6).toFixed(2)}M</td><td>${r.data?(r.data/1000)+"k":""}</td><td class=n>${r.step.toLocaleString()}</td><td class=n>${ll==null?"-":ll.toFixed(4)}</td><td class="n best">${r.best?r.best.score.toFixed(3):"-"}</td><td class=n>${e?f3(e.dev.model)+" / "+f3(e.dev.hybrid):"-"}</td><td class=n>${e?f3(e.heldout.model)+" / "+f3(e.heldout.hybrid):"-"}</td><td class=n>${e?f3(e.style.model)+" / "+f3(e.style.hybrid):"-"}</td><td>${note[r.name]||""}${r.end?"":(r.name=="FINAL"?" (running)":" (stopped early)")}</td></tr>`});
 D.rounds.forEach(r=>rows+=`<tr><td>${r.run}</td><td>${r.params}</td><td></td><td class=n>${r.steps.toLocaleString()}</td><td class=n>-</td><td class=n>-</td><td class=n colspan=3 style="text-align:left">${r.note}</td><td>different test sets</td></tr>`);
 rows+=`<tr><td>r3 blind</td><td>1.06M</td><td></td><td class=n></td><td class=n></td><td class=n></td><td class=n colspan=3 style="text-align:left">85 blind: rules ${D.blind.rules} / model ${D.blind.model} / <b>hybrid ${D.blind.hybrid}</b></td><td>current default (baseline)</td></tr>`;
 rows+=`<tr><td>G14 blind</td><td>1.06M</td><td></td><td class=n></td><td class=n></td><td class=n></td><td class=n colspan=3 style="text-align:left">85 blind: rules ${D.blind_g14.rules} / <b>model ${D.blind_g14.model}</b> / hybrid ${D.blind_g14.hybrid}</td><td>tie on hybrid: default unchanged</td></tr>`;
 document.getElementById("tbl").innerHTML=rows;
 const fl=F&&F.evals.length?F.evals[F.evals.length-1]:null;
 document.getElementById("mean").innerHTML=[
  "Varied training data mattered most: the same 1M model went from <b>"+(R.A&&R.A.best?R.A.best.score.toFixed(3):"0.707")+"</b> (run A, old data) to <b>"+(R.B&&R.B.best?R.B.best.score.toFixed(3):"0.858")+"</b> (run B, new data).",
  "The bigger 4.9M model (C) ran ~20x slower on the laptop GPU, so it was dropped.",
  fb?"The FINAL run's best checkpoint so far scores <b>"+fb.score.toFixed(3)+"</b> at step "+fb.step.toLocaleString()+". That best one is kept, not the last one.":"",
  fl?"Sloppy typing (style) is mostly solved ("+f3(fl.style.model)+" model alone); request types never seen in training are still the weak spot (~"+f3(fl.heldout.hybrid)+" with rules).":"",
  "Loss near 0.0001 means it has learned the training examples well; the practice scores are what count.",
  "Done: blind test. G14 hybrid 0.929 ties round 3 (0.929); model alone 0.941. Default unchanged."].filter(Boolean).map(s=>"<li>"+s+"</li>").join("")}
let D0=window.__DATA__;
async function tick(){try{const r=await fetch("data.json",{cache:"no-store"});render(await r.json())}catch(e){document.getElementById("gen").textContent="Could not read data.json: "+e}}
if(D0){render(D0)}else{tick();setInterval(tick,15000)}
addEventListener("resize",()=>D0?render(D0):tick());
</script></body></html>"""
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def do_GET(self):
        if self.path.startswith("/data.json"):
            body=json.dumps(parse(HERE)).encode(); ct="application/json"
        elif self.path in ("/","/index.html"):
            body=HTML.encode(); ct="text/html; charset=utf-8"
        else:
            self.send_response(404); self.end_headers(); return
        self.send_response(200); self.send_header("Content-Type",ct); self.send_header("Cache-Control","no-store")
        self.send_header("Content-Length",str(len(body))); self.end_headers(); self.wfile.write(body)
if __name__=="__main__":
    if len(sys.argv)>=4 and sys.argv[1]=="--snapshot":
        D=parse(sys.argv[2]); D["snapshot"]=True
        open(sys.argv[3],"w",encoding="utf-8").write(HTML.replace("let D0=window.__DATA__;","let D0="+json.dumps(D)+";"))
    else:
        socketserver.ThreadingTCPServer.allow_reuse_address=True
        with socketserver.ThreadingTCPServer(("127.0.0.1",PORT),H) as s: s.serve_forever()
