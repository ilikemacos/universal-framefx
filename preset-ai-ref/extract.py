"""Rule extractor v2 (round 3). Returns features; slot None/empty = not mentioned."""
import re
from kb import GAMES, GPUS
def norm(t): return re.sub(r"\s+"," ",re.sub(r"[’'`]","",t.lower()))
def _find(items, names, t):
    best=None
    for it in items:
        for a in names(it):
            a=norm(a); m=re.search(r"(?<![\w])"+re.escape(a)+r"(?![\w])",t)
            if m and (best is None or len(a)>best[0]): best=(len(a),it,m.span())
    return best
PREF={"blur":r"blur|smear|ghost|sharp","latency":r"latency|input lag|input delay|responsive|low lag",
 "quality":r"max(imum)? quality|best quality|high(est)? quality|gorgeous|eye candy|look(s)? (as good|amazing|great|incredible|stunning)|best look|max settings|ultra quality|pretty as possible",
 "battery":r"battery|unplugged|quiet|cool and quiet|plugged out"}
ASK={"fsr3fg":r"fsr ?3 (frame ?gen|fg)|amd frame gen","fsr4":r"fsr ?4","fsr1":r"fsr ?1(?!\d)","fsr2":r"fsr ?2(?!\d)","fsr3":r"fsr ?3(?! ?(frame|fg))",
 "xess":r"xess","ssgi":r"ssgi","rt":r"ray.?trac|\brt lighting","cpu":r"cpu boost|boost (my |the )?cpu","csr12":r"csr ?1\.2"}
CUR=r"(getting|get|at|sitting at|around|about|roughly|like|currently|runs at|running at|base|baseline|i'?m at|stuck at|only|now|hovering)"
TGT=r"(run at|can (i|we) get|i'?d take|push it to|want|target|targeting|aim|aiming( for)?|need|to|for|happy with|love|hit|reach|get me|->|→|into)"
def extract(text):
    t=norm(text); f=dict(game=None,gpu=None,laptop=bool(re.search(r"laptop|mobile|notebook",t)),res=None,fps=None,target=None,prefs=set(),asks=set())
    g=_find(GAMES,lambda g:[g[0]]+g[2]+[g[1][:-4]],t); p=_find(GPUS,lambda g:[g[0]]+g[1],t)
    s=t
    for hit in (g,p):
        if hit: f["game" if hit is g else "gpu"]=hit[1]; a,b=hit[2]; s=s[:a]+" "*(b-a)+s[b:]
    for k,pat in (("1080",r"1080p|\bfhd\b|\b1080\b"),("1440",r"1440p|\bqhd\b|\b1440\b|\b2k\b"),("4k",r"\b4k\b|2160p|\buhd\b")):
        if re.search(pat,s): f["res"]=k
    s=re.sub(r"1080p?|1440p?|2160p?|\d+ ?hz|\d+ ?gb|\b20\d\d\b","  ",s)
    for k,pat in PREF.items():
        if re.search(pat,s): f["prefs"].add(k)
    for k,pat in ASK.items():
        if re.search(pat,s): f["asks"].add(k)
    nums=[(m.start(),int(m.group(1))) for m in re.finditer(r"(?<![\w.])(\d{2,3})(?!\d|\.\d)",s) if 15<=int(m.group(1))<=500]
    cur=tgt=None
    for pos,n in nums:
        before=s[max(0,pos-22):pos]; after=s[pos:pos+30]
        if re.search(r"(i'?d|would) (like|love|be happy with)\s*$|"+TGT+r"\s*(like |about |around )?$",before) and not re.search(r"(runs at|at|getting|sitting at|currently)\s*(like )?$",before): tgt=n
        elif re.search(CUR+r"\s*$",before) or re.search(r"^\d+\s*(fps)?\s*(now|currently|base|right now|locked|->|→|to \d)",after): cur=n
        elif re.search(r"^\d+\s*(fps)?\s*would be (ideal|nice|great)",after): tgt=n
        elif tgt is None and cur is not None and n!=cur: tgt=n
        elif tgt is None: tgt=n
    if cur is not None and tgt is None and re.search(r"(want|aim|target)",s) is None: pass
    f["fps"],f["target"]=cur,tgt
    return f
