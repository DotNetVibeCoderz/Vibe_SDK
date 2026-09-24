using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeSafeSdk;

/// <summary>Resource untuk mengambil daftar model TypeSafe: <c>GET /v1/models</c>.</summary>
public sealed class ModelsClient
{
    private readonly TypeSafeClient _client;

    internal ModelsClient(TypeSafeClient client) => _client = client;

    /// <summary>Mengambil daftar model yang tersedia untuk API key saat ini.</summary>
    public async Task<ListModelsResponse> ListAsync(
        RetryPolicy? retry = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        if (_client.Options.Simulator) return ListModelsResponse.Simulated();
        var (body, _) = await _client.SendAsync(HttpMethod.Get, TypeSafeConstants.ModelsPath, null, retry, timeout, extraHeaders, cancellationToken);
        try { return ListModelsResponse.Parse(body); }
        catch (JsonException ex) { throw new TypeSafeApiResponseValidationException("models", body, ex); }
    }

    /// <summary>Overload ringkas untuk pemanggilan dengan cancellation token saja.</summary>
    public Task<ListModelsResponse> ListAsync(CancellationToken cancellationToken) => ListAsync(null, null, null, cancellationToken);
}

/// <summary>Metadata satu model TypeSafe.</summary>
public sealed record ModelMetadata(
    string Name,
    string? Description = null,
    [property: JsonPropertyName("release_date")] string? ReleaseDate = null);

/// <summary>
/// Response <c>GET /v1/models</c>. Dapat dipakai langsung sebagai koleksi
/// <see cref="ModelMetadata"/> maupun lewat properti <see cref="Models"/>.
/// </summary>
public sealed class ListModelsResponse : IReadOnlyList<ModelMetadata>
{
    /// <summary>Daftar model yang tersedia.</summary>
    public IReadOnlyList<ModelMetadata> Models { get; }

    public ListModelsResponse(IReadOnlyList<ModelMetadata> models) => Models = models;

    public ModelMetadata this[int index] => Models[index];
    public int Count => Models.Count;
    public IEnumerator<ModelMetadata> GetEnumerator() => Models.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Menerima bentuk array telanjang maupun objek <c>{ "models": [...] }</c>.</summary>
    public static ListModelsResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
            : throw new JsonException("Expected a models array or an object with a 'models' array.");
        return new ListModelsResponse(JsonSerializer.Deserialize<List<ModelMetadata>>(array.GetRawText(), JsonDefaults.Options) ?? []);
    }

    /// <summary>Daftar model yang dikembalikan simulator lokal.</summary>
    public static ListModelsResponse Simulated() => new([new ModelMetadata(TypeSafeConstants.DefaultModel, "Local simulator model.", "2026-01-01")]);
}
