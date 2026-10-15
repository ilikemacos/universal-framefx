"""Preset assistant: model -> validator -> rule fallback -> GameProfile JSON + explanation."""
import re, json, sys, os, shutil, argparse
from kb import GAMES, GPUS
from rules import decide, parse, valid, SLOTS, to_line
from gen import PREF, ASK
from extract import extract  # v2 (round 3)
def extract_v1(t):
    t0=t.lower(); f=dict(laptop=bool(re.search(r"laptop|mobile|notebook",t0)),prefs=set(),asks=set(),fps=None,target=None,res=None)
    def best(items, names):
        hits=[(len(a),it) for it in items for a in names(it) if re.search(r"(?<![\w])"+re.escape(a.lower())+r"(?![\w])",t0)]
        return max(hits,key=lambda h:h[0])[1] if hits else None
    f["game"]=best(GAMES,lambda g:[g[0]]+g[2]+[g[1][:-4]]); f["gpu"]=best(GPUS,lambda g:[g[0]]+g[1])
    for k,ph in PREF.items():
        if any(p in t0 for p in ph) or (k=="blur" and "blur" in t0) or (k=="latency" and ("latency" in t0 or "input lag" in t0)): f["prefs"].add(k)
    for k,ph in ASK.items():
        if any(p in t0 for p in ph): f["asks"].add(k)
    for k,pat in (("1080",r"1080p|fhd"),("1440",r"1440p|qhd"),("4k",r"\b4k\b|2160p")):
        if re.search(pat,t0): f["res"]=k
    m=re.search(r"(?:getting|at|running at)\s*(\d{2,3})\s*fps",t0); f["fps"]=int(m.group(1)) if m else None
    m=re.search(r"(?:want|target|aiming for|need|for)\s*(\d{2,3})",t0) or re.search(r"(\d{2,3})\s*fps",t0)
    if m and (f["fps"] is None or int(m.group(1))!=f["fps"]): f["target"]=int(m.group(1))
    return f
def rule_answer(text):
    f=extract(text)
    if not f["gpu"]: f["gpu"]=next(g for g in GPUS if g[0]=="RTX 3060")
    if not f["game"]: return None
    return decide(f)
def load_model(path="ckpt.pt"):
    import torch; from model import GPT
    torch.set_num_threads(int(os.environ.get("THR","2"))); m=GPT(); m.load_state_dict(torch.load(path)); m.eval(); return m
def answer(text, model=None):
    src="model"; d=None
    if model is not None:
        d=parse(model.gen(text))
        if not valid(d): d=None
    ra=rule_answer(text)
    if d is None:
        if ra is None: return None,"unknown game"
        d={k:ra[k] for k in SLOTS}; src="rules (model output invalid)" if model else "rules"
    return d,src
BACK=["CSR 1.3","CSR 1.2","Bilinear","FSR 1","FSR 2","FSR 3","FSR 4","XeSS","CSR 2.0","CSR AI"]
WHY={"quality":"Quality preset because you asked for the best look.","headroom":"Quality preset: your GPU has headroom for it.",
 "perf":"Performance preset (CSR 1.3 low-cost path) to keep GPU time down.","laptop":"Laptop GPU: assumed ~25% less performance.",
 "anticheat":"WARNING: this game uses anti-cheat. Frame generation is left off; overlays can trip some anti-cheats, use at your own risk.",
 "ac_warn":"","competitive":"Competitive shooter: frame generation off to keep input latency low.",
 "latency":"You care about latency, so frame generation is off.","at_target":"You're already near your target fps, so no frame generation needed.",
 "blur":"Capped at 2x frame generation because you dislike blur/ghosting.","lowbase":"Base fps is low (<35); generated frames will show more artifacts. Lowering in-game settings helps.",
 "hud":"HUD-heavy game: watch for HUD shimmer with frame generation.","fsr4":"FSR 4 upscaler: you asked for max quality on an RX 9000 card.",
 "no_fsr4":"FSR 4 needs an RX 9000 card, so CSR 1.3 is used instead.","asked_fsr3fg":"FSR 3 frame generation, as requested.",
 "ssgi":"SSGI on, as requested (experimental).","rt":"Ray-traced lighting on, as requested (experimental, replaces SSGI).","cpu":"CPU boost options on, as requested."}
def profile(d):
    fg=int(d["fg"]); c=d["cpu"]=="1"
    return {"Preset":int(d["p"]),"FrameGen":fg!=0,"FgSelect":fg,"FgMultiplier":int(d["m"]) if fg else 4,"Ssgi":d["gi"]=="1","SsgiPreset":0,"SsgiTemporal":False,
      "Ssrt":d["rt"]=="1","SsrtPreset":0,"SsrtTemporal":False,"Backend":int(d["b"]),"Res":int(d["r"]),
      "Cpu":{"PowerPlan":c,"MinProcessorState100":False,"DisableCoreParking":c,"BoostMode":1 if c else 0,"HighPriority":c,"PreferFastCores":c,
             "AvoidSmtSiblings":False,"TimerResolution1ms":False,"AllPhysicalCores":False,"LowerFrameFxPriority":c,"BackgroundProcesses":[],"DisablePowerThrottling":c},
      "FgV2HudMask":None,"FgV2HudMaskW":64,"FgV2HudMaskH":36}
def explain(d):
    fg=["Off","CSR 1.3","CSR 2.0","FSR 3","CSR AI"][int(d["fg"])]
    lines=[f"Frame generation: {fg}"+(f" {d['m']}x" if d["fg"]!="0" else "")+f" · Upscaler: {BACK[int(d['b'])]}"]
    lines+= [WHY[w] for w in d["why"].split(",") if WHY.get(w)]
    if any(w.startswith("asked_") and w!="asked_fsr3fg" for w in d["why"].split(",")): lines.append("Upscaler set as you requested.")
    return "\n".join(lines)
def write_copy(exe, prof, path):
    """Merge into a COPY only. Never touches the real %APPDATA% ui.json."""
    assert "appdata" not in os.path.abspath(path).lower(), "refusing: only writes to a copy"
    ui=json.load(open(path)) if os.path.exists(path) else {}
    ui.setdefault("Profiles",{})[exe]=prof; json.dump(ui,open(path,"w"),indent=2); return path
def main():
    ap=argparse.ArgumentParser(); ap.add_argument("--write",metavar="UI_JSON_COPY"); ap.add_argument("--src",metavar="ORIGINAL_UI_JSON",help="copied to --write path first")
    ap.add_argument("--model",choices=["rules","r1","r2","r3"],default="r3",help="r3 = hybrid, rules first + 1M model fills empty slots (default; beat rules on fresh blind3 .88 vs .80); rules = rules only; r1 = end-to-end 1M model; r2 = 1M extractor + rules; r3 = rules first, round-3 1M extractor fills empty slots"); ap.add_argument("--chat",action="store_true",help="multi-turn chat REPL"); ap.add_argument("request",nargs="*"); a=ap.parse_args()
    m=None if a.model=="rules" else load_model({"r1":"ckpt_r1_s7500.pt","r2":"ckpt_r2.pt","r3":"ckpt_r3.pt"}[a.model])
    if a.chat:
        from chat import Chat; c=Chat(m); print("FrameFX preset chat. Type 'quit' to exit, 'json' to see the preset.")
        while True:
            try: t=input("you> ").strip()
            except EOFError: break
            if t in ("quit","exit"): break
            if t=="json": print(json.dumps(c.profile_json(),indent=2)); continue
            print("bot>",c.turn(t))
        return
    reqs=[" ".join(a.request)] if a.request else iter(lambda: input("you> "),"")
    for t in reqs:
        if a.model=="r3":
            from evaluate3 import mfeats, hybrid, settle
            kw=extract(t); o=settle(hybrid(kw,mfeats(m.gen(t)))); d={k:o[k] for k in SLOTS} if o else None; src="rules + r3 model (fills empty slots)"
        elif a.model=="r2":
            from assist2 import answer2; d,_,src=answer2(t,m)
        else: d,src=answer(t,m)
        if d is None: print("Sorry, I don't know that game yet. Try its full name."); continue
        exe=d["exe"]; print(json.dumps({exe:profile(d)},indent=2)); print(explain(d)); print(f"[source: {src}]")
        if a.write:
            if a.src and not os.path.exists(a.write): shutil.copy(a.src,a.write)
            print("merged into copy:",write_copy(exe,profile(d),a.write))
if __name__=="__main__": main()
