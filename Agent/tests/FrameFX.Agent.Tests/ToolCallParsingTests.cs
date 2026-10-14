using System.Text.Json;
using UniversalFrameFX.Agent;
using UniversalFrameFX.Agent.Tools;
using Xunit;

public class ToolCallParsingTests
{
    static string Chunk(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } });

    [Fact]
    public void StreamedText_Accumulates()
    {
        var acc = new StreamAccumulator();
        Assert.Equal("Hel", acc.FeedLine(Chunk(new { content = "Hel" })));
        Assert.Equal("lo", acc.FeedLine(Chunk(new { content = "lo" })));
        Assert.Null(acc.FeedLine("data: [DONE]"));
        var t = acc.Finish();
        Assert.Equal("Hello", t.Content);
        Assert.Empty(t.ToolCalls);
    }

    [Fact]
    public void StreamedToolCall_FragmentsJoined()
    {
        var acc = new StreamAccumulator();
        acc.FeedLine(Chunk(new { tool_calls = new[] { new { index = 0, id = "c1", type = "function", function = new { name = "web_search", arguments = "{\"que" } } } }));
        acc.FeedLine(Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "ry\":\"what is google\"}" } } } }));
        acc.FeedLine(Chunk(new { tool_calls = new[] { new { index = 1, id = "c2", function = new { name = "get_framefx_state", arguments = "" } } } }, "tool_calls"));
        acc.FeedLine("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30}}");
        var t = acc.Finish();
        Assert.Equal(2, t.ToolCalls.Count);
        Assert.Equal("web_search", t.ToolCalls[0].Name);
        Assert.Equal("what is google", JsonDocument.Parse(t.ToolCalls[0].ArgumentsJson).RootElement.GetProperty("query").GetString());
        Assert.Equal("{}", t.ToolCalls[1].ArgumentsJson);
        Assert.Equal("tool_calls", t.FinishReason);
        Assert.Equal(new Usage(120, 30), t.Usage);
    }

    [Fact]
    public void InlineToolCallTags_Fallback()
    {
        var acc = new StreamAccumulator();
        acc.FeedLine(Chunk(new { content = "Sure.\n<tool_call>\n{\"name\": \"make_preset\", \"arguments\": {\"request\": \"Cyberpunk on a 3060\"}}\n</tool_call>" }));
        var t = acc.Finish();
        Assert.Equal("Sure.", t.Content);
        Assert.Single(t.ToolCalls);
        Assert.Equal("make_preset", t.ToolCalls[0].Name);
        Assert.Contains("Cyberpunk", t.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void InlineToolCall_StringArguments()
    {
        var (_, calls) = ToolCallParser.ExtractInline("<tool_call>{\"name\":\"fetch_page\",\"arguments\":\"{\\\"url\\\":\\\"https://x.com\\\"}\"}</tool_call>");
        Assert.Equal("{\"url\":\"https://x.com\"}", calls[0].ArgumentsJson);
    }

    [Fact]
    public void BrokenJson_IsKeptAsRawAndRejected()
    {
        var acc = new StreamAccumulator();
        acc.FeedLine(Chunk(new { tool_calls = new[] { new { index = 0, id = "c", function = new { name = "web_search", arguments = "{\"query\": " } } } }));
        var call = acc.Finish().ToolCalls[0];
        var reg = new ToolRegistry(new Confirm(true)).Add(new WebSearchTool(new FakeSearch()));
        var r = reg.InvokeAsync(call, default).Result;
        Assert.False(r.Ok);
    }

    [Fact]
    public void NonDataLines_Ignored()
    {
        var acc = new StreamAccumulator();
        Assert.Equal("", acc.FeedLine(": keep-alive"));
        Assert.Equal("", acc.FeedLine(""));
    }

    [Fact]
    public void Registry_DeclinedConfirm_DoesNotExecute()
    {
        var host = new FakeHost();
        var reg = new ToolRegistry(new Confirm(false)).Add(new SetSettingTool(host));
        var r = reg.InvokeAsync(new ToolCall("1", "set_setting", "{\"exe\":\"game.exe\",\"key\":\"ssgi\",\"value\":\"on\"}"), default).Result;
        Assert.False(r.Ok);
        Assert.Equal(0, host.Saves);
    }

    [Fact]
    public void Registry_ConfirmedSetting_Saves_AndShowsBeforeAfter()
    {
        var host = new FakeHost();
        var c = new Confirm(true);
        var reg = new ToolRegistry(c).Add(new SetSettingTool(host));
        var r = reg.InvokeAsync(new ToolCall("1", "set_setting", "{\"exe\":\"game.exe\",\"key\":\"fg_mode\",\"value\":\"fsr3\"}"), default).Result;
        Assert.True(r.Ok, r.Text);
        Assert.Equal(1, host.Saves);
        Assert.Contains(c.Last!.Lines, l => l.StartsWith("Before:"));
    }

    [Fact]
    public void FrameGen_On_AntiCheatGame_Refused()
    {
        var host = new FakeHost { AntiCheat = true };
        var reg = new ToolRegistry(new Confirm(true)).Add(new SetSettingTool(host));
        var r = reg.InvokeAsync(new ToolCall("1", "set_setting", "{\"exe\":\"eac.exe\",\"key\":\"frame_gen\",\"value\":\"on\"}"), default).Result;
        Assert.False(r.Ok);
        Assert.Equal(0, host.Saves);
    }

    [Fact]
    public void UnknownTool_Fails()
    {
        var r = new ToolRegistry(new Confirm(true)).InvokeAsync(new ToolCall("1", "run_shell", "{}"), default).Result;
        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost/")]
    [InlineData("http://192.168.1.5/")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void FetchPage_RejectsLocal(string url) => Assert.Throws<ArgumentException>(() => FetchPageTool.CheckUrl(url));

    [Fact]
    public void DuckDuckGo_Parse()
    {
        string html = "<a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.google.com%2F&amp;rut=x\">Google</a>" +
                      "<a class=\"result__snippet\" href=\"x\">Search the <b>world</b></a>";
        var h = DuckDuckGoHtmlProvider.Parse(html, 5);
        Assert.Equal("https://www.google.com/", h[0].Url);
        Assert.Equal("Search the world", h[0].Snippet);
    }

    [Fact]
    public void LaunchApp_RejectsShells() =>
        Assert.Throws<ArgumentException>(() => LaunchAppTool.Validate(@"C:\Windows\System32\cmd.exe"));

    [Fact]
    public void AgentLoop_RunsToolThenAnswers()
    {
        var backend = new ScriptedBackend(
            new AssistantTurn("", [new ToolCall("a", "get_framefx_state", "{}")], "tool_calls", null),
            new AssistantTurn("FrameFX isn't running.", [], "stop", null));
        var reg = new ToolRegistry(new Confirm(true)).Add(new GetStateTool(new FakeHost()));
        var loop = new AgentLoop(backend, reg, new TokenBudget(16_384));
        var events = new List<AgentEvent>();
        loop.RunTurnAsync("is it running?", events.Add, default).Wait();
        Assert.Contains(events, e => e is ToolFinished { Result.Ok: true });
        Assert.Equal("FrameFX isn't running.", events.OfType<TurnDone>().Single().FinalText);
        Assert.Equal("tool", loop.History[^2].Role);
    }

    [Fact]
    public void AgentLoop_StepLimit()
    {
        var turns = Enumerable.Range(0, 20).Select(_ => new AssistantTurn("", [new ToolCall("a", "get_framefx_state", "{}")], "tool_calls", null)).ToArray();
        var loop = new AgentLoop(new ScriptedBackend(turns), new ToolRegistry(new Confirm(true)).Add(new GetStateTool(new FakeHost())),
                                 new TokenBudget(16_384)) { MaxSteps = 3 };
        var events = new List<AgentEvent>();
        loop.RunTurnAsync("loop", events.Add, default).Wait();
        Assert.Contains(events, e => e is AgentNotice n && n.Text.Contains("3 steps"));
    }

    [Fact]
    public void Budget_Summarizes_WhenNearWindow()
    {
        var loop = new AgentLoop(new ScriptedBackend(), new ToolRegistry(new Confirm(true)), new TokenBudget(2_000));
        var events = new List<AgentEvent>();
        for (int i = 0; i < 8; i++) loop.RunTurnAsync("q" + i + new string('y', 600), events.Add, default).Wait();
        Assert.Contains(events, e => e is AgentNotice n && n.Text.Contains("summarized"));
        Assert.Contains(loop.History, m => m.Role == "system" && m.Content!.StartsWith("Summary of the earlier conversation"));
        Assert.True(loop.History.Count < 17);
    }

    [Fact]
    public void Budget_SessionLimit_Stops()
    {
        var b = new TokenBudget(8_192, sessionLimit: 100);
        b.Add(new Usage(90, 20), 0, 0);
        Assert.True(b.Exhausted);
        var loop = new AgentLoop(new ScriptedBackend(), new ToolRegistry(new Confirm(true)), b);
        var ev = new List<AgentEvent>();
        loop.RunTurnAsync("hi", ev.Add, default).Wait();
        Assert.Contains(ev, e => e is AgentNotice);
    }
}

sealed class Confirm(bool answer) : IConfirmer
{
    public ConfirmRequest? Last;
    public Task<bool> ConfirmAsync(ConfirmRequest r, CancellationToken ct) { Last = r; return Task.FromResult(answer); }
}

sealed class FakeSearch : ISearchProvider
{
    public string Name => "fake";
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string q, int n, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>>([new SearchHit("t", "https://x", "s")]);
}

sealed class FakeHost : IFrameFxHost
{
    public bool AntiCheat; public int Saves;
    public FrameFxState GetState() => new(false, null, [], "RTX 3060 Laptop");
    public ProfileSnapshot? GetProfile(string exe) => null;
    public ProfileSnapshot SaveProfile(ProfileSnapshot p) { Saves++; return p; }
    public bool IsAntiCheatGame(string exe) => AntiCheat;
    public (bool Ok, string Message) Start(string exe) => (true, "started");
    public (bool Ok, string Message) Stop() => (true, "stopped");
}

sealed class ScriptedBackend(params AssistantTurn[] turns) : IChatBackend
{
    int _i;
    public Task<AssistantTurn> CompleteAsync(IEnumerable<ChatMessage> m, System.Text.Json.Nodes.JsonArray? t, Action<string>? o,
        CancellationToken ct, double temperature = 0.4, int maxTokens = 1024)
    {
        var turn = _i < turns.Length ? turns[_i++] : new AssistantTurn("done", [], "stop", null);
        if (turn.Content.Length > 0) o?.Invoke(turn.Content);
        return Task.FromResult(turn);
    }
}
