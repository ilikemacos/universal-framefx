"""gen5 = gen4 grammar + ~26 new families (Hark plan 15:32): number words, units, negation distractors,
corrections, chatter, slang, terse lists, wider value ranges, char noise. Held-out families + held-out style
(all-lowercase, no spaces) for test. Usage: python gen5.py N_train seed -> data/r5_*.jsonl"""
import random, json, sys, re
import gen4 as G
from gen4 import F, HOLD, RESN, OPEN, CLOSE, SEP, NOISE, feat_line
from kb import GAMES, GPUS
from rules import decide, to_line
F=dict(F); HOLD=dict(HOLD)
F["cur"]=F["cur"]+[["{n} fps base","base {n}fps"],["my fps is {n}","fps: {n}"],["{n}fps rn","{n} frames rn"],["only pulling {n}","pulling {n}"],["i average {n} frames","{n} frames per second"]]
F["tgt"]=F["tgt"]+[["i'd like {n}","id like {n} fps"],["{n} target","target: {n}"],["make it {n}","bump it to {n}"],["want it at {n} fps","wanna hit {n}"],["{n} would be sick","shooting for {n}"],["match my {n}hz","for my {n}hz screen"]]
F["curtgt"]=F["curtgt"]+[["{c}fps base, i'd like {t}","{c} base, {t} target"],["{c}->{t}","{c}>{t}","{c} => {t}"],["double my {c} to {t}","{c} but want {t}"]]
F["res"]=F["res"]+[["{r} res","in {r}"],["{r} 144hz panel","{r} oled"]]
F["blur"]=F["blur"]+[["zero blur","disable blur"],["turn off motion blur","no motion blur"]]
F["latency"]=F["latency"]+[["competitive feel","snappy controls"]]
F["quality"]=F["quality"]+[["make it pretty","crispy visuals"]]
F["battery"]=F["battery"]+[["running off battery","no charger"]]
for k,n in (("cur",5),("tgt",6),("curtgt",3),("res",2)): HOLD[k]=set(HOLD.get(k,set()))|{len(F[k])-1}
for k in ("blur","latency","quality","battery"): HOLD[k]=set(HOLD.get(k,set()))|{len(F[k])-1}
NEG={"blur":["dont care about blur","blur is fine","blur doesnt bother me"],"latency":["latency doesnt matter","dont care about input lag","single player so lag is fine"],
     "quality":["dont need max quality","quality isnt important","dont care how it looks"],"battery":["always plugged in","battery doesnt matter","on the charger"]}
CHAT=["first time using framefx","my friend recommended this","been tweaking for hours","new to pc gaming","potato pc lol","my rig is kinda old",
      "help me out","idk what any of the settings do","love this app","just upgraded","fans get loud","coming from console","max frames pls",
      "smooth as butter would be nice","ty in advance","be honest","i also stream sometimes","sorry for bad english"]
EMO=[" 🙏"," 😅"," 🔥"," :)"," :("," xD"," 👍"]
ONES="zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen".split()
TENS="_ _ twenty thirty forty fifty sixty seventy eighty ninety".split()
def words(n):
    if n<20: return ONES[n]
    if n<100: return TENS[n//10]+("" if n%10==0 else r_.choice(["-"," ",""])+ONES[n%10])
    h=n//100; rest=n%100
    return ONES[h]+" hundred"+("" if rest==0 else r_.choice([" "," and "])+words(rest))
r_=random.Random(0)
def pick(r,slot,test):
    fams=F[slot]; h=HOLD.get(slot,set())
    idx=[i for i in range(len(fams)) if (i in h)==test] or list(range(len(fams)))
    if test and h and r.random()<0.5: idx=sorted(h)
    return r.choice(fams[r.choice(idx)])
def charnoise(s,r):
    if r.random()<0.15: s=s.upper() if r.random()<0.2 else "".join(c.upper() if r.random()<0.1 else c for c in s)
    if r.random()<0.15:
        for _ in range(r.randint(1,3)):
            i=r.randrange(len(s)); s=(s[:i]+s[i]+s[i:]) if s[i].isalpha() else s
    if r.random()<0.1:
        for _ in range(r.randint(1,2)):
            j=[m.start() for m in re.finditer(" ",s)]
            if j: i=r.choice(j); s=s[:i]+s[i+1:]
    if r.random()<0.1: s=s+r.choice(EMO)
    return s
def typo(s,r):
    for _ in range(r.choice([0,0,0,1,2])):
        if len(s)>10:
            i=r.randrange(len(s)-1)
            if not (s[i].isdigit() or s[i+1].isdigit()): s=s[:i]+s[i+1]+s[i]+s[i+2:]
    return s
def numfmt(n,r,test):
    if n is None: return n
    return words(n) if r.random()<0.12 else str(n)
def sample(r,test=False,style_test=False):
    g=r.choice(GAMES); gpu=r.choice(GPUS); lap=r.random()<0.3; res=r.choice([None,None,"1080","1440","4k"])
    cur=r.choice([None,None,r.randint(20,110),r.choice([30,40,45,50,60,70,80,90])])
    tgt=r.choice([None,r.randint(50,240),r.choice([60,90,100,120,120,144,165,180,240]),r.choice([60,75,90,120,144])])
    if cur and tgt and tgt<=cur: tgt=None
    prefs={k for k in ("blur","latency","quality","battery") if r.random()<0.12}
    if "quality" in prefs: prefs.discard("battery")
    asks={k for k in ("fsr1","fsr2","fsr3","xess","fsr4","fsr3fg","ssgi","rt","cpu","csr12") if r.random()<0.03}
    gname=r.choice([g[0],g[0]]+g[2]); cname=r.choice([gpu[0],gpu[0].lower()]+gpu[1]+([gpu[0].replace("RTX ","rtx").replace("GTX ","gtx")] if " " in gpu[0] else []))
    if lap and r.random()<0.6: cname=r.choice(F["lap"][0]).format(c=cname)
    terse = r.random()<0.12
    if terse:  # "cs2 4090 1080p 144" style: bare values, bare trailing number = target
        parts=[gname,cname]+([r.choice(RESN[res])] if res else [])
        if cur and tgt: parts.append(r.choice([f"{cur}->{tgt}",f"{cur} to {tgt}",f"{cur}fps {tgt} target"]))
        elif tgt: parts.append(str(tgt))
        elif cur: parts.append(f"{cur}fps now")
        if lap: parts.append("laptop")
        parts+= [pick(r,k,test) for k in list(prefs)+list(asks)]
        txt=r.choice([" ",", "," | "]).join(parts)
    else:
        cl=[]; game_cl=pick(r,"game",test).format(g=gname)
        if r.random()<0.05:  # correction on game/gpu: wrong first, then "no wait"
            wg=r.choice(GAMES); game_cl=f"{r.choice([wg[0]]+wg[2])}? no wait, {gname}" if wg!=g else game_cl
        gc=pick(r,"gpu",test).format(c=cname)
        if r.random()<0.05:
            wc=r.choice(GPUS); gc=f"{wc[0]} - actually i have a {cname}" if wc!=gpu else gc
        cl.append(gc)
        if lap and "laptop" not in gc and "mobile" not in gc:
            cl.append(r.choice([x for fi,fam in enumerate(F["lap"]) if fi!=0 and ((fi in HOLD["lap"])==test) for x in fam] or F["lap"][1]))
        cs,ts=numfmt(cur,r,test),numfmt(tgt,r,test)
        if cur and tgt and r.random()<0.35: cl.append(pick(r,"curtgt",test).format(c=cs,t=ts))
        else:
            if cur:
                c0=pick(r,"cur",test).format(n=cs)
                if r.random()<0.08: c0=f"{c0}, actually more like {cs}".replace(f"{c0}, actually more like",f"getting {r.randint(20,110)}, actually more like",1)
                cl.append(c0)
            if tgt:
                t0=pick(r,"tgt",test).format(n=ts)
                if r.random()<0.08: t0=f"want {r.randint(50,240)}, no wait {ts}"
                cl.append(t0)
        if res: cl.append(pick(r,"res",test).format(r=r.choice(RESN[res])))
        for k in list(prefs)+list(asks): cl.append(pick(r,k,test))
        for k in ("blur","latency","quality","battery"):
            if k not in prefs and r.random()<0.04: cl.append(r.choice(NEG[k]))
        if r.random()<0.15: cl.append(r.choice(NOISE).strip())
        if r.random()<0.2: cl.append(r.choice(CHAT))
        r.shuffle(cl); cl.insert(0 if r.random()<0.7 else r.randint(0,len(cl)),game_cl)
        txt=r.choice(OPEN)+"".join(c+(r.choice(SEP) if i<len(cl)-1 else "") for i,c in enumerate(cl))+r.choice(CLOSE)
    txt=typo(txt if r.random()<0.4 else txt.lower(),r)
    txt=charnoise(txt,r)
    if style_test: txt=txt.lower().replace(" ","")
    return txt,dict(game=g,gpu=gpu,laptop=lap,res=res,fps=cur,target=tgt,prefs=prefs,asks=asks)
def row(x,f): return json.dumps({"x":x,"y":feat_line(f),"settings":to_line(decide(f))})
if __name__=="__main__":
    n=int(sys.argv[1]); seed=int(sys.argv[2]); r=random.Random(seed); r_.seed(seed)
    if n==0:
        with open("data/r5_val.jsonl","w") as fh: [fh.write(row(*sample(r))+"\n") for _ in range(1000)]
        with open("data/r5_test.jsonl","w") as fh: [fh.write(row(*sample(r,True))+"\n") for _ in range(1000)]
        with open("data/r5_style.jsonl","w") as fh: [fh.write(row(*sample(r,False,True))+"\n") for _ in range(500)]
    else:
        with open(f"data/r5_train_{seed}.jsonl","w") as fh: [fh.write(row(*sample(r))+"\n") for _ in range(n)]
