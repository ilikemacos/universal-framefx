# Same GPT as model.py (same parameter names, so a d128/L5 checkpoint loads into model.GPT), device-aware + dropout.
import torch, torch.nn as nn, torch.nn.functional as F
from model import enc, dec, STOI, V
class Block(nn.Module):
    def __init__(s,d,h,p):
        super().__init__(); s.h=h; s.p=p; s.l1=nn.LayerNorm(d); s.att=nn.MultiheadAttention(d,h,dropout=p,batch_first=True); s.l2=nn.LayerNorm(d)
        s.ff=nn.Sequential(nn.Linear(d,4*d),nn.GELU(),nn.Linear(4*d,d)); s.dp=nn.Dropout(p)
    def forward(s,x,mask):
        B,T,C=x.shape; a=s.att; q,k,v=F.linear(s.l1(x),a.in_proj_weight,a.in_proj_bias).split(C,2)
        q,k,v=[t.view(B,T,s.h,C//s.h).transpose(1,2) for t in (q,k,v)]
        y=F.scaled_dot_product_attention(q,k,v,is_causal=True,dropout_p=s.p if s.training else 0.0).transpose(1,2).reshape(B,T,C)
        x=x+s.dp(a.out_proj(y)); return x+s.dp(s.ff(s.l2(x)))
class GPT(nn.Module):
    def __init__(s,d=128,L=5,h=4,T=448,p=0.0):
        super().__init__(); s.T=T; s.tok=nn.Embedding(V,d); s.pos=nn.Embedding(T,d)
        s.blocks=nn.ModuleList([Block(d,h,p) for _ in range(L)]); s.ln=nn.LayerNorm(d); s.head=nn.Linear(d,V,bias=False); s.head.weight=s.tok.weight
    def forward(s,idx):
        B,T=idx.shape; x=s.tok(idx)+s.pos(torch.arange(T,device=idx.device))
        mask=None
        for b in s.blocks: x=b(x,mask)
        return s.head(s.ln(x))
    @torch.no_grad()
    def gen(s,prompt,maxn=160):
        dev=s.tok.weight.device; ids=enc(prompt[-300:]+"\t")
        for _ in range(maxn):
            lo=s(torch.tensor([ids[-s.T:]],device=dev))[0,-1]; n=int(lo.argmax())
            if n==STOI["\n"]: break
            ids.append(n)
        return dec(ids).split("\t",1)[1]

    @torch.no_grad()
    def gen_batch(s,prompts,maxn=120):
        dev=s.tok.weight.device; seqs=[enc(p[-300:]+"\t") for p in prompts]; st=[len(q) for q in seqs]; done=[False]*len(seqs)
        for _ in range(maxn):
            act=[i for i in range(len(seqs)) if not done[i]]
            if not act: break
            Lm=max(len(seqs[i]) for i in act); X=torch.zeros(len(act),Lm,dtype=torch.long)
            for j,i in enumerate(act): X[j,:len(seqs[i])]=torch.tensor(seqs[i])
            with torch.autocast("cuda",dtype=torch.bfloat16): lo=s(X.to(dev))
            idx=torch.tensor([len(seqs[i])-1 for i in act],device=dev); nx=lo[torch.arange(len(act),device=dev),idx].argmax(-1).tolist()
            for j,i in enumerate(act):
                if nx[j]==STOI["\n"] or len(seqs[i])>=s.T: done[i]=True
                else: seqs[i].append(nx[j])
        return [dec(q[st[i]:]) for i,q in enumerate(seqs)]
