// Copy into the app project (UniversalFrameFX). Wraps the Beta7 model-first ChatSession (PresetAi).
using UniversalFrameFX.Agent.Tools;
using UniversalFrameFX.PresetAi;

namespace UniversalFrameFX.Agent.App;

public sealed class PresetAiEngine : IPresetEngine
{
    readonly ChatSession _session;
    public PresetAiEngine()
    {
        // Beta7 + model-first is the default in 1.4.1-beta.8 (PresetModelSettings.DefaultKind/DefaultPolicy).
        var model = OnnxPresetModel.TryLoad(PresetModelSettings.DefaultKind);
        _session = new ChatSession(model);
    }
    public void Reset() => _session.Reset();
    public PresetProposal Turn(string request)
    {
        string reply = _session.Turn(request);
        var d = _session.LastDecision;
        if (d == null || d.Exe is "?" or "-" or "") return new PresetProposal(reply, null, null);
        var g = PresetAssistantMapper.ToGameProfile(d);
        string note = PresetAssistantMapper.AntiCheatNote(d.Exe);
        return new PresetProposal(reply, FrameFxHostAdapter.ToSnapshot(d.Exe, g), note.Length > 0 ? note : null);
    }
}
