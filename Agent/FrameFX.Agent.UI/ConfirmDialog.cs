namespace UniversalFrameFX.Agent.UI;

/// <summary>Modal confirm for every side-effecting tool. Shows the exact lines; default button is Cancel;
/// closing the window = No. Never auto-approves.</summary>
public sealed class ConfirmDialog : Form
{
    ConfirmDialog(ConfirmRequest r)
    {
        Text = "LarkquillAI: " + r.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(14);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "The assistant wants to do this:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(new TextBox
        {
            Text = string.Join(Environment.NewLine, r.Lines), ReadOnly = true, Multiline = true, WordWrap = true,
            Width = 520, Height = Math.Min(260, 22 * (r.Lines.Count + 1)), ScrollBars = ScrollBars.Vertical, TabStop = false,
        });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var no = new Button { Text = "Cancel", DialogResult = DialogResult.No, AutoSize = true };
        var yes = new Button { Text = r.ConfirmLabel, DialogResult = DialogResult.Yes, AutoSize = true };
        buttons.Controls.Add(no); buttons.Controls.Add(yes);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = no; CancelButton = no; // Enter does NOT confirm
        Shown += (_, _) => no.Focus();
    }

    public static bool Ask(IWin32Window? owner, ConfirmRequest r)
    {
        using var d = new ConfirmDialog(r);
        return d.ShowDialog(owner) == DialogResult.Yes;
    }
}

/// <summary>IConfirmer/IUserPicker on the UI thread of <paramref name="owner"/>.</summary>
public sealed class WinFormsPrompts(Control owner) : IConfirmer, IUserPicker
{
    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct) =>
        OnUi(() => ConfirmDialog.Ask(owner.FindForm(), request));

    public Task<string?> PickExecutableAsync(string reason, CancellationToken ct) => OnUi<string?>(() =>
    {
        if (MessageBox.Show(owner.FindForm(), reason, "LarkquillAI", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return null;
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Pick the program to allow" };
        return dlg.ShowDialog(owner.FindForm()) == DialogResult.OK ? dlg.FileName : null;
    });

    Task<T> OnUi<T>(Func<T> f)
    {
        if (!owner.InvokeRequired) return Task.FromResult(f());
        var tcs = new TaskCompletionSource<T>();
        owner.BeginInvoke(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }
}
