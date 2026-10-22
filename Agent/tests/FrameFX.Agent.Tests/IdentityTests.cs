using UniversalFrameFX.Agent;
using Xunit;

namespace FrameFX.Agent.Tests;

public class IdentityTests
{
    [Fact]
    public void SystemPromptUsesLarkquillAIName()
    {
        Assert.Contains("You are LarkquillAI", AgentLoop.SystemPrompt);
        Assert.Contains("LarkquillAI made for FrameFX", AgentLoop.SystemPrompt);
        Assert.DoesNotContain("WyneAI", AgentLoop.SystemPrompt);
        Assert.DoesNotContain("Qwen", AgentLoop.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }
}
