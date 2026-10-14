using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalFrameFX.Agent.Tools;

public sealed record AllowedApp(string Name, string Path);

/// <summary>Apps the agent may launch. Stored as JSON. Entries are added only through the user's own file picker.</summary>
public sealed class LaunchAllowlist
{
    readonly string _file;
    readonly List<AllowedApp> _apps;
    public LaunchAllowlist(string file)
    {
        _file = file;
        _apps = File.Exists(file) ? JsonSerializer.Deserialize<List<AllowedApp>>(File.ReadAllText(file)) ?? [] : [];
    }
    public IReadOnlyList<AllowedApp> Apps => _apps;
    public AllowedApp? Find(string name) =>
        _apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _apps.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
    public void Add(AllowedApp app)
    {
        _apps.RemoveAll(a => a.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
        _apps.Add(app);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_apps, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Launches an allow-listed program, with no arguments, after the user confirms the exact path.
/// If the app isn't on the list, the user can pick the .exe themselves (that adds it). The model can't supply
/// a path or arguments, and nothing goes through a shell (cmd/powershell/scripts are refused).
/// </summary>
public sealed class LaunchAppTool(LaunchAllowlist list, IUserPicker picker, Func<string, Process?>? starter = null) : IAgentTool
{
    static readonly string[] Blocked = ["cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
                                        "rundll32.exe", "regsvr32.exe", "reg.exe", "bash.exe", "wsl.exe", "msiexec.exe"];
    public string Name => "launch_app";
    public string Description => "Open a program on this PC by name, e.g. a game or Steam. Only apps on the user's allowlist; " +
                                 "if missing, the user is asked to pick the .exe. Allowed now: " +
                                 (list.Apps.Count == 0 ? "(none yet)" : string.Join(", ", list.Apps.Select(a => a.Name)));
    public JsonObject Parameters { get; } = Args.Schema(("name", "string", "app name from the allowlist", true));

    AllowedApp? _pending;

    public async Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct)
    {
        string name = Args.Str(a, "name");
        var app = list.Find(name);
        if (app == null)
        {
            string? path = await picker.PickExecutableAsync($"The assistant wants to open \"{name}\", which isn't on your allowed list. Pick its .exe to allow it, or cancel.", ct);
            if (path == null) throw new ArgumentException($"'{name}' is not on the allowlist and the user didn't pick it");
            app = new AllowedApp(name, path);
        }
        Validate(app.Path);
        _pending = app;
        return new ConfirmRequest("Open a program?",
            [$"Program: {app.Name}", $"File: {app.Path}", "Arguments: none",
             list.Find(app.Name) == null ? "This also adds it to your allowed apps list." : "It's on your allowed apps list."], "Open");
    }

    internal static void Validate(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("only full paths to .exe files can be launched");
        if (Blocked.Contains(Path.GetFileName(path).ToLowerInvariant()))
            throw new ArgumentException("shells and script hosts can't be launched by the assistant");
        if (!File.Exists(path)) throw new ArgumentException($"file not found: {path}");
    }

    public Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var app = _pending ?? throw new InvalidOperationException("not confirmed");
        _pending = null;
        if (list.Find(app.Name) == null) list.Add(app);
        var p = (starter ?? (path => Process.Start(new ProcessStartInfo(path)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })))(app.Path);
        return Task.FromResult(ToolResult.Success(p == null ? $"Started {app.Name}." : $"Started {app.Name} (pid {p.Id})."));
    }
}
