namespace UniversalFrameFX;

// LarkquillAI button (1.4.1-beta.10). Add _larkquill next to the other main-page buttons in MainForm.Layout.cs and
// call WireLarkquill() once after InitializeLayout().
public sealed partial class MainForm
{
    readonly PillButton _larkquill = new() { Text = "LarkquillAI", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 6, 0, 0) };

    void WireLarkquill() => _larkquill.Click += (_, _) => UniversalFrameFX.Agent.App.LarkquillBootstrap.Open(this);
}
