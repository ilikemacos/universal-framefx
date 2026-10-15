namespace UniversalFrameFX.PresetAi;

/// <summary>Rule-engine output: preset slots plus comma-separated why codes.</summary>
public sealed class SlotDecision
{
    public static readonly string[] SlotKeys = ["exe", "p", "fg", "m", "b", "r", "gi", "rt", "cpu", "why"];

    public string Exe { get; set; } = "?";
    public string P { get; set; } = "0";
    public string Fg { get; set; } = "0";
    public string M { get; set; } = "0";
    public string B { get; set; } = "0";
    public string R { get; set; } = "0";
    public string Gi { get; set; } = "0";
    public string Rt { get; set; } = "0";
    public string Cpu { get; set; } = "0";
    public string Why { get; set; } = "";

    public int BaseFps { get; set; }
    public int TargetFps { get; set; }

    public SlotDecision Clone() => new()
    {
        Exe = Exe, P = P, Fg = Fg, M = M, B = B, R = R, Gi = Gi, Rt = Rt, Cpu = Cpu, Why = Why,
        BaseFps = BaseFps, TargetFps = TargetFps,
    };

}
