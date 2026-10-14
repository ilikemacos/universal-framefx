// Copy into the app project. Bridges the agent's IFrameFxHost to MainForm. Every call is marshalled to the UI thread.
using UniversalFrameFX.Agent.Tools;

namespace UniversalFrameFX.Agent.App;

public sealed class FrameFxHostAdapter(MainForm form) : IFrameFxHost
{
    T Ui<T>(Func<T> f) => form.InvokeRequired ? (T)form.Invoke(f) : f();

    public static ProfileSnapshot ToSnapshot(string exe, GameProfile p) =>
        new(GameProfiles.NormalizeKey(exe), p.Preset, p.FrameGen, p.FgSelect, p.FgMultiplier, p.Ssgi, p.Ssrt, p.Backend, p.Res);

    static GameProfile Merge(GameProfile? baseP, ProfileSnapshot s)
    {
        var p = GameProfiles.Clone(baseP ?? new GameProfile()); // keeps CPU/HUD-mask/etc. fields the agent doesn't touch
        p.Preset = s.Preset; p.FrameGen = s.FrameGen; p.FgSelect = s.FgSelect; p.FgMultiplier = s.FgMultiplier;
        p.Ssgi = s.Ssgi; p.Ssrt = s.Ssrt; p.Backend = s.Backend; p.Res = s.Res;
        return GameProfiles.Sanitize(p);
    }

    public FrameFxState GetState() => Ui(() =>
    {
        var b = form.CaptureAssistantBaseline();
        return new FrameFxState(form.AgentIsRunning, b.ActiveExe,
            b.Profiles.Select(kv => ToSnapshot(kv.Key, kv.Value)).ToList(), form.AgentGpuName);
    });

    public ProfileSnapshot? GetProfile(string exe) => Ui(() =>
        form.CaptureAssistantBaseline().Profiles.TryGetValue(GameProfiles.NormalizeKey(exe), out var p) ? ToSnapshot(exe, p) : null);

    public ProfileSnapshot SaveProfile(ProfileSnapshot s) => Ui(() =>
    {
        var profiles = form.CaptureAssistantBaseline().Profiles;
        profiles.TryGetValue(GameProfiles.NormalizeKey(s.Exe), out var old);
        form.ApplyAssistantProfile(s.Exe, Merge(old, s)); // enforces anti-cheat + Sanitize
        return GetProfile(s.Exe)!;
    });

    public bool IsAntiCheatGame(string exe) => AntiCheat.LookupBuiltin(exe) != null;

    // MainForm needs two small public wrappers (HANDOFF.md step 5): AgentStart(exe) and AgentStop(),
    // which do what the Start/Stop buttons do (find the game's window, StartOutput / stop).
    public (bool Ok, string Message) Start(string exe) => Ui(() => form.AgentStart(exe));
    public (bool Ok, string Message) Stop() => Ui(() => form.AgentStop());
}
