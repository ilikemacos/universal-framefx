using UniversalFrameFX.Agent;
using Xunit;

namespace FrameFX.Agent.Tests;

public class IdentityTests
{
    [Fact]
    public void SystemPromptUsesWrenbyAIName()
    {
        Assert.Contains("You are WrenbyAI", AgentLoop.SystemPrompt);
        Assert.Contains("WrenbyAI made for FrameFX", AgentLoop.SystemPrompt);
        Assert.DoesNotContain("WyneAI", AgentLoop.SystemPrompt);
        Assert.DoesNotContain("Qwen", AgentLoop.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }
}
