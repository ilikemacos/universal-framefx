using System.Text.Json;

namespace UniversalFrameFX;

/// <summary>Per-game settings remembered by the app shell (preset, frame generation, SSGI, upscaler, output resolution).
/// No windowing types: the decisions are pure so they can be unit-tested.</summary>
public sealed class GameProfile
{
    /// <summary>0 Performance, 1 Quality, 2 Competitive.</summary>
    public int Preset { get; set; }
    public bool FrameGen { get; set; }
    /// <summary>Frame-generation selector (FgSelect: 0 Off / 1 Csr13 / 2 Csr20 / 3 Fsr3 / 4 CsrAi). -1 = not saved by this app version yet.</summary>
    public int FgSelect { get; set; } = -1;
    /// <summary>2, 3, 4 or 8 (CSR 2.0 also 5, 6, 10, 20). Anything else is stored as 4.</summary>
    public int FgMultiplier { get; set; } = 4;
    public bool Ssgi { get; set; }
    /// <summary>0 Auto, 1 GTX 1050 Ti, 2 GTX 980 Ti.</summary>
    public int SsgiPreset { get; set; }
    /// <summary>Steadier lighting while moving, when SSGI is on.</summary>
    public bool SsgiTemporal { get; set; } = global::UniversalFrameFX.Ssgi.TemporalDefault;
    /// <summary>Experimental lighting. Off unless the user turns it on. Replaces SSGI while it is on.</summary>
    public bool Ssrt { get; set; }
    /// <summary>0 Auto, 1 GTX 1050 Ti, 2 GTX 980 Ti.</summary>
    public int SsrtPreset { get; set; }
    /// <summary>Steadier picture while moving, when ray-traced lighting is on. Default comes from the seed table.</summary>
    public bool SsrtTemporal { get; set; } = global::UniversalFrameFX.Ssrt.TemporalDefault;
    /// <summary><see cref="Backend"/> value.</summary>
    public int Backend { get; set; }
    /// <summary><see cref="OutputRes"/> value.</summary>
    public int Res { get; set; }
    /// <summary>Per-game CPU options. Missing on older profiles, which means everything off.</summary>
    public CpuBoostSettings Cpu { get; set; } = new();
    public string? FgV2HudMask { get; set; }
    public int FgV2HudMaskW { get; set; } = 64;
    public int FgV2HudMaskH { get; set; } = 36;
}

/// <summary>Key normalisation, summary text, and when a saved profile replaces the global settings.</summary>
public static class GameProfiles
{
    public static string NormalizeKey(string? exeOrPath)
    {
        if (string.IsNullOrWhiteSpace(exeOrPath)) return "";
        string s = exeOrPath.Trim().Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.ToLowerInvariant();
        if (s.Length == 0) return "";
        if (!s.EndsWith(".exe", StringComparison.Ordinal)) s += ".exe";
        return s;
    }

    /// <summary>File name as the user sees it (original spelling, no directory).</summary>
    public static string DisplayName(string? exeOrPath)
    {
        if (string.IsNullOrWhiteSpace(exeOrPath)) return "";
        string s = exeOrPath.Trim().Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    public static string PresetName(int preset) => preset switch
    {
        1 => "Quality",
        2 => "Competitive",
        _ => "Performance",
    };

    public static int Mul(int n, int fgSelect = -1)
    {
        if (n is 0 or 2 or 3 or 4 or 8) return n;
        if (fgSelect is (int)UniversalFrameFX.FgSelect.Csr20 or (int)UniversalFrameFX.FgSelect.CsrAi && n is 5 or 6 or 10 or 20) return n;
        return 4;
    }

    /// <summary>Short list line, e.g. "Competitive · FG 8× · SSGI off". Competitive always runs 8× frame generation.</summary>
    public static string Summary(GameProfile p)
    {
        p = Sanitize(p);
        string ssgi = p.Ssgi ? "SSGI on" : "SSGI off";
        string rt = p.Ssrt ? " · RT on" : "";
        string cpu = CpuBoost.AnyEnabled(p.Cpu) ? " · CPU boost" : "";
        if (p.Preset == 2) return $"Competitive · FG 8× · {ssgi}{rt}{cpu}";
        string fg = p.FrameGen ? (p.FgMultiplier == 0 ? "FG Auto" : $"FG {Mul(p.FgMultiplier)}×") : "FG off";
        return $"{PresetName(p.Preset)} · {fg} · {ssgi}{rt}{cpu}";
    }

    public static string AppliedStatus(string exe) => $"Profile: {DisplayName(exe)} (saved settings applied)";

    public static GameProfile Sanitize(GameProfile? p)
    {
        p ??= new GameProfile();
        int backend = Enum.IsDefined(typeof(Backend), p.Backend) ? p.Backend : 0;
        int res = p.Res is >= 0 and <= 4 ? p.Res : 0;
        int fgSel = p.FgSelect is >= 0 and <= 4 ? p.FgSelect : (p.FrameGen ? (int)UniversalFrameFX.FgSelect.Csr13 : (int)UniversalFrameFX.FgSelect.Off);
        return new GameProfile
        {
            Preset = p.Preset is 1 or 2 ? p.Preset : 0,
            FrameGen = fgSel != (int)UniversalFrameFX.FgSelect.Off,
            FgSelect = fgSel,
            FgMultiplier = Mul(p.FgMultiplier, fgSel),
            Ssgi = p.Ssgi,
            SsgiPreset = p.SsgiPreset is 1 or 2 ? p.SsgiPreset : 0,
            SsgiTemporal = p.SsgiTemporal,
            Ssrt = p.Ssrt,
            SsrtPreset = p.SsrtPreset is 1 or 2 ? p.SsrtPreset : 0,
            SsrtTemporal = p.SsrtTemporal,
            Backend = backend,
            Res = res,
            Cpu = CpuBoost.Sanitize(p.Cpu),
            FgV2HudMask = p.FgV2HudMask,
            FgV2HudMaskW = p.FgV2HudMaskW > 0 ? p.FgV2HudMaskW : 64,
            FgV2HudMaskH = p.FgV2HudMaskH > 0 ? p.FgV2HudMaskH : 36,
        };
    }

    public static GameProfile Clone(GameProfile p) => Sanitize(p);

    public static Dictionary<string, GameProfile> NormalizeMap(Dictionary<string, GameProfile>? raw)
    {
        var d = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        if (raw == null) return d;
        foreach (var kv in raw)
        {
            string k = NormalizeKey(kv.Key);
            if (k.Length == 0 || kv.Value == null) continue;
            d[k] = Sanitize(kv.Value);
        }
        return d;
    }

    public static bool Has(IReadOnlyDictionary<string, GameProfile>? profiles, string? exe)
    {
        string k = NormalizeKey(exe);
        return k.Length > 0 && profiles != null && profiles.ContainsKey(k);
    }

    /// <summary>Saved profile for this exe, otherwise a copy of <paramref name="global"/>.</summary>
    public static (GameProfile settings, bool fromProfile) Select(IReadOnlyDictionary<string, GameProfile>? profiles, string? exe, GameProfile global)
    {
        string k = NormalizeKey(exe);
        if (k.Length > 0 && profiles != null && profiles.TryGetValue(k, out var p) && p != null)
            return (Sanitize(p), true);
        return (Sanitize(global), false);
    }

    public static void Remember(Dictionary<string, GameProfile> profiles, string exe, GameProfile snapshot)
    {
        string k = NormalizeKey(exe);
        if (k.Length == 0) return;
        profiles[k] = Sanitize(snapshot);
    }

    public static bool Reset(Dictionary<string, GameProfile> profiles, string exe)
    {
        string k = NormalizeKey(exe);
        return k.Length > 0 && profiles.Remove(k);
    }

    public static void ResetAll(Dictionary<string, GameProfile> profiles) => profiles.Clear();

    /// <summary>Explicit user edits are stored. Automatic changes (latency budget, safeguards, compare) and
    /// test/automation modes pass false.</summary>
    public static bool ShouldRemember(bool userEdit, bool testMode, string? activeExe) =>
        userEdit && !testMode && NormalizeKey(activeExe).Length > 0;

    public static string ToJson(Dictionary<string, GameProfile> profiles) =>
        JsonSerializer.Serialize(NormalizeMap(profiles), new JsonSerializerOptions { WriteIndented = true });

    public static Dictionary<string, GameProfile> FromJson(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, GameProfile>>(json);
            return NormalizeMap(raw);
        }
        catch { return new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase); }
    }
}
