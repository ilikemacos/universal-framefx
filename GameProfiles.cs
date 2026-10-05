using System.Text.Json;

namespace UniversalFrameFX;

public sealed class GameProfile
{
    public int Preset { get; set; }
    public bool FrameGen { get; set; }
    public int FgMultiplier { get; set; } = 4;
    public bool Ssgi { get; set; }
    public int SsgiPreset { get; set; }
    public bool SsgiTemporal { get; set; } = global::UniversalFrameFX.Ssgi.TemporalDefault;
    public bool Ssrt { get; set; }
    public int SsrtPreset { get; set; }
    public bool SsrtTemporal { get; set; } = global::UniversalFrameFX.Ssrt.TemporalDefault;
    public int Backend { get; set; }
    public int Res { get; set; }
    public CpuBoostSettings Cpu { get; set; } = new();
}

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

    public static int Mul(int n) => n is 2 or 3 or 4 or 8 ? n : 4;

    public static string Summary(GameProfile p)
    {
        p = Sanitize(p);
        string ssgi = p.Ssgi ? "SSGI on" : "SSGI off";
        string rt = p.Ssrt ? " · RT on" : "";
        string cpu = CpuBoost.AnyEnabled(p.Cpu) ? " · CPU boost" : "";
        if (p.Preset == 2) return $"Competitive · FG 8× · {ssgi}{rt}{cpu}";
        string fg = p.FrameGen ? $"FG {Mul(p.FgMultiplier)}×" : "FG off";
        return $"{PresetName(p.Preset)} · {fg} · {ssgi}{rt}{cpu}";
    }

    public static string AppliedStatus(string exe) => $"Profile: {DisplayName(exe)} (saved settings applied)";

    public static GameProfile Sanitize(GameProfile? p)
    {
        p ??= new GameProfile();
        int backend = Enum.IsDefined(typeof(Backend), p.Backend) ? p.Backend : 0;
        int res = p.Res is >= 0 and <= 4 ? p.Res : 0;
        return new GameProfile
        {
            Preset = p.Preset is 1 or 2 ? p.Preset : 0,
            FrameGen = p.FrameGen,
            FgMultiplier = Mul(p.FgMultiplier),
            Ssgi = p.Ssgi,
            SsgiPreset = p.SsgiPreset is 1 or 2 ? p.SsgiPreset : 0,
            SsgiTemporal = p.SsgiTemporal,
            Ssrt = p.Ssrt,
            SsrtPreset = p.SsrtPreset is 1 or 2 ? p.SsrtPreset : 0,
            SsrtTemporal = p.SsrtTemporal,
            Backend = backend,
            Res = res,
            Cpu = CpuBoost.Sanitize(p.Cpu),
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
