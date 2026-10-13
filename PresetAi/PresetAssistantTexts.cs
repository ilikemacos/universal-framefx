namespace UniversalFrameFX.PresetAi;

public static class PresetAssistantTexts
{
    public static readonly string[] FrameGenLabels = ["Off", "CSR 1.3", "CSR 2.0", "FSR 3", "CSR AI"];
    public static readonly string[] PresetLabels = ["Performance", "Quality", "Balanced"];
    public static readonly string[] BackendLabels =
        ["CSR 1.3", "CSR 1.2", "Bilinear", "FSR 1", "FSR 2", "FSR 3", "FSR 4", "XeSS", "CSR 2.0", "CSR AI"];

    public static readonly Dictionary<string, string> ResLabels = new()
    {
        ["0"] = "native", ["1"] = "1440p", ["2"] = "4K", ["3"] = "720p", ["4"] = "1080p",
    };

    public const string OffTopic =
        "I only help with game presets for FrameFX. Tell me a game and your GPU (e.g. \"Cyberpunk on a 3060, want 120 fps\") and I'll build one.";

    public static readonly Dictionary<string, string> WhyText = new()
    {
        ["quality"] = "Quality preset because you asked for the best look.",
        ["headroom"] = "Quality preset: your GPU has headroom for it.",
        ["perf"] = "Performance preset (CSR 1.3 low-cost path) to keep GPU time down.",
        ["laptop"] = "Laptop GPU: assumed ~25% less performance.",
        ["anticheat"] = "WARNING: this game uses anti-cheat. Frame generation is left off; overlays can trip some anti-cheats, use at your own risk.",
        ["competitive"] = "Competitive shooter: frame generation off to keep input latency low.",
        ["latency"] = "You care about latency, so frame generation is off.",
        ["at_target"] = "You're already near your target fps, so no frame generation needed.",
        ["blur"] = "Capped at 2x frame generation because you dislike blur/ghosting.",
        ["lowbase"] = "Base fps is low (<35); generated frames will show more artifacts. Lowering in-game settings helps.",
        ["hud"] = "HUD-heavy game: watch for HUD shimmer with frame generation.",
        ["fsr4"] = "FSR 4 upscaler: you asked for max quality on an RX 9000 card.",
        ["no_fsr4"] = "FSR 4 needs an RX 9000 card, so CSR 1.3 is used instead.",
        ["asked_fsr3fg"] = "FSR 3 frame generation, as requested.",
        ["ssgi"] = "SSGI on, as requested (experimental).",
        ["rt"] = "Ray-traced lighting on, as requested (experimental, replaces SSGI).",
        ["cpu"] = "CPU boost options on, as requested.",
    };
}
