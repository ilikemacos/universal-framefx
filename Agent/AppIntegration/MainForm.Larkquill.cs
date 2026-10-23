namespace UniversalFrameFX;

// LarkquillAI button (1.4.1-beta.11). Add _larkquill (or use _agentAssistant) next to the other main-page buttons
// in MainForm.Layout.cs and call WireLarkquill() once after InitializeLayout().
// Opening asks for local-run permission once; No makes LarkquillAI unavailable.
public sealed partial class MainForm
{
    readonly PillButton _larkquill = new() { Text = "LarkquillAI", AutoSize = true, Kind = PillKind.Ghost, Margin = new Padding(0, 6, 0, 0) };

    void WireLarkquill() => _larkquill.Click += (_, _) => UniversalFrameFX.Agent.App.LarkquillBootstrap.Open(this);
}
