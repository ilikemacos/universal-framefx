import json, sys, collections
from rules import parse, valid, SLOTS
from assist import answer, rule_answer, load_model
rows=[json.loads(l) for l in open(sys.argv[1])][:int(sys.argv[2]) if len(sys.argv)>2 else None]
m=load_model(sys.argv[3] if len(sys.argv)>3 else "ckpt.pt"); raw_ok=exact=final_exact=rb_exact=0; slot=collections.Counter()
for e in rows:
    y=parse(e["y"]); p=parse(m.gen(e["x"])); raw_ok+=valid(p)
    exact+=all(p.get(k)==y[k] for k in SLOTS if k!="why"); [slot.__setitem__(k,slot[k]+(p.get(k)==y[k])) for k in SLOTS]
    d,_=answer(e["x"],None); d2,_=answer(e["x"],m)
    final_exact+= d2 is not None and all(d2[k]==y[k] for k in SLOTS if k!="why")
    rb_exact+= d is not None and all(d[k]==y[k] for k in SLOTS if k!="why")
n=len(rows); print(f"n={n} raw_valid={raw_ok/n:.3f} raw_exact(settings)={exact/n:.3f} final_exact(model+validator+fallback)={final_exact/n:.3f} rules_only_from_text={rb_exact/n:.3f}")
print("per-slot:",{k:round(v/n,3) for k,v in slot.items()})
