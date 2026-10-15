import json
from evaluate3 import *
from assist import load_model
m=load_model("ckpt_r3.pt")
for e in [json.loads(l) for l in open("data/blind2.jsonl")]:
    y=gold_from(e); kw=extract(e["x"]); line=m.gen(e["x"]); mf=mfeats(line); h=settle(hybrid(kw,mf))
    if not same(h,y) or not same(settle(mf),y):
        print("HYB_OK" if same(h,y) else "HYB_MISS","|",e["x"],"|MODEL:",line,"|GOLD:",{k:e[k] for k in ("g","gpu","lap","res","fps","tgt","pref","ask")})
