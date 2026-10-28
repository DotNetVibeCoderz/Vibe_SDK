using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TypeSafeSdk;

/// <summary>Client TypeSafe untuk endpoint System One dan daftar model.</summary>
public sealed class TypeSafeClient : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _http;
    private readonly TypeSafeOptions _options;
    private readonly ILogger? _logger;
    private readonly bool _ownsHttp;
    private readonly ModelsClient _models;
    private readonly AttachedAssets _attached = new();
    private bool _disposed;

    /// <summary>Resource daftar model: <c>client.Models.ListAsync()</c>.</summary>
    public ModelsClient Models => _models;

    /// <summary>Opsi efektif yang dipakai client ini.</summary>
    public TypeSafeOptions Options => _options;

    public TypeSafeClient(TypeSafeOptions? options = null, HttpClient? httpClient = null, ILogger<TypeSafeClient>? logger = null)
    {
        _options = options ?? TypeSafeOptions.FromEnvironment();
        _http = httpClient ?? new HttpClient();
        _ownsHttp = httpClient is null;
        _logger = logger;
        _models = new ModelsClient(this);
    }

    /// <summary>Menanyakan satu pertanyaan <c>choice</c> bernama <c>category</c>.</summary>
    public Task<SystemOneResponse> SystemOneAsync(object state, ChoiceSchema schema, CancellationToken cancellationToken = default)
        => SystemOneAsync(state, new Dictionary<string, object> { ["category"] = schema }, _options.DefaultModel, null, null, null, null, cancellationToken);

    /// <summary>Menanyakan sekumpulan pertanyaan dengan model tertentu.</summary>
    public Task<SystemOneResponse> SystemOneAsync(object state, object questions, string? model, CancellationToken cancellationToken)
        => SystemOneAsync(state, questions, model, null, null, null, null, cancellationToken);

    /// <summary>
    /// Memanggil endpoint System One. <paramref name="questions"/> boleh berupa
    /// <see cref="ChoiceSchema"/>/<see cref="NoulSchema"/>/<see cref="ScoreSchema"/>, sebuah
    /// <see cref="QuestionsSchema"/>, atau dictionary mentah yang sudah berbentuk payload API.
    /// </summary>
    /// <param name="state">Konteks yang dinilai: string, objek, atau array.</param>
    /// <param name="questions">Pertanyaan bernama yang harus dijawab API.</param>
    /// <param name="model">Model yang dipakai; null memakai <see cref="TypeSafeOptions.DefaultModel"/>.</param>
    /// <param name="retry">Kebijakan retry; null memakai <see cref="TypeSafeOptions.Retry"/>.</param>
    /// <param name="timeout">Timeout per request; null memakai <see cref="TypeSafeOptions.Timeout"/>.</param>
    /// <param name="extraHeaders">Header tambahan khusus pemanggilan ini.</param>
    /// <param name="extraBody">Field tambahan yang digabungkan ke body request.</param>
    /// <param name="assets">Aset menurut nama yang dipakai untuknya di <c>state</c> atau pertanyaan, misalnya <c>"&lt;photo&gt;"</c>.</param>
    /// <param name="cancellationToken">Token pembatalan pemanggilan.</param>
    /// <exception cref="TypeSafeException">Request tidak valid sebelum dikirim, misalnya pertanyaan kosong atau aset yang tidak bisa dibaca.</exception>
    public async Task<SystemOneResponse> SystemOneAsync(
        object state,
        object questions,
        string? model = null,
        RetryPolicy? retry = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        IReadOnlyDictionary<string, object?>? extraBody = null,
        IReadOnlyDictionary<string, Asset>? assets = null,
        CancellationToken cancellationToken = default)
    {
        if (state is null) throw new TypeSafeException("State is required.");
        var normalized = QuestionPayload.Normalize(questions);
        QuestionPayload.Validate(normalized);
        if (_options.Simulator) return TypeSafeSimulator.Answer(state, normalized, model ?? _options.DefaultModel);
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new TypeSafeAuthenticationException(0, $"TypeSafe API key is required. Set {TypeSafeConstants.ApiKeyEnv} or TypeSafeOptions.ApiKey.", null, null, TypeSafeConstants.SystemOnePath);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["state"] = state, ["model"] = model ?? _options.DefaultModel, ["questions"] = normalized };
        if (extraBody is not null) foreach (var pair in extraBody) body[pair.Key] = pair.Value;
        var content = await SystemOneRequestBuilder.BuildAsync(body, assets, _attached.Snapshot(), cancellationToken);

        var (raw, responseHeaders) = await SendAsync(HttpMethod.Post, TypeSafeConstants.SystemOnePath, content, retry, timeout, extraHeaders, cancellationToken);
        try { return SystemOneResponse.Parse(raw) with { RequestId = responseHeaders.GetValueOrDefault(TypeSafeConstants.RequestIdHeader) }; }
        catch (JsonException ex) { throw new TypeSafeApiResponseValidationException("answers", raw, ex); }
    }

    /// <summary>Overload 1.1 tanpa <c>assets</c>, dipertahankan untuk kompatibilitas biner.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task<SystemOneResponse> SystemOneAsync(object state, object questions, string? model, RetryPolicy? retry, TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? extraHeaders, IReadOnlyDictionary<string, object?>? extraBody, CancellationToken cancellationToken)
        => SystemOneAsync(state, questions, model, retry, timeout, extraHeaders, extraBody, null, cancellationToken);

    /// <summary>
    /// Memasang aset pada pemanggilan client ini sampai hasilnya di-dispose, agar aset bisa dirujuk di dalam teks:
    /// setiap aset terpasang yang rujukannya (<c>asset.ToString()</c>) muncul di request ikut dikirim.
    /// </summary>
    /// <remarks>Rujukan ke aset yang tidak dipasang, tidak dilewatkan lewat <c>assets</c>, dan tidak diletakkan langsung di isi request dikirim sebagai teks biasa.</remarks>
    /// <example><code>
    /// var photo = ImageAsset.FromFile("product.jpg");
    /// using (client.AttachAssets(photo))
    ///     await client.SystemOneAsync($"Product: {photo}", new Dictionary&lt;string, object&gt; { ["damaged"] = new NoulSchema("Is the product damaged?") });
    /// </code></example>
    public IDisposable AttachAssets(params Asset[] assets) => _attached.Attach(assets);

    /// <inheritdoc cref="AttachAssets(Asset[])"/>
    public IDisposable AttachAssets(IEnumerable<Asset> assets) => _attached.Attach(assets);

    /// <summary>Mengirim request dengan retry, timeout, dan pemetaan error terpusat.</summary>
    internal async Task<(string Body, IReadOnlyDictionary<string, string> Headers)> SendAsync(
        HttpMethod method,
        string path,
        RequestBody? body,
        RetryPolicy? retry,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var policy = retry ?? _options.Retry;
        var effectiveTimeout = timeout ?? _options.Timeout;
        var endpoint = new Uri(new Uri(_options.Endpoint.TrimEnd('/') + "/"), path);
        var budget = Stopwatch.StartNew();
        // Body yang cukup besar dikompres sekali untuk semua percobaan.
        var encoding = body is not null && body.Content.Length >= TypeSafeConstants.MinCompressedBodyLength ? _options.Compression : RequestCompression.None;
        var content = encoding == RequestCompression.Zstd ? ZstdCompress(body!.Content) : body?.Content;

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, endpoint);
            request.Headers.Authorization = new("Bearer", _options.ApiKey);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation(TypeSafeConstants.SdkHeader, TypeSafeConstants.SdkHeaderValue);
            request.Headers.TryAddWithoutValidation(TypeSafeConstants.RuntimeHeader, TypeSafeConstants.RuntimeHeaderValue);
            if (attempt > 0) request.Headers.TryAddWithoutValidation(TypeSafeConstants.RetryCountHeader, attempt.ToString());
            foreach (var header in _options.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (extraHeaders is not null) foreach (var header in extraHeaders) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(content!);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
                if (encoding == RequestCompression.Zstd) request.Content.Headers.ContentEncoding.Add("zstd");
            }

            _logger?.LogInformation("TypeSafe {Method} {Endpoint} (attempt {Attempt})", method.Method, endpoint, attempt + 1);

            HttpResponseMessage? response = null;
            string payload;
            IReadOnlyDictionary<string, string> headers;
            int status;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (effectiveTimeout > TimeSpan.Zero) timeoutSource.CancelAfter(effectiveTimeout);
            try
            {
                response = await _http.SendAsync(request, timeoutSource.Token);
                payload = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                headers = ReadHeaders(response);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return (payload, headers);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var timeoutError = new TypeSafeApiTimeoutException(effectiveTimeout, ex);
                if (!policy.RetryTimeoutErrors || !CanRetry(policy, attempt, budget, null)) throw timeoutError;
                await DelayAsync(policy, attempt, null, cancellationToken); continue;
            }
            catch (HttpRequestException ex)
            {
                var connectionError = new TypeSafeApiConnectionException($"TypeSafe request to {endpoint} failed: {ex.Message}", ex);
                if (!policy.RetryConnectionErrors || !CanRetry(policy, attempt, budget, null)) throw connectionError;
                await DelayAsync(policy, attempt, null, cancellationToken); continue;
            }
            finally { response?.Dispose(); }

            var retryAfter = TypeSafeRateLimitException.ReadRetryAfter(headers);
            if (policy.ShouldRetryStatus(status) && CanRetry(policy, attempt, budget, retryAfter))
            {
                _logger?.LogWarning("TypeSafe returned HTTP {Status}; retrying (attempt {Attempt} of {Max})", status, attempt + 1, policy.MaxRetries);
                await DelayAsync(policy, attempt, retryAfter, cancellationToken);
                continue;
            }
            throw TypeSafeErrorFactory.Create(status, payload, headers, endpoint.AbsoluteUri);
        }
    }

    private static byte[] ZstdCompress(byte[] content)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(content).ToArray();
    }

    private bool CanRetry(RetryPolicy policy, int attempt, Stopwatch budget, TimeSpan? retryAfter)
    {
        if (attempt >= policy.MaxRetries) return false;
        var delay = policy.NextDelay(attempt, retryAfter);
        return budget.Elapsed + delay < policy.EffectiveBudget;
    }

    private static Task DelayAsync(RetryPolicy policy, int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = policy.NextDelay(attempt, retryAfter);
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    /// <summary>Melepas HttpClient internal bila client ini yang membuatnya.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; if (_ownsHttp) _http.Dispose(); }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Konfigurasi client TypeSafe.</summary>
public sealed class TypeSafeOptions
{
    /// <summary>API key bearer. Kosong hanya valid dalam mode simulator.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Base URL API.</summary>
    public string Endpoint { get; set; } = TypeSafeConstants.DefaultBaseUrl;
    /// <summary>Model default untuk setiap pemanggilan.</summary>
    public string DefaultModel { get; set; } = TypeSafeConstants.DefaultModel;
    /// <summary>Jalankan seluruh pemanggilan secara lokal tanpa menyentuh jaringan.</summary>
    public bool Simulator { get; set; }
    /// <summary>Timeout per request. Nol atau negatif menonaktifkan timeout SDK.</summary>
    public TimeSpan Timeout { get; set; } = TypeSafeConstants.DefaultTimeout;
    /// <summary>Kebijakan retry default untuk seluruh pemanggilan client ini.</summary>
    public RetryPolicy Retry { get; set; } = new();
    /// <summary>Header tambahan yang dikirim pada setiap request.</summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Level log yang dibaca dari <c>TYPESAFE_LOG_LEVEL</c>, bila diisi.</summary>
    public LogLevel? LogLevel { get; set; }
    /// <summary>
    /// Kompresi body request. Default <see cref="RequestCompression.Zstd"/>, sama dengan Python SDK; pakai
    /// <see cref="RequestCompression.None"/> untuk gateway atau API tidak resmi yang belum mendukung zstd.
    /// </summary>
    public RequestCompression Compression { get; set; } = RequestCompression.Zstd;

    /// <summary>Membangun opsi dari environment variable standar TypeSafe.</summary>
    public static TypeSafeOptions FromEnvironment() => new()
    {
        ApiKey = Environment.GetEnvironmentVariable(TypeSafeConstants.ApiKeyEnv) ?? "",
        Endpoint = Environment.GetEnvironmentVariable(TypeSafeConstants.BaseUrlEnv) ?? TypeSafeConstants.DefaultBaseUrl,
        DefaultModel = Environment.GetEnvironmentVariable(TypeSafeConstants.DefaultModelEnv) ?? TypeSafeConstants.DefaultModel,
        Simulator = string.Equals(Environment.GetEnvironmentVariable(TypeSafeConstants.SimulatorEnv), "true", StringComparison.OrdinalIgnoreCase),
        LogLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable(TypeSafeConstants.LogLevelEnv), ignoreCase: true, out var level) ? level : null
    };
}

/// <summary>Kompresi body request, dikirim lewat header <c>Content-Encoding</c>.</summary>
public enum RequestCompression
{
    /// <summary>Body dikirim apa adanya.</summary>
    None,
    /// <summary>Body minimal <see cref="TypeSafeConstants.MinCompressedBodyLength"/> byte dikompres dengan zstd.</summary>
    Zstd
}

internal static class JsonDefaults
{
    // JSON tidak punya token NaN/Infinity; seperti Python SDK, keduanya dikirim sebagai string "NaN"/"Infinity"/"-Infinity".
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
