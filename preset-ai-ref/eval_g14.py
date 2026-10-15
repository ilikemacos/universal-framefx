"""Final eval (r3 style): rules / model / hybrid on fresh blind (blind2+blind3), dev25, held-out families, old test, r5 held-out, style."""
import json, sys, torch
import evaluate3 as E
from model_g14 import GPT
def load(p):
    sd=torch.load(p,map_location="cpu"); d=sd["tok.weight"].shape[1]; L=1+max(int(k.split(".")[1]) for k in sd if k.startswith("blocks."))
    m=GPT(d,L,4 if d==128 else 8); m.load_state_dict(sd); m.eval(); return m
def run(rows,m):
    outs=[]
    for i in range(0,len(rows),64): outs+=m.gen_batch([e["x"] for e in rows[i:i+64]])
    c={"rules":0,"model":0,"hybrid":0}
    for e,o in zip(rows,outs):
        y=E.gold_from(e); kw=E.extract(e["x"]); mf=E.mfeats(o)
        c["rules"]+=E.same(E.settle(kw),y); c["model"]+=E.same(E.settle(mf),y); c["hybrid"]+=E.same(E.settle(E.hybrid(kw,mf)),y)
    return {k:round(v/len(rows),3) for k,v in c.items()}|{"n":len(rows)}
if __name__=="__main__":
    torch.set_num_threads(6); m=load(sys.argv[1]); J=lambda p,n=None:[json.loads(l) for l in open(p)][:n]
    b2=J("data/blind2.jsonl"); b3=J("data/blind3.jsonl")
    for name,rows in (("FRESH blind2+blind3 (85)",b2+b3),("blind3 (50)",b3),("blind2 (35, dev)",b2),("old dev25",J("data/freeform.jsonl")),
                      ("r3 held-out families",J("data/r3_test.jsonl",300)),("old unseen-phrasing",J("data/r2_test.jsonl",300)),
                      ("r5 held-out families",J("data/r5_test.jsonl",300)),("r5 held-out style",J("data/r5_style.jsonl",300))):
        print(sys.argv[1],name,run(rows,m),flush=True)
