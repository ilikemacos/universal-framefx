"""Round 2: 1M from-scratch model extracts request details; rule engine computes settings."""
import sys, json, argparse
from kb import GAMES, GPUS
from rules import decide, parse, SLOTS
from assist import extract, profile, explain, write_copy, load_model
GBY={g[1].lower():g for g in GAMES}; PBY={g[0].lower():g for g in GPUS}
PREFS={"blur","latency","quality","battery"}; ASKS={"fsr1","fsr2","fsr3","xess","fsr4","fsr3fg","ssgi","rt","cpu","csr12"}
def num(v): return int(v) if v.isdigit() and 10<=int(v)<=500 else None
def feats_from_model(line):
    """Returns (features, ok_fields). Every field validated; bad fields marked."""
    d=parse(line); f={}; bad=[]
    f["game"]=GBY.get(d.get("g","")); bad+= [] if f["game"] else ["game"]
    f["gpu"]=PBY.get(d.get("gpu","")); bad+= [] if f["gpu"] else ["gpu"]
    f["laptop"]=d.get("lap")=="1"; bad+= [] if d.get("lap") in ("0","1") else ["lap"]
    f["res"]=d.get("res") or None; bad+= [] if f["res"] in (None,"1080","1440","4k") else ["res"]
    f["fps"]=num(d.get("fps","")) ; f["target"]=num(d.get("tgt",""))
    p=set(filter(None,d.get("pref","").split(","))); a=set(filter(None,d.get("ask","").split(",")))
    if not p<=PREFS: bad.append("pref")
    if not a<=ASKS: bad.append("ask")
    f["prefs"]=p&PREFS; f["asks"]=a&ASKS
    return f,bad
def answer2(text, model):
    f,bad=feats_from_model(model.gen(text)); kw=extract(text); src="model"
    for k,fk in (("game","game"),("gpu","gpu"),("lap","laptop"),("res","res"),("pref","prefs"),("ask","asks")):
        if k in bad: f[fk]=kw[fk]; src="model+keyword fallback("+",".join(bad)+")"
    if f["gpu"] is None: f["gpu"]=PBY["rtx 3060"]
    if f["game"] is None: return None,f,"unknown game"
    o=decide(f); return {k:o[k] for k in SLOTS},f,src
if __name__=="__main__":
    ap=argparse.ArgumentParser(); ap.add_argument("--ckpt",default="ckpt_r2.pt"); ap.add_argument("--write"); ap.add_argument("request",nargs="*"); a=ap.parse_args()
    m=load_model(a.ckpt); reqs=[" ".join(a.request)] if a.request else iter(lambda: input("you> "),"")
    for t in reqs:
        d,f,src=answer2(t,m)
        if d is None: print("Sorry, I don't know that game yet. Try its full name."); continue
        print(json.dumps({d["exe"]:profile(d)},indent=2)); print(explain(d)); print(f"[source: {src}; read as: {f['game'][0]}, {f['gpu'][0]}{' laptop' if f['laptop'] else ''}, fps={f['fps']}, target={f['target']}, prefs={sorted(f['prefs'])}, asks={sorted(f['asks'])}]")
        if a.write: print("merged into copy:",write_copy(d["exe"],profile(d),a.write))

def answer3(text, model):
    """Hybrid: keyword extractor first; the model fills only fields the keywords missed (game, gpu, fps, target, res)."""
    kw=extract(text); src="keywords"
    if kw["game"] is None or kw["gpu"] is None or (kw["fps"] is None and kw["target"] is None) or kw["res"] is None:
        f,bad=feats_from_model(model.gen(text))
        for k in ("game","gpu","res","fps","target"):
            if kw[k] is None and f.get(k) is not None: kw[k]=f[k]; src="keywords+model"
    if kw["gpu"] is None: kw["gpu"]=PBY["rtx 3060"]
    if kw["game"] is None: return None,kw,"unknown game"
    o=decide(kw); return {k:o[k] for k in SLOTS},kw,src
