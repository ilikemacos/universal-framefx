namespace UniversalFrameFX.PresetAi;

public sealed class SessionFeatures
{
    public GameEntry? Game { get; set; }
    public GpuEntry? Gpu { get; set; }
    public bool Laptop { get; set; }
    public string? Res { get; set; }
    public int? Fps { get; set; }
    public int? Target { get; set; }
    public HashSet<string> Prefs { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Asks { get; } = new(StringComparer.Ordinal);

    public SessionFeatures Clone()
    {
        var c = new SessionFeatures
        {
            Game = Game,
            Gpu = Gpu,
            Laptop = Laptop,
            Res = Res,
            Fps = Fps,
            Target = Target,
        };
        c.Prefs.UnionWith(Prefs);
        c.Asks.UnionWith(Asks);
        return c;
    }
}
