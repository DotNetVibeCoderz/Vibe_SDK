using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
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
    /// <param name="cancellationToken">Token pembatalan pemanggilan.</param>
    public async Task<SystemOneResponse> SystemOneAsync(
        object state,
        object questions,
        string? model = null,
        RetryPolicy? retry = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        IReadOnlyDictionary<string, object?>? extraBody = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = QuestionPayload.Normalize(questions);
        if (_options.Simulator) return TypeSafeSimulator.Answer(state, normalized, model ?? _options.DefaultModel);
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new TypeSafeAuthenticationException(0, $"TypeSafe API key is required. Set {TypeSafeConstants.ApiKeyEnv} or TypeSafeOptions.ApiKey.", null, null, TypeSafeConstants.SystemOnePath);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["state"] = state, ["model"] = model ?? _options.DefaultModel, ["questions"] = normalized };
        if (extraBody is not null) foreach (var pair in extraBody) body[pair.Key] = pair.Value;

        var (raw, responseHeaders) = await SendAsync(HttpMethod.Post, TypeSafeConstants.SystemOnePath, body, retry, timeout, extraHeaders, cancellationToken);
        try { return SystemOneResponse.Parse(raw) with { RequestId = responseHeaders.GetValueOrDefault(TypeSafeConstants.RequestIdHeader) }; }
        catch (JsonException ex) { throw new TypeSafeApiResponseValidationException("answers", raw, ex); }
    }

    /// <summary>Mengirim request dengan retry, timeout, dan pemetaan error terpusat.</summary>
    internal async Task<(string Body, IReadOnlyDictionary<string, string> Headers)> SendAsync(
        HttpMethod method,
        string path,
        object? body,
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
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonDefaults.Options);

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

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
