// Copy into the app project. Builds everything and opens the chat window.
using UniversalFrameFX.Agent.Tools;
using UniversalFrameFX.Agent.UI;
using UniversalFrameFX.Agent;

namespace UniversalFrameFX.Agent.App;

public static class AgentBootstrap
{
    static LlamaServerHost? _host; // one server per app run; disposed in MainForm.OnFormClosed
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) }; // shared: one per chat leaked sockets

    public static void Shutdown() { _host?.Dispose(); _host = null; }

    public static void Open(MainForm form)
    {
        if (!LarkquillPermission.EnsureAllowed(form)) return;
        var paths = AgentPaths.Default();
        if (!paths.Installed)
        {
            MessageBox.Show(form, "LarkquillAI needs a one-time download (about 2 GB).\nRun agent\\scripts\\Setup-FrameFXAgent.ps1 from the FrameFX folder, then try again.",
                "LarkquillAI");
            return;
        }
        AgentChatForm? chat = null;
        chat = new AgentChatForm(async () =>
        {
            var (vram, gpuName) = DxgiVramProbe.Best();
            _host ??= new LlamaServerHost(paths);
            // If a game is already running on this GPU, leave it more room.
            long reserve = form.AgentIsRunning ? ContextSizer.GamingReserveBytes : ContextSizer.DefaultReserveBytes;
            var plan = _host.Plan ?? await _host.StartAsync(vram, reserveBytes: reserve);
            var prompts = new WinFormsPrompts(chat!);
            var http = _http;
            var settings = AgentSettings.Load(paths.SettingsFile);
            var tools = DefaultTools.Build(prompts, prompts, new FrameFxHostAdapter(form), new PresetAiEngine(),
                DefaultTools.Search(http, settings.SearchProvider, settings.SearxngUrl, settings.SearchApiKey),
                http, new LaunchAllowlist(paths.AllowlistFile));
            var backend = new LlamaBackend(new LlamaChatClient(_host.BaseUri!));
            return new AgentLoop(backend, tools, new TokenBudget(plan.ContextTokens, settings.SessionTokenLimit));
        });
        chat.Show(form);
    }
}

public sealed class AgentSettings
{
    public string SearchProvider { get; set; } = "duckduckgo";
    public string? SearxngUrl { get; set; }
    public string? SearchApiKey { get; set; }  // stored per-user in %LOCALAPPDATA%; consider DPAPI (ProtectedData) before shipping
    public long SessionTokenLimit { get; set; } = 262_144;
    public static AgentSettings Load(string file)
    {
        // A hand-edited or half-written settings file must not take the chat window down with it.
        try
        {
            if (File.Exists(file))
                return System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new();
    }
}
