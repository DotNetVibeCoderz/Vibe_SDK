using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Transport;

/// <summary>
/// A thin JSON-over-HTTP helper for the daemon REST surfaces.
/// </summary>
/// <remarks>
/// It exists to translate transport failures into <see cref="RobotConnectionException"/> and
/// non-success statuses into <see cref="RobotCommandException"/>, so that callers never have to
/// decide whether an <see cref="HttpRequestException"/> meant "robot is off" or "you asked for
/// something silly". Those two need different messages in a UI.
/// </remarks>
public sealed class HttpJsonClient : IDisposable
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>The base address every path is resolved against.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Creates a client for <paramref name="baseAddress"/>.</summary>
    public HttpJsonClient(Uri baseAddress, TimeSpan? timeout = null, HttpClient? httpClient = null)
    {
        BaseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= baseAddress;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>GETs a path and parses the body as JSON.</summary>
    public async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GETs a path and deserialises the body.</summary>
    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(WireOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POSTs an optional JSON body and parses the reply.</summary>
    public async Task<JsonNode?> PostAsync(string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        HttpContent? content = body is null ? null : JsonContent.Create(body, options: WireOptions);
        HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, content, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches a path as raw bytes - camera frames and audio, which are not JSON.</summary>
    public async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the daemon answers at all, used for the "is the robot there" check.</summary>
    public async Task<bool> PingAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, path));
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path)) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RobotConnectionException($"{method} {path} timed out after {_http.Timeout.TotalSeconds:0.#}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RobotConnectionException($"{method} {path} could not reach {BaseAddress}. Is the daemon running?", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();

        // A 404 on a daemon that is otherwise answering means this build predates the endpoint,
        // which callers handle by degrading rather than failing.
        throw response.StatusCode == HttpStatusCode.NotFound
            ? new RobotCommandException($"The daemon has no endpoint '{path}'. It may be older than this SDK.", path)
            : new RobotCommandException($"{method} {path} returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}", path);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

    /// <summary>Releases the underlying client when this instance created it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
