using UniversalFrameFX.PresetAi;
using Xunit;

namespace UniversalFrameFX.PresetAi.Tests;

public class ChatRulesOnlyTests
{
    [Fact]
    public void Cyberpunk_turn_sets_last_decision_without_onnx()
    {
        var chat = new ChatSession(null);
        _ = chat.Turn("hi");
        _ = chat.Turn("weather?");
        var reply = chat.Turn("I want to play Cyberpunk 2077, getting about 45 fps and want 120");
        Assert.Contains("Cyberpunk", reply);
        Assert.NotNull(chat.LastDecision);
        Assert.Equal("cyberpunk2077.exe", chat.LastDecision!.Exe);
    }
}
