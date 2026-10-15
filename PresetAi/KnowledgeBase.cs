namespace UniversalFrameFX.PresetAi;

public readonly record struct GameEntry(
    string Name,
    string Exe,
    string[] Aliases,
    int Cost,
    bool HudHeavy,
    bool Competitive,
    bool AntiCheat);

public readonly record struct GpuEntry(string Name, string[] Aliases, int Score, string Vendor, string Series);

public static partial class KnowledgeBase
{
    static readonly Dictionary<string, GameEntry> GameByExe;
    static readonly Dictionary<string, GameEntry> GameByAlias;
    static readonly Dictionary<string, GpuEntry> GpuByAlias;

    static KnowledgeBase()
    {
        GameByExe = new(StringComparer.OrdinalIgnoreCase);
        GameByAlias = new(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Games)
        {
            GameByExe[NormalizeExe(g.Exe)] = g;
            GameByAlias[g.Name.ToLowerInvariant()] = g;
            foreach (var a in g.Aliases)
                GameByAlias[a.ToLowerInvariant()] = g;
            var stem = g.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g.Exe[..^4] : g.Exe;
            GameByAlias[stem.ToLowerInvariant()] = g;
        }

        GpuByAlias = new(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Gpus)
        {
            GpuByAlias[g.Name.ToLowerInvariant()] = g;
            foreach (var a in g.Aliases)
                GpuByAlias[a.ToLowerInvariant()] = g;
        }
    }

    static string NormalizeExe(string exe) => exe.ToLowerInvariant();

    public static GameEntry? FindGame(string? exeLower) =>
        exeLower != null && GameByExe.TryGetValue(exeLower, out var g) ? g : null;

    public static GameEntry? FindGameByAlias(string alias)
    {
        alias = alias.Trim().ToLowerInvariant();
        return GameByAlias.TryGetValue(alias, out var g) ? g : null;
    }

    public static GpuEntry? FindGpuByAlias(string alias)
    {
        alias = alias.Trim().ToLowerInvariant();
        return GpuByAlias.TryGetValue(alias, out var g) ? g : null;
    }

    public static GpuEntry DefaultGpu => Array.Find(Gpus, g => g.Name == "RTX 3060");
}
