using System.Text.Json;

namespace UniversalFrameFX.Agent.UI;

/// <summary>
/// Chat window: streamed replies, a "used tool" row per tool call (expand to see arguments and result),
/// Stop button, and a status line with context size and session tokens used / 256k.
/// The app builds the AgentLoop (see HANDOFF.md) and passes it in.
/// </summary>
public sealed class AgentChatForm : Form
{
    readonly Func<Task<AgentLoop>> _start;
    AgentLoop? _agent;
    CancellationTokenSource? _cts;
    readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true };
    readonly ListView _toolsView = new() { Dock = DockStyle.Bottom, Height = 120, View = View.Details, FullRowSelect = true };
    readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true, Height = 54 };
    readonly Button _send = new() { Text = "Send", Dock = DockStyle.Right, Width = 80 };
    readonly Button _stop = new() { Text = "Stop", Dock = DockStyle.Right, Width = 70, Enabled = false };
    readonly Button _new = new() { Text = "New chat", Dock = DockStyle.Right, Width = 80 };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 20, Text = "Starting the assistant…" };

    public AgentChatForm(Func<Task<AgentLoop>> start)
    {
        _start = start;
        Text = "WyneAI"; Width = 760; Height = 640; StartPosition = FormStartPosition.CenterParent;
        _toolsView.Columns.Add("Tool", 150); _toolsView.Columns.Add("Result", 560);
        _toolsView.ItemActivate += (_, _) =>
        {
            if (_toolsView.SelectedItems.Count == 1 && _toolsView.SelectedItems[0].Tag is string detail)
                MessageBox.Show(this, detail, "Tool details");
        };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        bottom.Controls.Add(_input); bottom.Controls.Add(_new); bottom.Controls.Add(_stop); bottom.Controls.Add(_send);
        Controls.Add(_log); Controls.Add(_toolsView); Controls.Add(bottom); Controls.Add(_status);
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _new.Click += (_, _) => { _agent?.Reset(); _log.Clear(); _toolsView.Items.Clear(); UpdateStatus(); };
        _input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendAsync(); } };
        Load += async (_, _) =>
        {
            SetBusy(true);
            try { _agent = await _start(); Append("WyneAI", "Hi! I'm WyneAI. Ask me anything, or tell me what to set up.\n"); }
            catch (Exception ex) { Append("Error", ex.Message + "\n"); }
            finally { SetBusy(false); UpdateStatus(); }
        };
    }

    async Task SendAsync()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || _agent == null || _cts != null) return;
        _input.Clear();
        Append("You", text + "\n");
        Append("WyneAI", "");
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await Task.Run(() => _agent.RunTurnAsync(text, e => BeginInvoke(() => OnEvent(e)), _cts.Token));
        }
        catch (OperationCanceledException) { Append(null, "\n[stopped]\n"); }
        catch (Exception ex) { Append("Error", ex.Message + "\n"); }
        finally { _cts.Dispose(); _cts = null; SetBusy(false); UpdateStatus(); }
    }

    void OnEvent(AgentEvent e)
    {
        switch (e)
        {
            case TextDelta d: _log.AppendText(d.Text); break;
            case ToolStarted s: _log.AppendText($"\n  ⚙ used tool: {s.Call.Name}\n"); break;
            case ToolFinished f:
                var item = new ListViewItem([f.Call.Name, (f.Result.Ok ? "✓ " : "✗ ") + FirstLine(f.Result.Text)])
                { Tag = $"Arguments:\n{Pretty(f.Call.ArgumentsJson)}\n\nResult:\n{f.Result.Text}" };
                _toolsView.Items.Add(item); item.EnsureVisible();
                break;
            case AgentNotice n: _log.AppendText($"\n[{n.Text}]\n"); break;
            case TurnDone: _log.AppendText("\n\n"); break;
        }
        _log.ScrollToCaret();
        UpdateStatus();
    }

    static string FirstLine(string s) { int i = s.IndexOf('\n'); return i < 0 ? s : s[..i]; }
    static string Pretty(string json)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch { return json; }
    }
    void Append(string? who, string text)
    {
        if (who != null) { _log.SelectionFont = new Font(_log.Font, FontStyle.Bold); _log.AppendText(who + ": "); _log.SelectionFont = _log.Font; }
        _log.AppendText(text);
    }
    void SetBusy(bool b) { _send.Enabled = !b; _stop.Enabled = b && _cts != null; }
    void UpdateStatus()
    {
        if (_agent == null) return;
        var bud = _agent.Budget;
        _status.Text = $"Context {bud.ContextWindow / 1024}k · session {bud.Used / 1000.0:0.#}k / {bud.SessionLimit / 1024}k tokens · runs offline on this PC";
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { _cts?.Cancel(); base.OnFormClosing(e); }
}
