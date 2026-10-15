import random, json, sys
from kb import GAMES, GPUS
from rules import decide, to_line
PREF = {"blur":["hate blur","no blur please","blur makes me sick","keep it sharp, no smear","i hate ghosting"],
 "latency":["low input lag","i care about latency","responsive controls matter most","no input delay"],
 "quality":["max quality","best looking","make it look amazing","eye candy","highest quality"],
 "battery":["on battery","save battery","quiet and cool"]}
ASK = {"fsr1":["use fsr 1","fsr1 upscaling"],"fsr2":["use fsr 2"],"fsr3":["fsr 3 upscaler please"],"xess":["use xess","intel xess"],
 "fsr4":["use fsr 4","fsr4"],"fsr3fg":["use fsr 3 frame gen","amd frame generation"],"ssgi":["turn on ssgi","better lighting with ssgi"],
 "rt":["ray traced lighting","enable rt lighting"],"cpu":["cpu boost","my cpu stutters, boost it"],"csr12":["use csr 1.2"]}
RES = {"1080":["1080p","1080p monitor","fhd"],"1440":["1440p","1440p screen","qhd"],"4k":["4k","4k tv","2160p"]}
TPL_TRAIN = ["{game} on a {gpu}{lap}, want {target} fps{res}{prefs}","playing {game} with my {gpu}{lap}{res}. target {target}fps{prefs}",
 "{gpu}{lap} {game} {target}fps{prefs}{res}","preset for {game}? i have a {gpu}{lap}{prefs}{res}","{game}, {gpu}{lap}, getting {fps} fps, want {target}{prefs}{res}",
 "i want {target} fps in {game}. gpu: {gpu}{lap}{res}{prefs}","help me set up {game} on {gpu}{lap}{prefs}"]
TPL_TEST = ["can you make a {game} preset, {gpu}{lap}, aiming for {target} fps{prefs}{res}","{game} | {gpu}{lap} | {target} fps{res}{prefs}",
 "running {game} at {fps} fps on {gpu}{lap}, need {target}{prefs}"]
def typo(s,r):
    if r.random()<0.15 and len(s)>8:
        i=r.randrange(len(s)-1); s=s[:i]+s[i+1]+s[i]+s[i+2:]
    return s
def sample(r, tpls):
    g=r.choice(GAMES); gpu=r.choice(GPUS); lap=r.random()<0.35
    tpl=r.choice(tpls); res=r.choice([None,"1080","1440","4k"]) if "{res}" in tpl else None
    fps = r.choice([30,40,45,50,60,70,80,90,100]) if "{fps}" in tpl else None
    target = r.choice([60,90,100,120,144,165,180,240]) if "{target}" in tpl else None
    prefs=set(k for k in PREF if r.random()<0.12); asks=set(k for k in ASK if r.random()<0.04)
    if "quality" in prefs and "battery" in prefs: prefs.discard("battery")
    gname = r.choice([g[0], g[0].lower()]+g[2]); gpun=r.choice([gpu[0], gpu[0].lower()]+gpu[1])
    ptxt="".join(", "+r.choice(PREF[k]) for k in sorted(prefs,key=lambda _:r.random()))+"".join(", "+r.choice(ASK[k]) for k in asks)
    text=tpl.format(game=gname,gpu=gpun,lap=(" "+r.choice(["laptop","laptop gpu","mobile"])) if lap else "",
        target=target,fps=fps,res=(", "+r.choice(RES[res])) if res else "",prefs=ptxt)
    text=typo(text.lower() if r.random()<0.5 else text, r)
    f=dict(game=g,gpu=gpu,laptop=lap,res=res,fps=fps,target=target,prefs=prefs,asks=asks)
    return text, to_line(decide(f)), f
if __name__=="__main__":
    r=random.Random(1)
    for name,n,t in (("train",60000,TPL_TRAIN),("val",1000,TPL_TRAIN),("test",1000,TPL_TEST)):
        with open(f"data/{name}.jsonl","w") as fh:
            for _ in range(n): x,y,_=sample(r,t); fh.write(json.dumps({"x":x,"y":y})+"\n")
