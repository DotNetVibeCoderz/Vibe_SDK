using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace DepthAI.Wizard.Ai.Plugins;

/// <summary>
/// Akses internet untuk asisten: pencarian, pengambilan halaman, dan pembacaan berkas
/// dari URL.
/// </summary>
public sealed partial class WebPlugin(HttpClient httpClient, string tavilyApiKey = "")
{
    /// <summary>Batas karakter yang dikembalikan tiap fungsi, agar tidak menghabiskan jendela konteks.</summary>
    private const int MaxContentLength = 12_000;

    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    [KernelFunction("search_web")]
    [Description("Mencari informasi terkini di internet lewat Tavily. Pakai ini untuk hal yang "
        + "berubah seiring waktu: versi paket, rilis terbaru, dokumentasi, atau kabar terbaru.")]
    public async Task<string> SearchWebAsync(
        [Description("Kata kunci pencarian.")] string query,
        [Description("Jumlah hasil yang diinginkan, 1 sampai 10.")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tavilyApiKey))
        {
            return "Pencarian internet belum aktif: kunci Tavily belum diisi. "
                + "Set variabel lingkungan TAVILY_API_KEY atau isi Tools:TavilyApiKey di app.config.";
        }

        var request = new
        {
            api_key = tavilyApiKey,
            query,
            max_results = Math.Clamp(maxResults, 1, 10),
            search_depth = "basic",
            include_answer = true,
        };

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "https://api.tavily.com/search", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return $"Pencarian gagal: Tavily mengembalikan {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            var builder = new StringBuilder();

            if (payload?["answer"]?.GetValue<string>() is { Length: > 0 } answer)
            {
                builder.AppendLine("Ringkasan: " + answer).AppendLine();
            }

            foreach (var result in payload?["results"]?.AsArray() ?? [])
            {
                builder
                    .AppendLine($"### {result?["title"]?.GetValue<string>()}")
                    .AppendLine(result?["url"]?.GetValue<string>())
                    .AppendLine(Truncate(result?["content"]?.GetValue<string>() ?? string.Empty, 800))
                    .AppendLine();
            }

            return builder.Length == 0 ? "Tidak ada hasil." : Truncate(builder.ToString(), MaxContentLength);
        }
        catch (HttpRequestException ex)
        {
            return $"Pencarian gagal: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Pencarian gagal: waktu tunggu habis.";
        }
    }

    [KernelFunction("scrape_page")]
    [Description("Mengambil halaman web dan mengembalikan isi teksnya tanpa tag HTML. "
        + "Pakai ini untuk membaca dokumentasi atau artikel yang URL-nya sudah diketahui.")]
    public async Task<string> ScrapePageAsync(
        [Description("URL halaman lengkap dengan http:// atau https://.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(url, out var uri, out var error))
        {
            return error;
        }

        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"Tidak bisa mengambil {url}: {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return Truncate(HtmlToText(html), MaxContentLength);
        }
        catch (HttpRequestException ex)
        {
            return $"Tidak bisa mengambil {url}: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return $"Tidak bisa mengambil {url}: waktu tunggu habis.";
        }
    }

    [KernelFunction("read_file_from_url")]
    [Description("Membaca berkas teks dari URL — misalnya kode sumber, JSON, CSV, atau Markdown. "
        + "Berbeda dari scrape_page, isinya dikembalikan apa adanya tanpa dibersihkan dari HTML.")]
    public async Task<string> ReadFileFromUrlAsync(
        [Description("URL berkas.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(url, out var uri, out var error))
        {
            return error;
        }

        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"Tidak bisa membaca {url}: {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // Berkas biner tidak berguna sebagai teks dan hanya akan memenuhi konteks.
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || mediaType == "application/octet-stream")
            {
                var length = response.Content.Headers.ContentLength ?? 0;
                return $"{url} berisi data biner ({mediaType}, {length} byte), bukan teks.";
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return Truncate(content, MaxContentLength);
        }
        catch (HttpRequestException ex)
        {
            return $"Tidak bisa membaca {url}: {ex.Message}";
        }
    }

    /// <summary>
    /// Menolak skema selain HTTP/HTTPS. Tanpa ini, <c>file://</c> akan mengubah fungsi
    /// pengambil halaman menjadi pembaca berkas lokal sembarang.
    /// </summary>
    private static bool TryValidateUrl(string url, out Uri uri, out string error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            uri = null!;
            error = $"'{url}' bukan URL yang valid.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            uri = null!;
            error = $"Hanya URL http dan https yang didukung, bukan '{parsed.Scheme}'.";
            return false;
        }

        uri = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>Membuang markup dan menyisakan teks yang bisa dibaca.</summary>
    private static string HtmlToText(string html)
    {
        // Script dan style dibuang lebih dulu; isinya bukan teks yang bisa dibaca
        // tapi tetap berada di antara tag.
        var text = ScriptOrStyle().Replace(html, " ");
        text = Tags().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength
            ? text
            : text[..maxLength] + $"\n\n[dipotong pada {maxLength} karakter dari total {text.Length}]";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
