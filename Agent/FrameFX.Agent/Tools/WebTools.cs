using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UniversalFrameFX.Agent.Tools;

public sealed record SearchHit(string Title, string Url, string Snippet);

public interface ISearchProvider
{
    string Name { get; }
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct);
}

/// <summary>Free default: DuckDuckGo's HTML endpoint (no key). Layout can change; parsing is tolerant and fails soft.</summary>
public sealed partial class DuckDuckGoHtmlProvider(HttpClient http) : ISearchProvider
{
    public string Name => "duckduckgo";
    [GeneratedRegex("<a[^>]+class=\"result__a\"[^>]+href=\"(?<u>[^\"]+)\"[^>]*>(?<t>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex Result();
    [GeneratedRegex("class=\"result__snippet\"[^>]*>(?<s>.*?)</(?:a|td|div)>", RegexOptions.Singleline)]
    private static partial Regex Snippet();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
        { Content = new FormUrlEncodedContent([new("q", query)]) };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FrameFX-Assistant");
        using var r = await http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return Parse(await r.Content.ReadAsStringAsync(ct), count);
    }

    internal static IReadOnlyList<SearchHit> Parse(string html, int count)
    {
        var hits = new List<SearchHit>();
        var links = Result().Matches(html);
        for (int k = 0; k < links.Count; k++)
        {
            var m = links[k];
            int end = k + 1 < links.Count ? links[k + 1].Index : html.Length;
            var sm = Snippet().Match(html, m.Index + m.Length, end - m.Index - m.Length);
            string url = WebUtility.HtmlDecode(m.Groups["u"].Value);
            // DDG wraps links as //duckduckgo.com/l/?uddg=<encoded>
            int i = url.IndexOf("uddg=", StringComparison.Ordinal);
            if (i >= 0) { string enc = url[(i + 5)..]; int amp = enc.IndexOf('&'); url = Uri.UnescapeDataString(amp >= 0 ? enc[..amp] : enc); }
            if (url.StartsWith("//")) url = "https:" + url;
            hits.Add(new SearchHit(Html.ToText(m.Groups["t"].Value), url, sm.Success ? Html.ToText(sm.Groups["s"].Value) : ""));
            if (hits.Count >= count) break;
        }
        return hits;
    }
}

/// <summary>SearXNG instance (self-hosted or public) with JSON output enabled: {base}/search?q=..&amp;format=json</summary>
public sealed class SearxngProvider(HttpClient http, string baseUrl) : ISearchProvider
{
    public string Name => "searxng";
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var u = $"{baseUrl.TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(u, ct));
        return doc.RootElement.GetProperty("results").EnumerateArray().Take(count).Select(e => new SearchHit(
            e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            e.TryGetProperty("url", out var l) ? l.GetString() ?? "" : "",
            e.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")).ToList();
    }
}

/// <summary>Brave Search API (optional key, user-supplied).</summary>
public sealed class BraveProvider(HttpClient http, string apiKey) : ISearchProvider
{
    public string Name => "brave";
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?count={count}&q={Uri.EscapeDataString(query)}");
        req.Headers.Add("X-Subscription-Token", apiKey);
        req.Headers.Add("Accept", "application/json");
        using var r = await http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("web", out var web)) return [];
        return web.GetProperty("results").EnumerateArray().Take(count).Select(e => new SearchHit(
            e.GetProperty("title").GetString() ?? "", e.GetProperty("url").GetString() ?? "",
            e.TryGetProperty("description", out var d) ? Html.ToText(d.GetString() ?? "") : "")).ToList();
    }
}

/// <summary>Tavily API (optional key).</summary>
public sealed class TavilyProvider(HttpClient http, string apiKey) : ISearchProvider
{
    public string Name => "tavily";
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var body = new JsonObject { ["api_key"] = apiKey, ["query"] = query, ["max_results"] = count };
        using var r = await http.PostAsync("https://api.tavily.com/search",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(e => new SearchHit(
            e.GetProperty("title").GetString() ?? "", e.GetProperty("url").GetString() ?? "",
            e.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")).ToList();
    }
}

public sealed class WebSearchTool(ISearchProvider provider) : IAgentTool
{
    public string Name => "web_search";
    public string Description => "Search the internet. Returns titles, URLs and snippets. Use fetch_page to read a result.";
    public JsonObject Parameters { get; } = Args.Schema(("query", "string", "search terms", true), ("count", "integer", "1-10, default 5", false));
    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct) { Args.Str(a, "query"); return Task.FromResult<ConfirmRequest?>(null); }
    public async Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        int n = Math.Clamp(Args.Int(a, "count") ?? 5, 1, 10);
        var hits = await provider.SearchAsync(Args.Str(a, "query"), n, ct);
        if (hits.Count == 0) return ToolResult.Success("No results.");
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++) sb.AppendLine($"{i + 1}. {hits[i].Title}\n   {hits[i].Url}\n   {hits[i].Snippet}");
        return ToolResult.Success(sb.ToString());
    }
}

/// <summary>Reads a public web page as plain text. http/https only; refuses localhost, private and link-local
/// addresses (so web content can't make the agent poke the LAN or the local llama-server).</summary>
public sealed class FetchPageTool(HttpClient http) : IAgentTool
{
    public string Name => "fetch_page";
    public string Description => "Download a public web page and return its readable text (max ~12k characters).";
    public JsonObject Parameters { get; } = Args.Schema(("url", "string", "http(s) URL", true));
    public int MaxBytes { get; init; } = 2_000_000;

    public Task<ConfirmRequest?> DescribeAsync(JsonElement a, CancellationToken ct)
    {
        CheckUrl(Args.Str(a, "url"));
        return Task.FromResult<ConfirmRequest?>(null);
    }

    internal static Uri CheckUrl(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException("only http(s) URLs are allowed");
        if (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("local addresses are not allowed");
        if (IPAddress.TryParse(u.Host, out var ip) && IsPrivate(ip)) throw new ArgumentException("private network addresses are not allowed");
        return u;
    }

    internal static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement a, CancellationToken ct)
    {
        var u = CheckUrl(Args.Str(a, "url"));
        foreach (var ip in await Dns.GetHostAddressesAsync(u.Host, ct))
            if (IsPrivate(ip)) return ToolResult.Fail("that host resolves to a private address");
        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FrameFX-Assistant");
        using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode) return ToolResult.Fail($"HTTP {(int)r.StatusCode}");
        var type = r.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html") && !type.StartsWith("text/") && !type.Contains("json")) return ToolResult.Fail($"not a text page ({type})");
        using var s = await r.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[81920]; int n;
        while ((n = await s.ReadAsync(chunk, ct)) > 0 && buf.Length < MaxBytes) buf.Write(chunk, 0, n);
        string raw = Encoding.UTF8.GetString(buf.ToArray());
        return ToolResult.Success($"[{u}]\n" + (type.Contains("html") ? Html.ToText(raw) : raw));
    }
}

public static partial class Html
{
    [GeneratedRegex(@"<(script|style|noscript|svg|head)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Junk();
    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")] private static partial Regex Blank();

    public static string ToText(string html)
    {
        string s = Junk().Replace(html, " ");
        s = Breaks().Replace(s, "\n");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        return Blank().Replace(s, "\n\n").Trim();
    }
}
