using System.Text.Json;

namespace TypeSafeSdk;

/// <summary>Resource untuk mengambil daftar model TypeSafe.</summary>
public sealed class ModelsClient
{
    private readonly HttpClient _http; private readonly TypeSafeOptions _options;
    internal ModelsClient(HttpClient http, TypeSafeOptions options) { _http = http; _options = options; }
    public async Task<IReadOnlyList<ModelMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.Endpoint.TrimEnd('/') + "/"), "v1/models"));
        request.Headers.Authorization = new("Bearer", _options.ApiKey);
        using var response = await _http.SendAsync(request, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new TypeSafeApiException((int)response.StatusCode, body);
        return JsonSerializer.Deserialize<List<ModelMetadata>>(body, JsonDefaults.Options) ?? [];
    }
}
public sealed record ModelMetadata(string Name, string? Description = null);
