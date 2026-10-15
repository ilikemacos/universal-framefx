import torch, torch.nn as nn, torch.nn.functional as F
CH = [chr(i) for i in range(32,127)]+["\t","\n"]
STOI = {c:i+1 for i,c in enumerate(CH)}; ITOS={i:c for c,i in STOI.items()}; V=len(CH)+1
def enc(s): return [STOI.get(c, STOI["?"]) for c in s]
def dec(ids): return "".join(ITOS.get(i,"") for i in ids)
class Block(nn.Module):
    def __init__(s,d,h):
        super().__init__(); s.l1=nn.LayerNorm(d); s.att=nn.MultiheadAttention(d,h,batch_first=True); s.l2=nn.LayerNorm(d)
        s.ff=nn.Sequential(nn.Linear(d,4*d),nn.GELU(),nn.Linear(4*d,d))
    def forward(s,x,mask):
        h=s.l1(x); x=x+s.att(h,h,h,attn_mask=mask,need_weights=False)[0]; return x+s.ff(s.l2(x))
class GPT(nn.Module):
    def __init__(s,d=128,L=5,h=4,T=448):
        super().__init__(); s.T=T; s.tok=nn.Embedding(V,d); s.pos=nn.Embedding(T,d)
        s.blocks=nn.ModuleList([Block(d,h) for _ in range(L)]); s.ln=nn.LayerNorm(d); s.head=nn.Linear(d,V,bias=False)
        s.head.weight=s.tok.weight
    def forward(s,idx):
        B,T=idx.shape; x=s.tok(idx)+s.pos(torch.arange(T))
        mask=torch.triu(torch.ones(T,T,dtype=torch.bool),1)
        for b in s.blocks: x=b(x,mask)
        return s.head(s.ln(x))
    @torch.no_grad()
    def gen(s,prompt,maxn=160):
        ids=enc(prompt[-300:]+"\t")
        for _ in range(maxn):
            lo=s(torch.tensor([ids[-s.T:]]))[0,-1]; n=int(lo.argmax())
            if n==STOI["\n"]: break
            ids.append(n)
        return dec(ids).split("\t",1)[1]
