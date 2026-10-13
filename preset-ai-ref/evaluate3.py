"""Round 3 eval: rules alone vs model(+rules) vs hybrid (rules first, model fills empty slots)."""
import json, sys, collections
from kb import GAMES, GPUS
from rules import parse, decide, SLOTS
from extract import extract
GBY={g[1].lower():g for g in GAMES}; PBY={g[0].lower():g for g in GPUS}
K=[k for k in SLOTS if k!="why"]
def same(o,y): return o is not None and all(o[k]==y[k] for k in K)
def mfeats(line):
    d=parse(line); n=lambda v: int(v) if v and v.isdigit() else None; S=lambda v:set() if v in (None,"","-") else set(v.split(","))
    return dict(game=GBY.get(d.get("g")),gpu=PBY.get(d.get("gpu")),laptop=d.get("lap")=="1",res=d.get("res") if d.get("res") in ("1080","1440","4k") else None,
                fps=n(d.get("fps")),target=n(d.get("tgt")),prefs=S(d.get("pref"))&{"blur","latency","quality","battery"},
                asks=S(d.get("ask"))&{"fsr1","fsr2","fsr3","xess","fsr4","fsr3fg","ssgi","rt","cpu","csr12"})
def settle(f):
    if f["game"] is None: return None
    f=dict(f); f["gpu"]=f["gpu"] or PBY["rtx 3060"]; return decide(f)
def hybrid(kw,mf):
    f=dict(kw)
    for k in ("game","gpu","res","fps","target"):
        if f[k] is None: f[k]=mf[k]
    f["laptop"]=kw["laptop"] or mf["laptop"]; return f
def gold_from(e):
    if "settings" in e: return parse(e["settings"])
    f=dict(game=GBY[e["g"]],gpu=PBY[e["gpu"]],laptop=bool(e["lap"]),res=e["res"] or None,fps=e["fps"],target=e["tgt"],
           prefs=set(filter(None,e["pref"].split(","))),asks=set(filter(None,e["ask"].split(","))))
    return decide(f)
def run(rows,m):
    c=collections.Counter()
    for e in rows:
        y=gold_from(e); kw=extract(e["x"]); c["rules"]+=same(settle(kw),y)
        if m:
            mf=mfeats(m.gen(e["x"])); c["model"]+=same(settle(mf),y); c["hybrid"]+=same(settle(hybrid(kw,mf)),y)
    return {k:round(v/len(rows),3) for k,v in c.items()}|{"n":len(rows)}
if __name__=="__main__":
    m=None
    if len(sys.argv)>1:
        from assist import load_model; m=load_model(sys.argv[1])
    for name,path,n in (("FRESH blind (35)","data/blind2.jsonl",None),("old dev (25)","data/freeform.jsonl",None),("held-out families (r3_test)","data/r3_test.jsonl",int(sys.argv[2]) if len(sys.argv)>2 else 300),("old unseen-phrasing test","data/r2_test.jsonl",int(sys.argv[2]) if len(sys.argv)>2 else 300)):
        rows=[json.loads(l) for l in open(path)][:n]; print(name, run(rows,m), flush=True)
