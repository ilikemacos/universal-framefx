"""Export r3 and g14 GPT checkpoints to ONNX for FrameFX Preset Assistant."""
import argparse
import os
import sys

import torch
import torch.nn as nn

from model import GPT as GPT_r3


def export_gpt(m: nn.Module, path: str, opset: int = 17):
    m.eval()
    dummy = torch.zeros(1, 8, dtype=torch.long)
    torch.onnx.export(
        m,
        dummy,
        path,
        input_names=["input_ids"],
        output_names=["logits"],
        dynamic_axes={"input_ids": {0: "batch", 1: "seq"}, "logits": {0: "batch", 1: "seq"}},
        opset_version=opset,
        do_constant_folding=True,
    )
    print("wrote", path, os.path.getsize(path))


def load_r3(path: str):
    m = GPT_r3()
    m.load_state_dict(torch.load(path, map_location="cpu", weights_only=True))
    return m


def load_g14(path: str):
    from model_g14 import GPT

    m = GPT(p=0.0)
    m.load_state_dict(torch.load(path, map_location="cpu", weights_only=True))
    return m


def parity(m, prompts, tag):
    from assist import load_model

    ref = load_model.__wrapped__ if hasattr(load_model, "__wrapped__") else None
    bad = 0
    for p in prompts:
        a = m.gen(p)
        bad += 0  # self-consistency only here; C# tests use saved golden lines
        print(f"[{tag}]", repr(p[:50]), "->", repr(a[:80]))
    return bad


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="../PresetAi/Models")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    prompts = [
        "I want Cyberpunk 2077, getting about 45 fps and want 120",
        "actually I have a 4070",
        "what about 4K",
        "switch to Valorant",
    ]
    for name, loader, ckpt in (
        ("preset_r3", load_r3, "ckpt_r3.pt"),
        ("preset_g14", load_g14, "ckpt_g14.pt"),
    ):
        m = loader(ckpt)
        out = os.path.join(args.out, f"{name}.onnx")
        export_gpt(m, out)
        for p in prompts:
            print(name, repr(m.gen(p)))
