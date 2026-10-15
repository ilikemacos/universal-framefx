"""Round 3 compositional grammar. Each slot has phrase FAMILIES; families with index in HOLD are test-only."""
import random, json, sys
from kb import GAMES, GPUS
from rules import decide, to_line
F = {
"game":[["{g}"],["playing {g}","i play {g}","im playing {g}"],["for {g}","preset for {g}","settings for {g}"],["{g} on pc","{g} pc"],
        ["in {g}"],["my game is {g}","game: {g}","the game is {g}"],["trying to run {g}","running {g}"],["{g} keeps lagging","{g} is choppy","{g} stutters a lot"]],
"gpu":[["{c}"],["on a {c}","on my {c}"],["with a {c}","with my {c}"],["gpu: {c}","gpu is {c}","card: {c}"],["card is a {c}","my card is a {c}"],
       ["i have a {c}","i got a {c}","ive got a {c}"],["running a {c}","rocking a {c}"],["{c} owner here","{c} user"]],
"lap":[["{c} laptop","{c} mobile","{c} laptop gpu"],["its a laptop","on my laptop","gaming laptop"],["(laptop)","notebook"]],
"cur":[["getting {n}","getting {n} fps"],["at {n} fps","at {n}fps"],["sitting at {n}","sitting at {n} fps"],["about {n} fps","around {n}","roughly {n} fps"],
       ["currently {n}fps","currently {n}"],["{n} fps now","{n} right now"],["runs at like {n}","runs at {n}"],["base is {n}","base fps {n}"],
       ["im at {n}","i'm at {n} fps"],["stuck at {n}","only {n} fps","barely {n}"],["hovering around {n}","averaging {n}"],["{n} locked","locked at {n}"],["it sits at {n}","does {n} fps","i get {n}"]],
"tgt":[["want {n}","want {n} fps","i want {n}"],["target {n}","targeting {n}","{n} fps target"],["aiming for {n}","aim {n}"],["need {n} fps","need {n}"],
       ["i'd be happy with {n}","would be happy with {n}"],["would love {n}","i'd love {n} fps"],["to hit {n}","hit {n} fps"],["goal is {n}","my goal is {n} fps"],
       ["get me to {n}","get me {n} fps"],["trying to reach {n}","reach {n}"],["{n}+ fps","{n} fps please"],["run at {n}","make it run at {n}"],["can i get {n}","can we get {n}?"],["{n} would be ideal","{n} would be nice"],["i'd take {n}","push it to {n}"]],
"curtgt":[["{c} -> {t}","{c} → {t}"],["from {c} to {t}","{c} to {t} fps"],["{c} fps now, want {t}","at {c} want {t}"],["doing {c}, need {t}"],["{c} fps -> {t}","~{c} fps -> {t}","{c}fps → {t}fps"]],
"res":[["{r}"],["at {r}","playing at {r}"],["{r} monitor","{r} screen","{r} display"],["on a {r} tv","{r} tv"],["res: {r}","resolution {r}"]],
"blur":[["hate blur","no blur"],["no smearing","smearing is awful"],["hate ghosting","no ghosting pls"],["keep it sharp","i want it sharp"],["motion blur makes me sick","blurry frames suck"]],
"latency":[["low latency","latency matters"],["no input lag","input lag kills me"],["responsive","needs to feel responsive"],["no input delay","minimal input delay"],["low lag please"]],
"quality":[["max quality","maximum quality"],["best quality","highest quality","high quality"],["gorgeous","eye candy"],["make it look amazing","look as good as possible","look stunning"],["max settings","ultra quality"]],
"battery":[["on battery","save battery"],["unplugged","usually unplugged"],["quiet","cool and quiet"],["battery life matters"]],
"fsr1":[["use fsr 1","fsr1 upscaling","with fsr 1"]],"fsr2":[["use fsr 2","fsr2 please"]],"fsr3":[["fsr 3 upscaler","use fsr3 upscaling"]],
"xess":[["use xess","intel xess","xess please"]],"fsr4":[["use fsr 4","fsr4","give me fsr 4"]],"fsr3fg":[["use fsr 3 frame gen","amd frame gen","fsr3 fg"]],
"ssgi":[["turn on ssgi","add ssgi","ssgi on"]],"rt":[["ray traced lighting","enable ray tracing lighting","rt lighting on"]],"cpu":[["cpu boost","boost my cpu","boost the cpu too"]],"csr12":[["use csr 1.2","csr1.2 please"]],
}
RESN={"1080":["1080p","fhd","1080"],"1440":["1440p","qhd","1440","2k"],"4k":["4k","2160p","uhd"]}
OPEN=["","","hey ","yo ","pls help, ","can you make a preset? ","framefx settings: ","quick q: ","hi! ","need help. ","so ","ok so "]
CLOSE=["","","",", thanks"," thx","?"," pls",". what should i use?"," lol"," tbh"]
SEP=[", ",", ",". "," "," and ","; "," | "," - "," "]
NOISE=[" 16gb ram"," 144hz"," 165hz monitor"," ryzen 5 5600"," i7"," 32gb"," 240hz"]
HOLD={"game":{3,7},"gpu":{4,7},"lap":{2},"cur":{4,9,10},"tgt":{4,7,9},"curtgt":{3},"res":{3},"blur":{4},"latency":{4},"quality":{3},"battery":{3}}
def pick(r,slot,test):
    fams=F[slot]; h=HOLD.get(slot,set())
    idx=[i for i in range(len(fams)) if (i in h)==test] or list(range(len(fams)))
    if test and h and r.random()<0.5: idx=sorted(h)
    return r.choice(fams[r.choice(idx)])
def typo(s,r):
    for _ in range(r.choice([0,0,0,1,2])):
        if len(s)>10: i=r.randrange(len(s)-1); s=s[:i]+s[i+1]+s[i]+s[i+2:]
    return s
def sample(r,test=False):
    g=r.choice(GAMES); gpu=r.choice(GPUS)
    lap= r.random()<0.3; res=r.choice([None,None,"1080","1440","4k"])
    cur=r.choice([None,None,25,30,35,40,45,50,55,60,70,80,90]); tgt=r.choice([None,60,75,90,100,120,120,144,160,165,180,200,240])
    if cur and tgt and tgt<=cur: tgt=None
    prefs={k for k in ("blur","latency","quality","battery") if r.random()<0.12}
    if "quality" in prefs: prefs.discard("battery")
    asks={k for k in ("fsr1","fsr2","fsr3","xess","fsr4","fsr3fg","ssgi","rt","cpu","csr12") if r.random()<0.03}
    gname=r.choice([g[0],g[0]]+g[2]); cname=r.choice([gpu[0],gpu[0].lower()]+gpu[1]+([gpu[0].replace("RTX ","rtx").replace("GTX ","gtx")] if " " in gpu[0] else []))
    cl=[]; game_cl=pick(r,"game",test).format(g=gname)
    gc=pick(r,"gpu",test)
    if lap and r.random()<0.6: cname=r.choice(F["lap"][0]).format(c=cname)
    cl.append(gc.format(c=cname)) if r.random()>0.05 or True else None
    if lap and "laptop" not in cl[-1] and "mobile" not in cl[-1]:
        lc=r.choice([x for fam_i,fam in enumerate(F["lap"]) if fam_i!=0 and ((fam_i in HOLD["lap"])==test) for x in fam] or F["lap"][1]); cl.append(lc)
    if cur and tgt and r.random()<0.35: cl.append(pick(r,"curtgt",test).format(c=cur,t=tgt))
    else:
        if cur: cl.append(pick(r,"cur",test).format(n=cur))
        if tgt: cl.append(pick(r,"tgt",test).format(n=tgt))
    if res: cl.append(pick(r,"res",test).format(r=r.choice(RESN[res])))
    for k in list(prefs)+list(asks): cl.append(pick(r,k,test))
    if r.random()<0.15: cl.append(r.choice(NOISE).strip())
    r.shuffle(cl); cl.insert(0 if r.random()<0.7 else r.randint(0,len(cl)),game_cl)
    txt=r.choice(OPEN)+"".join(c+(r.choice(SEP) if i<len(cl)-1 else "") for i,c in enumerate(cl))+r.choice(CLOSE)
    txt=typo(txt if r.random()<0.4 else txt.lower(),r)
    f=dict(game=g,gpu=gpu,laptop=lap,res=res,fps=cur,target=tgt,prefs=prefs,asks=asks)
    return txt,f
def feat_line(f):
    D=lambda v:"-" if v in (None,"",set()) else v
    return ";".join([f"g={f['game'][1].lower()}",f"gpu={f['gpu'][0].lower()}",f"lap={1 if f['laptop'] else '-'}",f"res={D(f['res'])}",
        f"fps={D(f['fps'])}",f"tgt={D(f['target'])}","pref="+(",".join(sorted(f['prefs'])) or "-"),"ask="+(",".join(sorted(f['asks'])) or "-")])
if __name__=="__main__":
    r=random.Random(4)
    for name,n,t in (("train",200000,False),("val",1000,False),("test",1000,True)):
        with open(f"data/r4_{name}.jsonl","w") as fh:
            for _ in range(n):
                x,f=sample(r,t); fh.write(json.dumps({"x":x,"y":feat_line(f),"settings":to_line(decide(f))})+"\n")
