"""Rule engine = teacher + fallback. See rules.md. Defaults pending Mica are marked DPM."""
import math
from kb import GAMES, GPUS
SLOTS = ["exe","p","fg","m","b","r","gi","rt","cpu","why"]
LEGAL = {"p":{"0","1","2"},"fg":{"0","1","2","3","4"},"m":{"0","2","3","4","8"},"b":{str(i) for i in range(10)},
         "r":{"0","1","2","3","4"},"gi":{"0","1"},"rt":{"0","1"},"cpu":{"0","1"}}
EXES = {g[1].lower() for g in GAMES}
def base_fps(game, gpu, laptop, res):
    s = gpu[2]*(0.75 if laptop else 1.0)
    rf = {"1080":1.0,"1440":0.65,"4k":0.32,None:0.8}[res]
    return max(8, round(s*rf*6.0/game[3]**1.1))
def decide(f):
    """f: dict game, gpu, laptop, res, fps(current, opt), target, prefs(set), asks(set). Returns slot dict."""
    g, gpu, prefs, asks = f["game"], f["gpu"], f["prefs"], f["asks"]
    why=[]
    base = f.get("fps") or base_fps(g, gpu, f["laptop"], f["res"])
    target = f.get("target") or (144 if g[5] else 120)
    o = dict(exe=g[1].lower() if g else "?", p="0", fg="0", m="0", b="0", r="0", gi="0", rt="0", cpu="0")
    o["r"] = {"1080":"4","1440":"1","4k":"2",None:"0"}[f["res"]]
    # preset: Quality if headroom or max quality; Performance on laptop/battery/low tier
    if "quality" in prefs and not ("battery" in prefs): o["p"]="1"; why.append("quality")
    elif gpu[2]>=45 and not f["laptop"] and base>=target/2: o["p"]="1"; why.append("headroom")
    else: why.append("perf")
    if f["laptop"]: why.append("laptop")
    # frame gen
    if g[5] or g[6]:
        why.append("anticheat" if g[6] else "competitive")   # DPM: FG off + warning, never Competitive 8x
        if g[6] and not g[5]: why.append("ac_warn")
    elif "latency" in prefs:
        why.append("latency")
    elif base >= target*0.9:
        why.append("at_target")
    else:
        m = math.ceil(target/base); m = min(max(m,2),4)
        if "blur" in prefs and m>2: m=2; why.append("blur")
        o["fg"]="1"; o["m"]=str(m)
        if "fsr3fg" in asks: o["fg"]="3"; why.append("asked_fsr3fg")
        if base < 35: why.append("lowbase")
        if g[4]: why.append("hud")
    # upscaler (DPM: CSR 1.3 unless asked)
    for k,v in (("fsr1","3"),("fsr2","4"),("fsr3","5"),("xess","7"),("csr12","1")):
        if k in asks: o["b"]=v; why.append("asked_"+k)
    if "fsr4" in asks or ("quality" in prefs and gpu[4]=="rx9000"):
        if gpu[4]=="rx9000": o["b"]="6"; why.append("fsr4")
        else: why.append("no_fsr4")
    if "ssgi" in asks: o["gi"]="1"; why.append("ssgi")
    if "rt" in asks: o["rt"]="1"; o["gi"]="0"; why.append("rt")
    if "cpu" in asks: o["cpu"]="1"; why.append("cpu")
    o["why"]=",".join(why); o["_base"]=base; o["_target"]=target
    return o
def to_line(o): return ";".join(f"{k}={o[k]}" for k in SLOTS)
def parse(line):
    d={}
    for part in line.strip().split(";"):
        if "=" in part: k,v=part.split("=",1); d[k.strip()]=v.strip()
    return d
def valid(d):
    if any(k not in d for k in SLOTS): return False
    if d["exe"]!="?" and d["exe"] not in EXES: return False
    for k,s in LEGAL.items():
        if d[k] not in s: return False
    if d["b"]=="6" and "fsr4" not in d["why"]: return False
    return True
