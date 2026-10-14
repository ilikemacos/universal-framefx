using System.Diagnostics;
using System.Net.Http;

namespace UniversalFrameFX.Agent;

/// <summary>Where the setup script put things. Default %LOCALAPPDATA%\FrameFX\agent (override with FRAMEFX_AGENT_DIR, e.g. W:\FrameFX\agent).</summary>
public sealed record AgentPaths(string Root)
{
    public static AgentPaths Default()
    {
        string? env = Environment.GetEnvironmentVariable("FRAMEFX_AGENT_DIR");
        string root = !string.IsNullOrWhiteSpace(env) ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameFX", "agent");
        return new AgentPaths(root);
    }
    public string ServerExe => Path.Combine(Root, "llama", "llama-server.exe");
    public string ModelFile => Path.Combine(Root, "models", "framefx-assistant.gguf");
    public string AllowlistFile => Path.Combine(Root, "launch-allowlist.json");
    public string SettingsFile => Path.Combine(Root, "agent-settings.json");
    public bool Installed => File.Exists(ServerExe) && File.Exists(ModelFile);
}

/// <summary>
/// Starts the bundled llama-server (Vulkan build) on 127.0.0.1, port 8737 when free (the WyneAI web page connects there) or a random port otherwise, waits for /health,
/// and walks the ContextSizer fallback ladder if the process dies while loading (usually out of VRAM).
/// Bound to localhost only; --jinja turns on native tool calling through the chat template.
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    readonly AgentPaths _paths;
    Process? _proc;
    public ContextPlan? Plan { get; private set; }
    public Uri? BaseUri { get; private set; }
    public string LastLog { get; private set; } = "";
    public event Action<string>? Status;

    public LlamaServerHost(AgentPaths paths) => _paths = paths;

    public async Task<ContextPlan> StartAsync(long vramBytes, int maxContext = 131_072, long reserveBytes = ContextSizer.DefaultReserveBytes, CancellationToken ct = default)
    {
        if (!_paths.Installed) throw new InvalidOperationException("The assistant isn't installed yet. Run Setup-FrameFXAgent.ps1.");
        var first = ContextSizer.Pick(vramBytes, reserveBytes: reserveBytes, maxContext: maxContext);
        foreach (var plan in ContextSizer.Ladder(first))
        {
            ct.ThrowIfCancellationRequested();
            Status?.Invoke($"Starting assistant: {plan.ContextTokens / 1024}k context, {plan.Reason}…");
            if (await TryStartAsync(plan, ct)) { Plan = plan; return plan; }
        }
        throw new InvalidOperationException("The assistant couldn't start on this PC. Last log:\n" + LastLog);
    }

    async Task<bool> TryStartAsync(ContextPlan plan, CancellationToken ct)
    {
        Stop();
        int port = FreePort();
        var psi = new ProcessStartInfo(_paths.ServerExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(_paths.ServerExe)!,
        };
        ConfigureEnvironment(psi);
        foreach (var a in new[] { "-m", _paths.ModelFile, "--host", "127.0.0.1", "--port", port.ToString(),
                                  "--jinja", "--parallel", "1", "--no-webui", "--alias", "wyneai",
                                  // Only the WyneAI web page may call this server from a browser (other sites get no CORS header).
                                  "--cors-origins", "https://ilikemacos.github.io" })
            psi.ArgumentList.Add(a);
        foreach (var a in plan.ToArgs()) psi.ArgumentList.Add(a);

        var log = new System.Text.StringBuilder();
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) { log.AppendLine(e.Data); if (log.Length > 20_000) log.Remove(0, 10_000); } };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        _proc.Start();
        _proc.BeginErrorReadLine(); _proc.BeginOutputReadLine();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddMinutes(4); // first Vulkan shader compile can be slow
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited) { lock (log) LastLog = log.ToString(); return false; }
            try
            {
                using var r = await http.GetAsync(new Uri(BaseUri, "health"), ct);
                if (r.IsSuccessStatusCode) { lock (log) LastLog = log.ToString(); return true; }
            }
            catch (HttpRequestException) { } catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct);
        }
        lock (log) LastLog = log.ToString();
        Stop();
        return false;
    }

    /// <summary>Fixed port the WyneAI web page (ilikemacos.github.io/framefx/wyneai/) uses by default.</summary>
    public const int PreferredPort = 8737;

    /// <summary>RivaTuner (RTSS) injects a Vulkan layer that crashes llama-server; this turns it off for the server only.</summary>
    internal static void ConfigureEnvironment(ProcessStartInfo psi) => psi.Environment["DISABLE_RTSS_LAYER"] = "1";

    static int FreePort()
    {
        try
        {
            var f = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, PreferredPort);
            f.Start(); f.Stop(); return PreferredPort;
        }
        catch (System.Net.Sockets.SocketException) { }
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc?.Dispose(); _proc = null;
    }

    public void Dispose() => Stop();
}
