using System.Net;
using System.Text;
using System.Text.Json;
using UniversalFrameFX.Agent;
using UniversalFrameFX.Agent.Cloud;
using UniversalFrameFX.Agent.Tools;
using Xunit;

namespace FrameFX.Agent.Tests;

public class LarkquillTests
{
    sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            Seen.Add((r, body));
            return f(r, body);
        }
    }

    static HttpResponseMessage Sse(params string[] lines) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "text/event-stream") };
    static string Chunk(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } });
    static HttpResponseMessage Err(HttpStatusCode code, object error) => new(code)
    { Content = new StringContent(JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json") };

    sealed class Yes : IConfirmer { public int Asked; public Task<bool> ConfirmAsync(ConfirmRequest r, CancellationToken ct) { Asked++; return Task.FromResult(true); } }

    sealed class Host : IFrameFxHost
    {
        public ProfileSnapshot? Saved;
        public FrameFxState GetState() => new(false, null, [], "RTX 3060 Laptop");
        public ProfileSnapshot? GetProfile(string exe) => Saved;
        public ProfileSnapshot SaveProfile(ProfileSnapshot p) => Saved = p;
        public bool IsAntiCheatGame(string exe) => exe.Contains("valorant", StringComparison.OrdinalIgnoreCase);
        public (bool Ok, string Message) Start(string exe) => (true, "Started.");
        public (bool Ok, string Message) Stop() => (true, "Stopped.");
    }

    sealed class Presets : IPresetEngine
    {
        public PresetProposal Turn(string request) => new("Here's a preset for Cyberpunk.", new ProfileSnapshot("cyberpunk2077.exe", 1, true, 2, 3, false, false, 0, 0), null);
        public void Reset() { }
    }

    [Fact]
    public void ModelsAndPlans()
    {
        Assert.Equal(["quill-wren", "quill-scribe", "quill-sage", "quill-sovereign"], Larkquill.Models.Select(m => m.Id));
        Assert.Equal("quill-sage", Larkquill.ById("larkquill-aria-1.5").Id);
        Assert.True(Larkquill.Unlocked(Larkquill.ById("larkquill-trill-1"), LarkquillPlan.Hobby));
        Assert.False(Larkquill.Unlocked(Larkquill.ById("larkquill-aria-1.5"), LarkquillPlan.Hobby));
        Assert.True(Larkquill.Unlocked(Larkquill.ById("larkquill-aria-1.5"), LarkquillPlan.Pro));
        Assert.False(Larkquill.Unlocked(Larkquill.ById("larkquill-chorus-0.7"), LarkquillPlan.Pro));
        Assert.True(Larkquill.Unlocked(Larkquill.ById("larkquill-chorus-0.7"), LarkquillPlan.Max));
        Assert.Equal("quill-scribe", Larkquill.ById("nope").Id);
    }

    [Fact]
    public void Branding_NoOldNames()
    {
        foreach (var s in new[] { Larkquill.SystemPrompt, AgentLoop.SystemPrompt })
        {
            Assert.Contains("LarkquillAI", s);
            Assert.DoesNotContain("Wrenby", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WyneAI", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Qwen", s, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Client_SendsKeyModelAndTools_AndStreams()
    {
        var h = new FakeHandler((_, _) => Sse(Chunk("Hi "), Chunk("there"), "data: [DONE]"));
        var c = new LarkquillClient("lq_test_key_123456", Larkquill.ById("larkquill-trill-1"), h);
        var got = new StringBuilder();
        var t = await c.CompleteAsync([ChatMessage.User("hello")], new System.Text.Json.Nodes.JsonArray(), d => got.Append(d), default);
        Assert.Equal("Hi there", t.Content);
        Assert.Equal("Hi there", got.ToString());
        var (req, body) = h.Seen.Single();
        Assert.Equal("https://larkquillai.vercel.app/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("lq_test_key_123456", req.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("quill-scribe", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Client_403_ParsesPlanAndUpgradeUrl()
    {
        var h = new FakeHandler((_, _) => Err(HttpStatusCode.Forbidden, new
        {
            message = "quill-sage needs LarkquillAI Pro; this key's account is on Hobby.", type = "permission_error",
            code = "plan_required", plan = "hobby", plan_needed = "pro", upgrade_url = "https://ilikemacos.github.io/larkquillai/plans.html",
        }));
        var c = new LarkquillClient("lq_test_key_123456", Larkquill.ById("larkquill-aria-1.5"), h);
        var ex = await Assert.ThrowsAsync<LarkquillApiException>(() => c.CompleteAsync([ChatMessage.User("hi")], null, null, default));
        Assert.True(ex.IsPlanRequired);
        Assert.Equal(LarkquillPlan.Hobby, ex.Plan);
        Assert.Equal(LarkquillPlan.Pro, ex.PlanNeeded);
        Assert.Equal("https://ilikemacos.github.io/larkquillai/plans.html", ex.UpgradeUrl);
    }

    [Fact]
    public void UpgradeUrl_OnlyTrustedHosts()
    {
        var ex = LarkquillApiException.FromResponse(403, "{\"error\":{\"message\":\"x\",\"code\":\"plan_required\",\"upgrade_url\":\"https://evil.example/pay\"}}");
        Assert.Equal(Larkquill.UpgradeUrl, ex.UpgradeUrl);
        var ex2 = LarkquillApiException.FromResponse(403, "not json");
        Assert.Equal(Larkquill.UpgradeUrl, ex2.UpgradeUrl);
        Assert.Null(LarkquillApiException.FromResponse(401, "{}").UpgradeUrl);
    }

    LarkquillSession Session(FakeHandler? h, string? key, out Host host, out Yes yes)
    {
        host = new Host(); yes = new Yes();
        return new LarkquillSession(yes, host, new Presets(), () => key, () => h);
    }

    [Fact]
    public void Session_ExposesOnlyFrameFxTools()
    {
        var s = Session(null, null, out _, out _);
        Assert.Equal(new[] { "apply_preset", "get_framefx_state", "make_preset", "set_setting", "start_framefx", "stop_framefx" }, s.ToolNames.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("launch_app", s.ToolNames);
        Assert.DoesNotContain("web_search", s.ToolNames);
    }

    [Fact]
    public async Task Session_NoKey_UsesOfflinePresetModel_AndApplyConfirms()
    {
        var s = Session(null, null, out var host, out var yes);
        var ev = new List<AgentEvent>();
        await s.RunTurnAsync("Cyberpunk on my 3060", ev.Add, default);
        Assert.Contains(ev, e => e is KeyNeeded);
        Assert.Contains(ev, e => e is UsedOffline);
        Assert.Contains("Say \"apply\"", string.Concat(ev.OfType<TextDelta>().Select(d => d.Text)));
        Assert.Null(host.Saved);

        ev.Clear();
        await s.RunTurnAsync("apply", ev.Add, default);
        Assert.Equal(1, yes.Asked);
        Assert.NotNull(host.Saved);
        Assert.Equal("cyberpunk2077.exe", host.Saved!.Exe);
    }

    [Fact]
    public async Task Session_403_ShowsUpgrade_ThenSkipsRequestsForLockedModel()
    {
        var h = new FakeHandler((_, _) => Err(HttpStatusCode.Forbidden, new { message = "needs Pro", code = "plan_required", plan = "hobby", plan_needed = "pro" }));
        var s = Session(h, "lq_test_key_123456", out _, out _);
        s.Model = Larkquill.ById("larkquill-aria-1.5");
        var ev = new List<AgentEvent>();
        await s.RunTurnAsync("hi", ev.Add, default);
        var u = Assert.Single(ev.OfType<UpgradeNeeded>());
        Assert.Equal(Larkquill.UpgradeUrl, u.UpgradeUrl);
        Assert.Equal(LarkquillPlan.Hobby, s.KnownPlan);
        ev.Clear();
        await s.RunTurnAsync("hi again", ev.Add, default);
        Assert.Single(h.Seen); // second turn didn't call the API
        Assert.Single(ev.OfType<UpgradeNeeded>());
    }

    [Fact]
    public async Task Session_ServerDown_FallsBackOffline()
    {
        var h = new FakeHandler((_, _) => Err(HttpStatusCode.BadGateway, new { message = "Upstream error", code = "upstream_error" }));
        var s = Session(h, "lq_test_key_123456", out _, out _);
        var ev = new List<AgentEvent>();
        await s.RunTurnAsync("Cyberpunk on my 3060", ev.Add, default);
        Assert.Contains(ev, e => e is UsedOffline);
        Assert.Contains("Cyberpunk", string.Concat(ev.OfType<TextDelta>().Select(d => d.Text)));
    }

    [Fact]
    public async Task Session_BadKey_AsksForKey_AndAnswersOffline()
    {
        var h = new FakeHandler((_, _) => Err(HttpStatusCode.Unauthorized, new { message = "Invalid or revoked API key.", code = "invalid_api_key" }));
        var s = Session(h, "lq_bad_key_1234567", out _, out _);
        var ev = new List<AgentEvent>();
        await s.RunTurnAsync("hello", ev.Add, default);
        Assert.Contains(ev, e => e is KeyNeeded k && k.Message.Contains("Invalid"));
        Assert.Contains(ev, e => e is UsedOffline);
    }

    [Fact]
    public async Task Session_CloudToolCall_GoesThroughConfirm()
    {
        int n = 0;
        var h = new FakeHandler((_, _) => ++n switch
        {
            1 => Sse("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id = "c1", type = "function", function = new { name = "set_setting", arguments = "{\"exe\":\"valorant.exe\",\"key\":\"frame_gen\",\"value\":\"on\"}" } } } }, finish_reason = "tool_calls" } } }), "data: [DONE]"),
            _ => Sse(Chunk("Valorant uses anti-cheat, so frame generation stays off."), "data: [DONE]"),
        });
        var s = Session(h, "lq_test_key_123456", out var host, out var yes);
        var ev = new List<AgentEvent>();
        await s.RunTurnAsync("turn on frame gen in valorant", ev.Add, default);
        var fin = Assert.Single(ev.OfType<ToolFinished>());
        Assert.False(fin.Result.Ok);           // validator refused: anti-cheat
        Assert.Equal(0, yes.Asked);            // refused before even asking
        Assert.Null(host.Saved);
        Assert.Equal(2, h.Seen.Count);
    }
}
