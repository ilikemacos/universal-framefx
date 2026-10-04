using System.Reflection;
using System.Text.RegularExpressions;

namespace UniversalFrameFX;

/// <summary>One place for the version. Internal semver from the csproj (e.g. 1.4.0-beta.3), shown as "v1.4.0 Beta3".</summary>
public static class AppVersion
{
    public static readonly string Version = (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.4.0-beta.3").Split('+')[0];
    public static string Display => Pretty(Version);
    public static string Pretty(string v)
    {
        var m = Regex.Match(v, @"^(\d+(?:\.\d+)*)-(alpha|beta|rc)\.?(\d*)$", RegexOptions.IgnoreCase);
        if (!m.Success) return "v" + v;
        string t = m.Groups[2].Value.ToLowerInvariant();
        string tag = t == "rc" ? "RC" : char.ToUpperInvariant(t[0]) + t[1..];
        return $"v{m.Groups[1].Value} {tag}{m.Groups[3].Value}";
    }
}

/// <summary>Beta2: hints for a source frame rate pinned by a platform limiter.</summary>
public static class FpsLock
{
    /// <summary>A source pinned at ~40 / ~30 fps (tiny variance) is a limiter, not a slow GPU. Pure function (unit-tested).</summary>
    public static string Hint(double meanFps, double sdFps, bool onBattery, double frameFxGpuMs)
    {
        if (sdFps > 1.2) return "";
        if (meanFps >= 37.5 && meanFps <= 41.5)
            return "game fps is locked at ~40 with headroom left — this matches ASUS Silent mode / NVIDIA Whisper Mode (or a 40 fps limit). " +
                   "Switch Armoury Crate to Turbo/Performance (Fn+F5) and turn Whisper Mode off in the NVIDIA App.";
        if (meanFps >= 28.5 && meanFps <= 31.5)
            return onBattery ? "game fps is locked at ~30 on battery — NVIDIA Battery Boost caps games at 30. Plug in the charger or raise the Battery Boost limit."
                             : "game fps is locked at ~30 — check the game's own limiter / background-fps setting, V-Sync half-rate, or an NVIDIA Max Frame Rate.";
        return "";
    }

}
