#!/usr/bin/env python3
"""Write ONNX greedy-decode golden lines for C# parity tests."""
import json
import pathlib

from assist import load_model

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parent / "PresetAi.Tests" / "Assets" / "onnx_golden.json"

PROMPTS = [
    "I want Cyberpunk 2077, getting about 45 fps and want 120",
    "actually I have a 4070",
    "what about 4K",
    "I hate blur and ghosting",
    "switch to Valorant",
    "Elden Ring on RTX 3060 laptop, 55 fps, want 120, use fsr 3 frame gen",
    "minecraft on a 1050 ti, chill",
]


def main():
    rows = []
    for tag, ckpt in (("r3", "ckpt_r3.pt"), ("g14", "ckpt_g14.pt")):
        m = load_model(ckpt)
        for p in PROMPTS:
            rows.append({"model": tag, "prompt": p, "line": m.gen(p)})
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(rows, indent=2))
    print("wrote", OUT, len(rows), "rows")


if __name__ == "__main__":
    main()
