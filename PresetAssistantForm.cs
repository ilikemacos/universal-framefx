using System.Drawing;
using System.Windows.Forms;
using UniversalFrameFX.PresetAi;

namespace UniversalFrameFX;

public sealed class PresetAssistantForm : Form
{
    readonly MainForm _host;
    readonly ChatSession _chat;
    readonly Panel _messages;
    readonly TextBox _input;
    readonly Label _summary;
    readonly Panel _proposal;
    readonly Label _proposalText;
    readonly Button _apply;
    readonly Button _undo;
    MainForm.AssistantBaseline? _undoSnap;
    string? _pendingExe;

    public PresetAssistantForm(MainForm host, OnnxPresetModel? model)
    {
        _host = host;
        _chat = new ChatSession(model);
        Text = "Preset Assistant";
        ClientSize = new Size(520, 640);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body();
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(420, 480);

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 8, 12, 4),
            ForeColor = Theme.Muted,
            Text = "Current preset: (none yet)",
        };

        _messages = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12),
            BackColor = Theme.Bg,
        };

        _proposal = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 120,
            Padding = new Padding(12),
            BackColor = Theme.Card,
            Visible = false,
        };
        _proposalText = new Label { Dock = DockStyle.Top, Height = 56, ForeColor = Theme.Text, AutoSize = false };
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _undo = new Button { Text = "Undo", Width = 88, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text };
        _apply = new Button { Text = "Apply", Width = 88, FlatStyle = FlatStyle.Flat, BackColor = Theme.Primary, ForeColor = Theme.PrimaryText };
        _undo.Click += (_, _) => UndoProposal();
        _apply.Click += (_, _) => ApplyProposal();
        btnRow.Controls.Add(_apply);
        btnRow.Controls.Add(_undo);
        _proposal.Controls.Add(btnRow);
        _proposal.Controls.Add(_proposalText);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 8, 12, 8) };
        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendMessage(); }
        };
        bottom.Controls.Add(_input);

        Controls.Add(_messages);
        Controls.Add(_proposal);
        Controls.Add(bottom);
        Controls.Add(_summary);

        AddBubble("Assistant",
            "Hi! Tell me your game, GPU, and fps (current vs. wanted). Then refine: \"what about 4K\", \"make it quieter\", \"turn frame gen off\"…",
            assistant: true);
        RefreshSummary();
    }

    void SendMessage()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        _input.Clear();
        AddBubble("You", t, assistant: false);
        var reply = _chat.Turn(t);
        AddBubble("Assistant", reply, assistant: true);
        UpdateProposal();
        RefreshSummary();
    }

    void AddBubble(string who, string text, bool assistant)
    {
        var wrap = new Panel
        {
            Width = _messages.ClientSize.Width - 32,
            AutoSize = true,
            MaximumSize = new Size(_messages.ClientSize.Width - 32, 0),
            Padding = new Padding(assistant ? 4 : 40, 4, assistant ? 40 : 4, 8),
        };
        var lbl = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(wrap.MaximumSize.Width - 16, 0),
            Text = who + ":\r\n" + text,
            ForeColor = assistant ? Theme.Text : Theme.Accent,
            BackColor = assistant ? Theme.Card : Color.FromArgb(40, Theme.Accent),
            Padding = new Padding(10, 8, 10, 8),
        };
        wrap.Controls.Add(lbl);
        wrap.Height = lbl.Height + 12;
        _messages.Controls.Add(wrap);
        _messages.ScrollControlIntoView(wrap);
    }

    void UpdateProposal()
    {
        if (_chat.LastDecision is not { } d)
        {
            _proposal.Visible = false;
            _pendingExe = null;
            return;
        }
        _pendingExe = d.Exe;
        var prof = PresetAssistantMapper.ToGameProfile(d);
        _proposalText.Text = $"Suggested for {GameProfiles.DisplayName(d.Exe)}:\r\n{GameProfiles.Summary(prof)}";
        _proposal.Visible = true;
    }

    void ApplyProposal()
    {
        if (_chat.LastDecision is not { } d || _pendingExe is null) return;
        _undoSnap = _host.CaptureAssistantBaseline();
        var prof = PresetAssistantMapper.ToGameProfile(d);
        _host.ApplyAssistantProfile(_pendingExe, prof);
        _proposal.Visible = false;
        AddBubble("Assistant", "Applied. Settings saved through your normal game profile.", assistant: true);
        RefreshSummary();
    }

    void UndoProposal()
    {
        if (_undoSnap is { } snap)
        {
            _host.RestoreAssistantBaseline(snap);
            _undoSnap = null;
            AddBubble("Assistant", "Reverted the last assistant apply.", assistant: true);
            RefreshSummary();
        }
    }

    void RefreshSummary()
    {
        if (_chat.LastDecision is { } d)
        {
            var p = PresetAssistantMapper.ToGameProfile(d);
            _summary.Text = "Draft preset: " + GameProfiles.Summary(p) + " · " + GameProfiles.DisplayName(d.Exe);
        }
        else
            _summary.Text = "Current preset: " + (_host.ActiveProfileSummary() ?? "(none yet)");
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        foreach (Control c in _messages.Controls)
            if (c is Panel p) p.Width = _messages.ClientSize.Width - 32;
    }
}
