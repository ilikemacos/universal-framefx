using System.Diagnostics;
using UniversalFrameFX.Agent.App;

namespace UniversalFrameFX;

// LarkquillAI hooks: small public wrappers the agent's FrameFxHostAdapter calls (always on the UI thread).
// Wire: LarkquillAI → LarkquillBootstrap.Open(this); LarkquillAI Local → AgentBootstrap.Open(this).
// Both Open paths ask "Do we have permission to run LarkquillAI locally for FrameFX purposes?" once when unset.
public sealed partial class MainForm
{
    readonly PillButton _agentAssistant = new() { Text = "LarkquillAI", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 6, 0, 0) };
    readonly PillButton _agentLocal = new() { Text = "LarkquillAI Local (experimental)", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 6, 0, 0) };

    public bool AgentIsRunning => _out != null;
    public string AgentGpuName => _gpu?.AdapterName ?? "unknown GPU";

    /// <summary>Same path as the Start button in games mode: find the game's window, load its profile, start the overlay.</summary>
    public (bool Ok, string Message) AgentStart(string exe)
    {
        if (_gpu is null || _pipe is null) return (false, "FrameFX has no GPU device right now.");
        if (_out != null) return (false, "FrameFX is already running. Stop it first.");
        string name = Path.GetFileNameWithoutExtension(exe.Trim());
        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; } } catch { }
            finally { p.Dispose(); }
        }
        if (hwnd == IntPtr.Zero) return (false, $"{name}.exe isn't running (or has no window). Start the game first.");
        PrepareGameSettings(name + ".exe");
        StartOutput(hwnd, OutputMode.Overlay, name + ".exe");
        return _out != null ? (true, $"Started on {name}.exe.") : (false, "FrameFX couldn't start on that window.");
    }

    /// <summary>Same as the Stop button.</summary>
    public (bool Ok, string Message) AgentStop()
    {
        if (_out != null) { _out.UserStop = true; _out.Close(); return (true, "Stopped."); }
        if (_gamesArmed) { DisarmGames(); _start.Text = "Start"; return (true, "Games mode disarmed."); }
        return (true, "FrameFX wasn't running.");
    }
}
