using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
namespace TypeSafeSdk;
public sealed class TypeSafeClient : IAsyncDisposable
{
    private readonly HttpClient _http; private readonly TypeSafeOptions _options; private readonly ILogger? _logger; private readonly bool _ownsHttp;
    public ModelsClient Models => new(_http, _options);
    public TypeSafeClient(TypeSafeOptions? options = null, HttpClient? httpClient = null, ILogger<TypeSafeClient>? logger = null) { _options = options ?? TypeSafeOptions.FromEnvironment(); _http = httpClient ?? new HttpClient(); _ownsHttp = httpClient is null; _logger = logger; }
    public Task<SystemOneResponse> SystemOneAsync(object state, ChoiceSchema schema, CancellationToken cancellationToken = default) => SystemOneAsync(state, new Dictionary<string, object> { ["category"] = schema.ToApiQuestion() }, _options.DefaultModel, null, cancellationToken);
    public Task<SystemOneResponse> SystemOneAsync(object state, object questions, string? model, CancellationToken cancellationToken = default) => SystemOneAsync(state, questions, model, null, cancellationToken);
    public async Task<SystemOneResponse> SystemOneAsync(object state, object questions, string? model = null, RetryPolicy? retry = null, CancellationToken cancellationToken = default)
    {
        if (_options.Simulator) return TypeSafeSimulator.Classify(state); if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new TypeSafeApiException(0, "TypeSafe API key is required.");
        var endpoint = new Uri(new Uri(_options.Endpoint.TrimEnd('/') + "/"), "v1/systemone"); var payload = questions is QuestionsSchema typed ? typed.Values : questions;
        for (var attempt = 0; ; attempt++) { using var request = new HttpRequestMessage(HttpMethod.Post, endpoint); request.Headers.Authorization = new("Bearer", _options.ApiKey); request.Headers.Accept.ParseAdd("application/json"); request.Headers.Add("X-TypeSafe-SDK", "typesafe-sdk-dotnet/1.0.0"); request.Content = JsonContent.Create(new { state, model = model ?? _options.DefaultModel, questions = payload }); _logger?.LogInformation("Calling TypeSafe System One endpoint {Endpoint}", endpoint); using var response = await _http.SendAsync(request, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken); if (response.IsSuccessStatusCode) return SystemOneResponse.Parse(body); if (retry is null || attempt >= retry.MaxRetries || response.StatusCode is not (System.Net.HttpStatusCode.TooManyRequests or (System.Net.HttpStatusCode)529)) throw new TypeSafeApiException((int)response.StatusCode, body); var delay = TimeSpan.FromMilliseconds(Math.Min(retry.EffectiveMaxDelay.TotalMilliseconds, retry.EffectiveDelay.TotalMilliseconds * Math.Pow(2, attempt))); await Task.Delay(delay, cancellationToken); }
    }
    public ValueTask DisposeAsync() { if (_ownsHttp) _http.Dispose(); return ValueTask.CompletedTask; }
}
public sealed class TypeSafeOptions { public string ApiKey { get; set; } = ""; public string Endpoint { get; set; } = "https://api.typesafe.ai"; public string DefaultModel { get; set; } = "jev-latest"; public bool Simulator { get; set; } public static TypeSafeOptions FromEnvironment() => new() { ApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? "", Endpoint = Environment.GetEnvironmentVariable("TYPESAFE_BASE_URL") ?? "https://api.typesafe.ai", DefaultModel = Environment.GetEnvironmentVariable("TYPESAFE_DEFAULT_MODEL") ?? "jev-latest", Simulator = Environment.GetEnvironmentVariable("TYPESAFE_SIMULATOR") == "true" }; }
public sealed class TypeSafeApiException(int statusCode, string message) : Exception(message) { public int StatusCode { get; } = statusCode; }
internal static class JsonDefaults { public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web); }
