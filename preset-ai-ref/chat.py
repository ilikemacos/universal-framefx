"""Multi-turn chat mode for the preset assistant. Each turn is parsed (rules first, 1M model fills gaps) as an UPDATE
to a session state; the rule engine rebuilds the preset and a templated reply explains what changed and any warnings."""
import re, json
from kb import GAMES, GPUS
from rules import decide, SLOTS
from extract import extract
from evaluate3 import mfeats
from assist import profile, explain, WHY, BACK
FG=["Off","CSR 1.3","CSR 2.0","FSR 3","CSR AI"]; PRESET=["Performance","Quality","Balanced"]
RES={"0":"native","1":"1440p","2":"4K","3":"720p","4":"1080p"}
OFFTOPIC="I only help with game presets for FrameFX. Tell me a game and your GPU (e.g. \"Cyberpunk on a 3060, want 120 fps\") and I'll build one."
class Chat:
    def __init__(s,model=None): s.m=model; s.reset()
    def reset(s):
        s.f=dict(game=None,gpu=None,laptop=False,res=None,fps=None,target=None,prefs=set(),asks=set()); s.fg_off=False; s.last=None
    def _intents(s,t):
        t=t.lower(); ch=[]
        if re.search(r"\b(start over|reset|new game|forget it)\b",t): s.reset(); return ["reset"]
        if re.search(r"(frame ?gen\w*|fg|generated frames)\W+(off|disabled?)|(turn|switch|shut) off (the )?(frame ?gen|fg)|no (frame ?gen|fg)|disable (frame ?gen|fg)",t): s.fg_off=True; ch.append("fg_off")
        elif re.search(r"(frame ?gen|fg)\W+(on|back)|(turn|switch) (on|back on) (the )?(frame ?gen|fg)|enable (frame ?gen|fg)",t): s.fg_off=False; ch.append("fg_on")
        if re.search(r"quiet|cooler|less (noise|heat|power)|fans? (are )?(too )?loud|save power",t): s.f["prefs"]=(s.f["prefs"]-{"quality"})|{"battery"}; ch.append("quiet")
        if re.search(r"prettier|better looking|more quality|look better|max(imum)? quality|looks? (bad|ugly)",t): s.f["prefs"]=(s.f["prefs"]-{"battery"})|{"quality"}; ch.append("quality")
        if re.search(r"(don'?t|do not|no longer) care about (blur|ghosting)|blur is (fine|ok)",t): s.f["prefs"].discard("blur"); ch.append("blur_off")
        if re.search(r"(don'?t|do not|no longer) care about (latency|input lag|lag)",t): s.f["prefs"].discard("latency"); ch.append("lat_off")
        if re.search(r"\b(desktop|not a laptop|plugged in)\b",t): s.f["laptop"]=False; s.f["prefs"].discard("battery"); ch.append("desktop")
        return ch
    def turn(s,text):
        intents=s._intents(text)
        if re.fullmatch(r"\W*(hi|hello|hey|yo|help|can you help( me)?|what can you do)\W*(can you help( me)?)?\W*",text.lower()):
            return "Hi! I build FrameFX presets. Tell me your game, GPU, and what fps you get vs. want (e.g. \"Elden Ring on a 3060, 50 fps, want 100\")."
        if intents==["reset"]: return "OK, starting over. Which game, and what GPU do you have?"
        kw=extract(text); mf=mfeats(s.m.gen(text)) if s.m else None; has_num=bool(re.search(r"\d|hundred|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety",text.lower()))
        old=dict(s.f,prefs=set(s.f["prefs"]),asks=set(s.f["asks"])); changed=[]
        on_topic=any(kw[k] is not None for k in ("game","gpu","res","fps","target")) or kw["prefs"] or kw["asks"] or kw["laptop"] or re.search(r"\b(fps|frames?|gpu|preset|settings|frame ?gen|upscal\w*|rtx|gtx|rx|arc)\b",text.lower())
        if not on_topic: mf=None
        tl=text.lower()
        for gg in GAMES:
            for al in [gg[0]]+gg[2]: tl=tl.replace(al.lower()," ")
        tl=text.lower()
        for gg in GAMES:
            for al in sorted([gg[0]]+gg[2],key=len,reverse=True): tl=tl.replace(al.lower()," ")
        gpu_hint=bool(re.search(r"\b(rtx|gtx|rx|arc|radeon|geforce|b580|a770)\b|\b(?!1440\b|2160\b)\d{2}[0-9]0\b(?!\s?(p|fps|hz)\b)",tl))
        for k in ("game","gpu","res","fps","target"):
            v=kw[k]
            if v is None and mf:
                if k=="game" and s.f["game"] is None: v=mf["game"]
                elif k in ("fps","target") and has_num: v=mf[k]
                elif k=="gpu" and gpu_hint: v=mf[k]
            if v is not None and v!=s.f[k]: s.f[k]=v; changed.append(k)
        if kw["laptop"] and not s.f["laptop"]: s.f["laptop"]=True; changed.append("laptop")
        for p in kw["prefs"]-s.f["prefs"]:
            s.f["prefs"].add(p); changed.append("pref:"+p)
            if p=="quality": s.f["prefs"].discard("battery")
            if p=="battery": s.f["prefs"].discard("quality")
        for a in kw["asks"]-s.f["asks"]: s.f["asks"].add(a); changed.append("ask:"+a)
        if s.f["fps"] and s.f["target"] and s.f["target"]<=s.f["fps"]:
            if "fps" in changed and "target" not in changed: s.f["target"]=None
            elif "target" in changed and "fps" not in changed: s.f["fps"]=None
        if not changed and not intents:
            return OFFTOPIC if s.f["game"] is None else "I didn't catch a change there. You can say things like \"what about 4K\", \"make it quieter\", \"turn frame gen off\" or \"actually I have a 4070\"."
        if s.f["game"] is None: return "Got it. Which game is this for?"+("" if s.f["gpu"] else " And what GPU do you have?")
        f=dict(s.f); assumed=f["gpu"] is None
        if assumed: f["gpu"]=next(g for g in GPUS if g[0]=="RTX 3060")
        o=decide(f)
        if s.fg_off and o["fg"]!="0": o["fg"]="0"; o["m"]="0"; o["why"]+=",user_fg_off"
        prev=s.last; s.last=o; return s._reply(o,prev,changed+intents,assumed)
    def _reply(s,o,prev,changed,assumed):
        g=next(x for x in GAMES if x[1].lower()==o["exe"]); fg=FG[int(o["fg"])]+(f" {o['m']}x" if o["fg"]!="0" else "")
        heard=[]; f=s.f
        for c in changed:
            if c=="game": heard.append(f"game {g[0]}")
            elif c=="gpu": heard.append(f"GPU {f['gpu'][0]}"+(" (laptop)" if f["laptop"] else ""))
            elif c=="res": heard.append(f"resolution {f['res']}".replace("4k","4K"))
            elif c=="fps": heard.append(f"you're getting ~{f['fps']} fps")
            elif c=="target": heard.append(f"target {f['target']} fps")
            elif c=="laptop": heard.append("it's a laptop")
            elif c.startswith("pref:"): heard.append({"blur":"you dislike blur","latency":"latency matters to you","quality":"you want max quality","battery":"you want it quiet / battery-friendly"}[c[5:]])
            elif c.startswith("ask:"): heard.append(f"you asked for {c[4:]}")
            elif c=="fg_off": heard.append("frame generation off")
            elif c=="fg_on": heard.append("frame generation allowed again")
            elif c=="quiet": heard.append("quieter / cooler")
            elif c=="quality": heard.append("better looking")
            elif c=="desktop": heard.append("desktop, not laptop")
            elif c in ("blur_off","lat_off"): heard.append("dropping that preference")
        lines=["Noted: "+", ".join(heard)+"." if heard else ""]
        now={"Preset":PRESET[int(o["p"])],"Frame gen":fg,"Upscaler":BACK[int(o["b"])],"Resolution":RES[o["r"]],"SSGI":"on" if o["gi"]=="1" else "off","RT lighting":"on" if o["rt"]=="1" else "off","CPU boost":"on" if o["cpu"]=="1" else "off"}
        if prev is None: lines.append(f"Here's a preset for {g[0]}: "+", ".join(f"{k} {v}" for k,v in list(now.items())[:4])+".")
        else:
            pv={"Preset":PRESET[int(prev["p"])],"Frame gen":FG[int(prev["fg"])]+(f" {prev['m']}x" if prev["fg"]!="0" else ""),"Upscaler":BACK[int(prev["b"])],"Resolution":RES[prev["r"]],"SSGI":"on" if prev["gi"]=="1" else "off","RT lighting":"on" if prev["rt"]=="1" else "off","CPU boost":"on" if prev["cpu"]=="1" else "off"}
            if prev["exe"]!=o["exe"]: lines.append(f"Switched to {g[0]}: "+", ".join(f"{k} {v}" for k,v in list(now.items())[:4])+".")
            else:
                diff=[f"{k}: {pv[k]} → {now[k]}" for k in now if pv[k]!=now[k]]
                lines.append("Changes: "+"; ".join(diff)+"." if diff else "No settings change needed. The current preset already covers that: "+", ".join(f"{k} {v}" for k,v in list(now.items())[:3])+".")
        why=[w for w in o["why"].split(",") if w]
        if "user_fg_off" in why: lines.append("Frame generation is off because you asked.")
        for w in why:
            if w in ("perf","quality","headroom") and prev is not None and prev["p"]==o["p"]: continue
            if WHY.get(w) and (w.startswith(("anticheat","competitive")) or prev is None or prev["exe"]!=o["exe"] or w not in prev["why"].split(",")): lines.append(WHY[w])
        if "user_fg_off" in why: lines=[l for l in lines if l!=WHY.get("at_target")]
        if assumed: lines.append("(I assumed an RTX 3060 since you didn't say your GPU. Tell me yours for a better fit.)")
        return "\n".join(l for l in lines if l)
    def profile_json(s): return None if s.last is None else {s.last["exe"]:profile({k:s.last[k] for k in SLOTS})}
