using System.Diagnostics;
using System.Text.Json;
using UniversalFrameFX.Agent.Cloud;

namespace UniversalFrameFX.Agent.UI;

/// <summary>
/// LarkquillAI panel: chat about FrameFX, and let it build, apply and change presets (every change is checked by
/// FrameFX and confirmed by the user). Model picker shows which plan each model needs. The API key is pasted once
/// and kept with DPAPI. With no key or no connection it falls back to the offline preset model.
/// </summary>
public sealed class LarkquillPanel : Form
{
    readonly LarkquillSession _session;
    readonly ILarkquillKeyStore _keys;
    readonly Action<string> _saveModel;
    CancellationTokenSource? _cts;

    readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false };
    readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true, Height = 54, PlaceholderText = "Ask about FrameFX, or say \"set up Cyberpunk on my 3060\"" };
    readonly Button _send = new() { Text = "Send", Dock = DockStyle.Right, Width = 80 };
    readonly Button _stop = new() { Text = "Stop", Dock = DockStyle.Right, Width = 70, Enabled = false };
    readonly Button _new = new() { Text = "New chat", Dock = DockStyle.Right, Width = 80 };
    readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Margin = new Padding(4, 6, 4, 4) };
    readonly TextBox _key = new() { Width = 220, UseSystemPasswordChar = true, PlaceholderText = "Paste your LarkquillAI API key", Margin = new Padding(4, 6, 4, 4) };
    readonly Button _saveKey = new() { Text = "Save key", AutoSize = true };
    readonly Button _removeKey = new() { Text = "Remove", AutoSize = true };
    readonly LinkLabel _getKey = new() { Text = "Get a free key", AutoSize = true, Margin = new Padding(6, 10, 4, 4) };
    readonly LinkLabel _upgrade = new() { Text = "Upgrade", AutoSize = true, Visible = false, Margin = new Padding(6, 10, 4, 4) };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
    string _upgradeUrl = Larkquill.UpgradeUrl;

    public LarkquillPanel(LarkquillSession session, ILarkquillKeyStore keys, string? savedModelId = null, Action<string>? saveModel = null)
    {
        _session = session; _keys = keys; _saveModel = saveModel ?? (_ => { });
        _session.Model = Larkquill.ById(savedModelId);
        Text = "LarkquillAI"; Width = 780; Height = 640; MinimumSize = new Size(560, 420); StartPosition = FormStartPosition.CenterParent;

        foreach (var m in Larkquill.Models) _model.Items.Add(m);
        _model.Format += (_, e) => { if (e.ListItem is LarkquillModel m) e.Value = $"{m.Name}  ·  {m.PlanLabel}"; };
        _model.SelectedItem = _session.Model;
        _model.SelectedIndexChanged += (_, _) =>
        {
            if (_model.SelectedItem is not LarkquillModel m) return;
            _session.Model = m; _saveModel(m.Id);
            _upgrade.Visible = !Larkquill.Unlocked(m, _session.KnownPlan) && m.Plan != LarkquillPlan.Hobby;
            if (_upgrade.Visible) _upgradeUrl = Larkquill.UpgradeUrl;
            UpdateStatus();
        };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(4, 0, 4, 0) };
        top.Controls.AddRange([new Label { Text = "Model", AutoSize = true, Margin = new Padding(4, 10, 0, 4) }, _model, _upgrade, _key, _saveKey, _removeKey, _getKey]);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        bottom.Controls.Add(_input); bottom.Controls.Add(_new); bottom.Controls.Add(_stop); bottom.Controls.Add(_send);
        Controls.Add(_log); Controls.Add(top); Controls.Add(bottom); Controls.Add(_status);

        _getKey.LinkClicked += (_, _) => Open(Larkquill.GetKeyUrl);
        _upgrade.LinkClicked += (_, _) => Open(_upgradeUrl);
        _saveKey.Click += (_, _) => SaveKey();
        _key.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveKey(); } };
        _removeKey.Click += (_, _) => { _keys.Clear(); _session.KeyChanged(); RefreshKeyUi(); Append(null, "Key removed. Using the offline preset model.\n\n"); };
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _new.Click += (_, _) => { _session.Reset(); _log.Clear(); Greet(); UpdateStatus(); };
        _input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendAsync(); } };

        RefreshKeyUi();
        Greet();
        UpdateStatus();
    }

    void Greet() => Append("LarkquillAI", _session.HasKey
        ? "Hi! Ask me anything about FrameFX, or tell me a game and I'll set it up.\n\n"
        : "Hi! I can answer FrameFX questions and set up your games once you add a free LarkquillAI key. Without one, I'll still make presets offline.\n\n");

    void SaveKey()
    {
        string k = _key.Text.Trim();
        if (k.Length < 12) { MessageBox.Show(this, "That doesn't look like a LarkquillAI API key.", "LarkquillAI"); return; }
        _keys.Save(k); _key.Clear(); _session.KeyChanged(); RefreshKeyUi();
        Append(null, "Key saved on this PC (encrypted for your Windows account).\n\n");
    }

    void RefreshKeyUi()
    {
        bool has = _session.HasKey;
        _key.Visible = !has; _saveKey.Visible = !has; _getKey.Visible = !has; _removeKey.Visible = has;
        UpdateStatus();
    }

    static void Open(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    async Task SendAsync()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || _cts != null) return;
        _input.Clear();
        Append("You", text + "\n");
        Append("LarkquillAI", "");
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try { await Task.Run(() => _session.RunTurnAsync(text, e => BeginInvoke(() => OnEvent(e)), _cts.Token)); }
        catch (OperationCanceledException) { Append(null, "\n[stopped]\n\n"); }
        catch (Exception ex) { Append("Error", ex.Message + "\n\n"); }
        finally { _cts.Dispose(); _cts = null; SetBusy(false); UpdateStatus(); }
    }

    void OnEvent(AgentEvent e)
    {
        switch (e)
        {
            case TextDelta d: _log.AppendText(d.Text); break;
            case ToolStarted s: _log.AppendText($"\n  ⚙ {Friendly(s.Call.Name)}\n"); break;
            case ToolFinished f: if (!f.Result.Ok) _log.AppendText("  ✗ " + FirstLine(f.Result.Text.Replace("ERROR: ", "")) + "\n"); break;
            case AgentNotice n: _log.AppendText($"\n[{n.Text}]\n"); break;
            case UpgradeNeeded u:
                _upgradeUrl = u.UpgradeUrl; _upgrade.Visible = true;
                _log.AppendText($"{u.Message}\nClick Upgrade at the top to unlock it, or pick a model on your plan.\n");
                break;
            case KeyNeeded k: RefreshKeyUi(); _log.AppendText($"[{k.Message}]\n"); break;
            case UsedOffline o: _status.Text = $"Offline preset model ({o.Reason})"; break;
            case TurnDone: _log.AppendText("\n\n"); break;
        }
        _log.ScrollToCaret();
    }

    static string Friendly(string tool) => tool switch
    {
        "make_preset" => "making a preset", "apply_preset" => "applying the preset", "set_setting" => "changing a setting",
        "start_framefx" => "starting FrameFX", "stop_framefx" => "stopping FrameFX", "get_framefx_state" => "checking FrameFX", _ => tool,
    };
    static string FirstLine(string s) { int i = s.IndexOf('\n'); return i < 0 ? s : s[..i]; }

    void Append(string? who, string text)
    {
        if (who != null) { _log.SelectionFont = new Font(_log.Font, FontStyle.Bold); _log.AppendText(who + ": "); _log.SelectionFont = _log.Font; }
        _log.AppendText(text);
    }
    void SetBusy(bool b) { _send.Enabled = !b; _stop.Enabled = b && _cts != null; _model.Enabled = !b; }
    void UpdateStatus()
    {
        var m = _session.Model;
        _status.Text = _session.HasKey
            ? $"LarkquillAI cloud · {m.Name} · {m.ContextTokens / 1000}k context · changes always ask first"
            : "No key: offline preset model · get a free key for full chat";
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { _cts?.Cancel(); base.OnFormClosing(e); }
}
