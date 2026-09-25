using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace TypeSafeAppGen.Ai.Plugins;

/// <summary>Pencarian internet via Tavily dan pengambil isi halaman web sebagai teks bersih.</summary>
public sealed partial class WebPlugin(HttpClient http, Func<string> tavilyApiKey)
{
    private const int DefaultMaxChars = 12_000;

    [GeneratedRegex(@"<(script|style|noscript|svg|head|nav|footer)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoiseBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/pre)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [KernelFunction("search_internet"), Description("Search the internet with Tavily for current documentation, library versions, APIs, or facts. Returns a short answer plus sources.")]
    public async Task<string> SearchInternetAsync(
        [Description("Search query.")] string query,
        [Description("Number of results, 1-10.")] int maxResults = 5,
        CancellationToken ct = default)
    {
        var key = tavilyApiKey();
        if (string.IsNullOrWhiteSpace(key)) return "Internet search is not configured. Tell the user to add a Tavily API key in Settings → Tools.";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query, max_results = Math.Clamp(maxResults, 1, 10), include_answer = true, search_depth = "basic" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return $"Tavily search failed with HTTP {(int)response.StatusCode}{(response.StatusCode == HttpStatusCode.Unauthorized ? " — the API key is invalid" : "")}.";

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var builder = new StringBuilder();
        if (json.RootElement.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String)
            builder.AppendLine("Answer: " + answer.GetString()).AppendLine();
        if (json.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                builder.AppendLine($"- {Get(item, "title")}\n  {Get(item, "url")}\n  {Truncate(Get(item, "content"), 600)}");
            }
        }
        return builder.Length == 0 ? "No results." : builder.ToString();
    }

    [KernelFunction("scrape_web_page"), Description("Download a web page and return its readable text (scripts, styles, and markup removed). Use for reading documentation pages.")]
    public async Task<string> ScrapeWebPageAsync(
        [Description("Absolute http or https URL.")] string url,
        [Description("Maximum characters to return.")] int maxChars = DefaultMaxChars,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Only absolute http(s) URLs can be scraped.";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (TypeSafeAppGen; Jack) AppleWebKit/537.36");
        request.Headers.Accept.ParseAdd("text/html,text/plain,application/json;q=0.9,*/*;q=0.5");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode} while fetching {uri}.";

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("text") && !mediaType.Contains("json") && !mediaType.Contains("xml") && mediaType.Length > 0)
            return $"{uri} returned {mediaType}, which is not text.";

        var raw = await ReadLimitedAsync(response, 2_000_000, ct);
        var text = mediaType.Contains("html") || raw.TrimStart().StartsWith('<') ? HtmlToText(raw) : raw;
        var title = Title().Match(raw) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : uri.Host;
        return $"# {title}\n{uri}\n\n{Truncate(text, Math.Clamp(maxChars, 500, 60_000))}";
    }

    internal static string HtmlToText(string html)
    {
        var text = NoiseBlocks().Replace(html, " ");
        text = BlockBreaks().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length < maxBytes) buffer.Write(chunk, 0, read);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " …";
}
