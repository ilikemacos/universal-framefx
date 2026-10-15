using UniversalFrameFX.PresetAi;

namespace UniversalFrameFX;

public static class PresetAssistantMapper
{
    public static GameProfile ToGameProfile(SlotDecision d) => GameProfiles.Sanitize(new GameProfile
    {
        Preset = int.Parse(d.P) is 1 or 2 ? int.Parse(d.P) : 0,
        FrameGen = d.Fg != "0",
        FgSelect = Math.Clamp(int.Parse(d.Fg), 0, 4),
        FgMultiplier = d.Fg != "0" ? GameProfiles.Mul(int.Parse(d.M), int.Parse(d.Fg)) : 4,
        Ssgi = d.Gi == "1",
        SsgiPreset = 0,
        SsgiTemporal = false,
        Ssrt = d.Rt == "1",
        SsrtPreset = 0,
        SsrtTemporal = Ssrt.TemporalDefault,
        Backend = int.Parse(d.B),
        Res = int.Parse(d.R),
        Cpu = CpuBoost.FromAssistant(d.Cpu == "1"),
        FgV2HudMask = null,
        FgV2HudMaskW = 64,
        FgV2HudMaskH = 36,
    });

    public static string AntiCheatNote(string? exe)
    {
        var hit = AntiCheat.LookupBuiltin(exe);
        return hit != null
            ? PresetAssistantTexts.WhyText["anticheat"]
            : "";
    }
}
