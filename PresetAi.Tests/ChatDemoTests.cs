using UniversalFrameFX.PresetAi;
using Xunit;

namespace UniversalFrameFX.PresetAi.Tests;

public class ChatDemoTests
{
    static readonly string[] DemoTurns =
    [
        "hi, can you help me?",
        "what's the weather like tomorrow?",
        "I want to play Cyberpunk 2077, getting about 45 fps and want 120",
        "actually I have a 4070",
        "what about 4K",
        "I hate blur and ghosting",
        "make it quieter",
        "turn frame gen off",
        "switch to Valorant",
        "start over",
    ];

    [Fact]
    public void Chat_demo_conversation_updates_profile()
    {
        using var model = OnnxPresetModel.TryLoad(PresetModelKind.HybridR3);
        Assert.NotNull(model);
        var chat = new ChatSession(model);
        var replies = new List<string>();
        foreach (var (turn, i) in DemoTurns.Select((t, i) => (t, i)))
        {
            replies.Add(chat.Turn(turn));
            if (i == 2)
            {
                Assert.Contains("Cyberpunk 2077", replies[2]);
                Assert.Contains("RTX 3060", replies[2]);
                Assert.NotNull(chat.LastDecision);
                Assert.Equal("cyberpunk2077.exe", chat.LastDecision!.Exe.ToLowerInvariant());
            }
            if (i == 8)
            {
                Assert.Contains("VALORANT", replies[8], StringComparison.OrdinalIgnoreCase);
                Assert.Contains("anti-cheat", replies[8], StringComparison.OrdinalIgnoreCase);
                Assert.Equal("0", chat.LastDecision!.Fg);
            }
        }

        Assert.Contains("Hi! I build FrameFX presets", replies[0]);
        Assert.Contains("I only help with game presets", replies[1]);
        Assert.Contains("4070", replies[3]);
        Assert.Contains("4K", replies[4]);
        Assert.Contains("2x", replies[5]);
        Assert.Contains("frame generation off", replies[7]);
        Assert.Contains("starting over", replies[9], StringComparison.OrdinalIgnoreCase);
        Assert.Null(chat.LastDecision);
    }

    [Fact]
    public void Sanitize_slots_from_decision()
    {
        var chat = new ChatSession(null);
        chat.Turn("Cyberpunk on RTX 4070, 45 fps, want 120");
        Assert.NotNull(chat.LastDecision);
        var d = chat.LastDecision!;
        Assert.True(RulesEngine.Valid(d));
        Assert.True(int.Parse(d.M) is 2 or 3 or 4 or 0);
    }
}
