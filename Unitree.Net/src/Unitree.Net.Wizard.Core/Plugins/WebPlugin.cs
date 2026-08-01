using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace Unitree.Net.Wizard.Core.Plugins;

/// <summary>
/// Search, page fetching and file reading over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// These are what let Jack look something up rather than guess. A model asked about an API it half
/// remembers will confabulate a plausible signature; the same model with a fetch tool will read the
/// page.
/// </para>
/// <para>
/// Everything is capped and truncated. An unbounded fetch of an arbitrary URL is the easiest way to
/// blow a context window, and the tail of a long page is rarely the part that answers the question.
/// </para>
/// </remarks>
public sealed partial class WebPlugin : IDisposable
{
    private const int MaxCharacters = 60_000;

    private readonly HttpClient _client;
    private readonly string _tavilyApiKey;
    private bool _disposed;

    /// <summary>Creates the plugin.</summary>
    /// <param name="tavilyApiKey">Tavily API key. Empty disables search, which then says so.</param>
    /// <param name="client">HTTP client to use. One is created if null.</param>
    public WebPlugin(string tavilyApiKey, HttpClient? client = null)
    {
        _tavilyApiKey = tavilyApiKey ?? string.Empty;
        _client = client ?? new HttpClient();

        if (_client.Timeout == TimeSpan.FromSeconds(100))
        {
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        // Some sites serve a challenge page to clients without a user agent, which then reads as an
        // empty article rather than as a block.
        if (!_client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _client.DefaultRequestHeaders.Add("User-Agent", "UnitreeRobotWizard/1.0 (+Jack The Code Bender)");
        }
    }

    /// <summary>Searches the web through Tavily.</summary>
    /// <param name="query">What to search for.</param>
    /// <param name="maxResults">How many results to return.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [KernelFunction("search_web")]
    [Description(
        "Searches the internet and returns titles, URLs and short extracts. Use this for anything " +
        "current, or to confirm an API or product detail you are not certain about.")]
    public async Task<string> SearchWebAsync(
        [Description("The search query.")] string query,
        [Description("How many results to return, 1 to 10.")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_tavilyApiKey))
        {
            return "Web search is not configured. Add a Tavily API key in Settings to enable it. " +
                   "You can still use fetch_page if you have a URL.";
        }

        var request = new
        {
            api_key = _tavilyApiKey,
            query,
            max_results = Math.Clamp(maxResults, 1, 10),
            search_depth = "basic",
            include_answer = true,
        };

        try
        {
            HttpResponseMessage response = await _client
                .PostAsJsonAsync("https://api.tavily.com/search", request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return $"Search failed: Tavily returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            using JsonDocument document = JsonDocument
                .Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var text = new StringBuilder();

            if (document.RootElement.TryGetProperty("answer", out JsonElement answer)
                && answer.ValueKind == JsonValueKind.String
                && answer.GetString() is { Length: > 0 } summary)
            {
                text.AppendLine($"Summary: {summary}").AppendLine();
            }

            if (document.RootElement.TryGetProperty("results", out JsonElement results))
            {
                int index = 0;

                foreach (JsonElement result in results.EnumerateArray())
                {
                    text.AppendLine($"[{++index}] {Text(result, "title")}");
                    text.AppendLine($"    {Text(result, "url")}");
                    text.AppendLine($"    {Truncate(Text(result, "content"), 400)}");
                    text.AppendLine();
                }

                if (index == 0)
                {
                    return $"No results for '{query}'.";
                }
            }

            return text.ToString();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"Search failed: {exception.Message}";
        }
    }

    /// <summary>Fetches a web page and returns its readable text.</summary>
    /// <param name="url">The page to fetch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [KernelFunction("fetch_page")]
    [Description(
        "Fetches a web page and returns its text with the markup stripped. Use this to read " +
        "documentation, a GitHub file, or anything a search result pointed at.")]
    public async Task<string> FetchPageAsync(
        [Description("Absolute http or https URL.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return $"'{url}' is not an absolute http or https URL.";
        }

        try
        {
            HttpResponseMessage response = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return $"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";

            // Only HTML needs stripping. Running the tag remover over JSON or source code would
            // silently delete anything between angle brackets — generics, for instance.
            string text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? StripHtml(body)
                : body;

            return $"Source: {uri}\nContent type: {mediaType}\n\n{Truncate(text, MaxCharacters)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"Could not fetch {uri}: {exception.Message}";
        }
    }

    /// <summary>Downloads a file and returns its text.</summary>
    /// <param name="url">The file to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [KernelFunction("read_file_from_url")]
    [Description(
        "Downloads a text file — source code, JSON, CSV, Markdown — and returns it verbatim, with " +
        "no markup stripping. Use this rather than fetch_page when the URL points at a file.")]
    public async Task<string> ReadFileFromUrlAsync(
        [Description("Absolute http or https URL of a text file.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return $"'{url}' is not an absolute http or https URL.";
        }

        try
        {
            byte[] bytes = await _client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);

            if (bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).IndexOf((byte)0) >= 0)
            {
                return $"{uri} is binary ({bytes.Length:N0} bytes), not text.";
            }

            return $"Source: {uri}\n\n{Truncate(Encoding.UTF8.GetString(bytes), MaxCharacters)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"Could not read {uri}: {exception.Message}";
        }
    }

    /// <summary>
    /// Reduces an HTML document to its readable text.
    /// </summary>
    /// <remarks>
    /// Script and style bodies are removed first. Stripping tags without doing that leaves the entire
    /// contents of every inline script in the output, which is usually larger than the article.
    /// </remarks>
    private static string StripHtml(string html)
    {
        string text = ScriptOrStyle().Replace(html, " ");
        text = Comment().Replace(text, " ");

        // Block-level tags become newlines so paragraph structure survives, which is most of what
        // makes the result readable rather than a wall.
        text = BlockTag().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);

        text = HorizontalSpace().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");

        return text.Trim();
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n\n… truncated, {text.Length - limit:N0} more characters.";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"(\s*\n){3,}")]
    private static partial Regex BlankLines();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }
}
