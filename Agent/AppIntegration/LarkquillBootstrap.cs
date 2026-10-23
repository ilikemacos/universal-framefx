// Copy into the app project (1.4.1-beta.11). Opens the LarkquillAI panel. Needs the beta.8+ app (Preset Assistant + anti-cheat APIs).
// Wire it like WrenbyAI was: a "LarkquillAI" button that calls LarkquillBootstrap.Open(this). Nothing to shut down (no local server).
using UniversalFrameFX.Agent.Cloud;
using UniversalFrameFX.Agent.UI;
using UniversalFrameFX.Agent;

namespace UniversalFrameFX.Agent.App;

public static class LarkquillBootstrap
{
    static LarkquillPanel? _open;

    public static void Open(MainForm form)
    {
        if (!LarkquillPermission.EnsureAllowed(form)) return;
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        var keys = new DpapiKeyStore();
        var paths = AgentPaths.Default();
        var settings = LarkquillSettings.Load(paths.Root);
        LarkquillPanel? panel = null;
        // Confirm dialogs are owned by the panel; every change still goes through MainForm.ApplyAssistantProfile (Sanitize + anti-cheat).
        var prompts = new DeferredPrompts(() => panel!);
        var session = new LarkquillSession(prompts, new FrameFxHostAdapter(form), new PresetAiEngine(), keys.Load);
        panel = new LarkquillPanel(session, keys, settings.ModelId, id => { settings.ModelId = id; settings.Save(paths.Root); });
        panel.FormClosed += (_, _) => _open = null;
        _open = panel;
        panel.Show(form);
    }

    sealed class DeferredPrompts(Func<Control> owner) : IConfirmer
    {
        public Task<bool> ConfirmAsync(ConfirmRequest r, CancellationToken ct) => new WinFormsPrompts(owner()).ConfirmAsync(r, ct);
    }
}

public sealed class LarkquillSettings
{
    public string? ModelId { get; set; }
    static string FileIn(string root) => Path.Combine(root, "larkquill-settings.json");
    public static LarkquillSettings Load(string root)
    {
        try { var f = FileIn(root); return File.Exists(f) ? System.Text.Json.JsonSerializer.Deserialize<LarkquillSettings>(File.ReadAllText(f)) ?? new() : new(); }
        catch { return new(); }
    }
    public void Save(string root)
    {
        try { Directory.CreateDirectory(root); File.WriteAllText(FileIn(root), System.Text.Json.JsonSerializer.Serialize(this)); } catch { }
    }
}
