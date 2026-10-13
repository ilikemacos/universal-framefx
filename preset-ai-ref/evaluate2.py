import json, sys, collections
from kb import GAMES, GPUS
from rules import parse, decide, SLOTS
from assist import answer, load_model
from assist2 import answer2, answer3, GBY, PBY
K=[k for k in SLOTS if k!="why"]
def same(d,y): return d is not None and all(d[k]==y[k] for k in K)
def run(rows, m2, m1=None):
    c=collections.Counter(); n=len(rows)
    for e in rows:
        y=e["gold"]; d2,f,_=answer2(e["x"],m2); dr,_=answer(e["x"],None)
        c["r2"]+=same(d2,y); c["rules"]+=same(dr,y); d3,_,_=answer3(e["x"],m2); c["hybrid"]+=same(d3,y)
        if m1 and m1!='skip': d1,_=answer(e["x"],m1); c["r1"]+=same(d1,y)
        gf=e.get("gf")
        if gf:
            c["f_game"]+= f["game"] is not None and f["game"][1].lower()==gf["g"]; c["f_gpu"]+= f["gpu"][0].lower()==gf["gpu"]
            c["f_fps"]+= f["fps"]==gf["fps"] and f["target"]==gf["tgt"]; c["f_prefasks"]+= f["prefs"]==gf["prefs"] and f["asks"]==gf["asks"]
    return {k:round(v/n,3) for k,v in c.items()}|{"n":n}
def feats(gf):
    return dict(game=GBY[gf["g"]],gpu=PBY[gf["gpu"]],laptop=bool(gf["lap"]),res=gf["res"] or None,fps=gf["fps"],target=gf["tgt"],
                prefs=set(filter(None,gf["pref"].split(","))),asks=set(filter(None,gf["ask"].split(","))))
if __name__=="__main__":
    n=int(sys.argv[1]); m2=load_model(sys.argv[2]); m1=load_model(sys.argv[3]) if len(sys.argv)>3 else None
    test=[]
    for l in list(open("data/r2_test.jsonl"))[:n]:
        e=json.loads(l); d=parse(e["y"]); gf=dict(g=d["g"],gpu=d["gpu"],lap=int(d["lap"]),res=d["res"],fps=int(d["fps"]) if d["fps"] else None,tgt=int(d["tgt"]) if d["tgt"] else None,
            pref=d["pref"],ask=d["ask"]); f=feats(gf); gf|={"prefs":f["prefs"],"asks":f["asks"]}
        test.append({"x":e["x"],"gold":parse(e["settings"]),"gf":gf})
    print("unseen-phrasing test:",run(test,m2,m1))
    ff=[]
    for l in open("data/freeform.jsonl"):
        gf=json.loads(l); f=feats(gf); gf|={"prefs":f["prefs"],"asks":f["asks"]}; ff.append({"x":gf["x"],"gold":decide(f),"gf":gf})
    print("free-form (hand-written):",run(ff,m2,m1))
