import random, json
from gen import sample, TPL_TRAIN, TPL_TEST
def feat_line(f):
    return ";".join([f"g={f['game'][1].lower()}",f"gpu={f['gpu'][0].lower()}",f"lap={int(f['laptop'])}",f"res={f['res'] or ''}",
        f"fps={f['fps'] or ''}",f"tgt={f['target'] or ''}","pref="+",".join(sorted(f['prefs'])),"ask="+",".join(sorted(f['asks']))])
r=random.Random(1)
for name,n,t in (("train",60000,TPL_TRAIN),("val",1000,TPL_TRAIN),("test",1000,TPL_TEST)):
    with open(f"data/r2_{name}.jsonl","w") as fh:
        for _ in range(n): x,y,f=sample(r,t); fh.write(json.dumps({"x":x,"y":feat_line(f),"settings":y})+"\n")
