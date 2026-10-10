namespace UniversalFrameFX;

public sealed record AcCatalogEntry(string Exe, string Game, string Engine);

public static class AntiCheatCatalog
{
    public static readonly AcCatalogEntry[] Builtin =
    {
        new("FortniteClient-Win64-Shipping.exe", "Fortnite", "EasyAntiCheat (EOS)"),
        new("VALORANT-Win64-Shipping.exe", "VALORANT", "Riot Vanguard"),
        new("TslGame.exe", "PUBG: BATTLEGROUNDS", "BattlEye"),
        new("RainbowSix.exe", "Rainbow Six Siege", "BattlEye"),
        new("DayZ_x64.exe", "DayZ", "BattlEye"),
        new("arma3_x64.exe", "Arma 3", "BattlEye"),
        new("EscapeFromTarkov.exe", "Escape from Tarkov", "BattlEye"),
        new("r5apex.exe", "Apex Legends", "EasyAntiCheat"),
        new("RustClient.exe", "Rust", "EasyAntiCheat"),
        new("eldenring.exe", "Elden Ring", "EasyAntiCheat"),
        new("DeadByDaylight-Win64-Shipping.exe", "Dead by Daylight", "EasyAntiCheat"),
        new("Finals.exe", "The Finals", "EasyAntiCheat"),
        new("Discovery.exe", "The Finals", "EasyAntiCheat"),
        new("FortniteClient-Win64-Shipping_EAC_EOS.exe", "Fortnite", "EasyAntiCheat (EOS)"),
        new("FortniteClient-Win64-Shipping_BE.exe", "Fortnite", "BattlEye"),
        new("bf2042.exe", "Battlefield 2042", "EA Javelin"),
        new("bf6.exe", "Battlefield 6", "EA Javelin"),
        new("HuntGame.exe", "Hunt: Showdown 1896", "EasyAntiCheat"),
        new("FallGuys_client_game.exe", "Fall Guys", "EasyAntiCheat"),
        new("Warframe.x64.exe", "Warframe", "EasyAntiCheat"),
        new("ZenlessZoneZero.exe", "Zenless Zone Zero", "HoYoKProtect"),
        new("StarRail.exe", "Honkai: Star Rail", "HoYoKProtect"),
        new("cs2.exe", "Counter-Strike 2", "VAC"),
        new("cod.exe", "Call of Duty", "Ricochet"),
        new("ModernWarfare.exe", "Call of Duty", "Ricochet"),
        new("BlackOpsColdWar.exe", "Call of Duty", "Ricochet"),
        new("Vanguard.exe", "Call of Duty", "Ricochet"),
        new("destiny2.exe", "Destiny 2", "BattlEye"),
        new("LeagueClientUx.exe", "League of Legends", "Riot Vanguard"),
        new("League of Legends.exe", "League of Legends", "Riot Vanguard"),
        new("Overwatch.exe", "Overwatch 2", "Blizzard anti-cheat"),
        new("Marvel-Win64-Shipping.exe", "Marvel Rivals", "EasyAntiCheat"),
        new("RobloxPlayerBeta.exe", "Roblox", "Hyperion/Byfron"),
        new("GenshinImpact.exe", "Genshin Impact", "mhyprot"),
        new("YuanShen.exe", "Genshin Impact", "mhyprot"),
    };

    public static readonly HashSet<string> PerGameServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "EasyAntiCheat.exe", "EasyAntiCheat_launcher.exe", "EasyAntiCheat_EOS.exe",
        "BEService.exe", "BEService_x64.exe", "BEDaisy.exe", "GameMon.des", "GameMon64.des", "GameGuard.des",
        "EAAntiCheat.GameService.exe", "EAAntiCheat.GameServiceLauncher.exe", "BlackCipher64.aes", "xhunter1.exe",
    };

    public static readonly HashSet<string> AlwaysOnServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc.exe", "vgtray.exe", "FACEIT.exe", "FACEITService.exe", "faceit-ac.exe", "mhyprot.exe",
    };

    public static readonly string[] FolderMarkers = { "EasyAntiCheat", "BattlEye" };
    public static readonly string[] FileMarkers = { "start_protected_game.exe" };
    public static readonly string[] FileSuffixMarkers = { "_be.exe", "_eac.exe" };
}

public static class AntiCheat
{
    public readonly record struct Signals(bool BuiltinExeMatch, string BuiltinEngine, bool FolderMarkers, bool ServiceProcessRunning);

    static readonly Dictionary<string, AcCatalogEntry> ByKey = BuildByKey();

    static Dictionary<string, AcCatalogEntry> BuildByKey()
    {
        var map = new Dictionary<string, AcCatalogEntry>(StringComparer.Ordinal);
        foreach (var e in AntiCheatCatalog.Builtin)
        {
            string k = GameProfiles.NormalizeKey(e.Exe);
            if (k.Length > 0) map[k] = e;
        }
        return map;
    }

    public static AcCatalogEntry? LookupBuiltin(string? exe)
    {
        string k = GameProfiles.NormalizeKey(exe);
        if (k.Length == 0) return null;
        return ByKey.TryGetValue(k, out var e) ? e : null;
    }

    public static Signals ComputeSignals(string? exe, bool folderMarkers, IEnumerable<string>? runningProcessNamesLower)
    {
        var hit = LookupBuiltin(exe);
        bool service = false;
        if (runningProcessNamesLower != null)
        {
            foreach (var n in runningProcessNamesLower)
            {
                if (IsPerGameService(n)) { service = true; break; }
            }
        }
        return new Signals(hit != null, hit?.Engine ?? "", folderMarkers, service);
    }

    public static string Decide(string? exe, Signals signals, IReadOnlyDictionary<string, bool>? overrides)
    {
        string key = GameProfiles.NormalizeKey(exe);
        if (overrides != null && key.Length > 0 && TryOverride(overrides, key, out bool forced))
        {
            if (!forced) return "";
            return signals.BuiltinEngine.Length > 0 ? signals.BuiltinEngine : "Custom (user-added)";
        }
        if (signals.BuiltinExeMatch) return signals.BuiltinEngine;
        if (signals.FolderMarkers) return "Anti-cheat (folder markers)";
        if (signals.ServiceProcessRunning) return "Anti-cheat (running service)";
        return "";
    }

    static bool TryOverride(IReadOnlyDictionary<string, bool> overrides, string key, out bool value)
    {
        if (overrides.TryGetValue(key, out value)) return true;
        foreach (var kv in overrides)
        {
            if (string.Equals(GameProfiles.NormalizeKey(kv.Key), key, StringComparison.Ordinal))
            {
                value = kv.Value;
                return true;
            }
        }
        value = false;
        return false;
    }

    public static bool IsServiceProcess(string? name) =>
        InSet(name, AntiCheatCatalog.PerGameServiceNames) || InSet(name, AntiCheatCatalog.AlwaysOnServiceNames);

    public static bool IsPerGameService(string? name) => InSet(name, AntiCheatCatalog.PerGameServiceNames);

    static bool InSet(string? name, IEnumerable<string> set)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string a = ServiceKey(name);
        if (a.Length == 0) return false;
        foreach (var s in set)
            if (string.Equals(a, ServiceKey(s), StringComparison.Ordinal)) return true;
        return false;
    }

    static string ServiceKey(string name)
    {
        string s = name.Trim().Trim('"').Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.ToLowerInvariant();
        if (s.EndsWith(".exe", StringComparison.Ordinal)) s = s[..^4];
        return s.Trim();
    }
}
