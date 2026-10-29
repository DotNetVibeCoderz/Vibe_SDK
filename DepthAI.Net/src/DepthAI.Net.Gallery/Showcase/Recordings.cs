using System.Text.Json;
using System.Text.Json.Serialization;
using DepthAI.Gallery.Inference;

namespace DepthAI.Gallery.Showcase;

/// <summary>
/// Hasil yang direkam dari kamera sungguhan untuk setiap pasangan use case dan gambar
/// contoh. Tanpa kamera, galeri menampilkan rekaman ini, lengkap dengan perangkat dan
/// waktu perekamannya, bukan hasil buatan.
/// </summary>
public sealed class Recordings
{
    public const string FileName = "recorded.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>use case → nama gambar → hasil.</summary>
    public Dictionary<string, Dictionary<string, GalleryResult>> Results { get; set; } = [];

    public GalleryResult? Find(string useCase, string sample)
        => Results.TryGetValue(useCase, out var samples) && samples.TryGetValue(sample, out var result) ? result : null;

    public void Store(string useCase, string sample, GalleryResult result)
    {
        if (!Results.TryGetValue(useCase, out var samples))
        {
            Results[useCase] = samples = [];
        }

        samples[sample] = result;
    }

    public static Recordings Load(string samplesDirectory)
    {
        var path = Path.Combine(samplesDirectory, FileName);
        if (!File.Exists(path))
        {
            return new Recordings();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Recordings>(stream, Json) ?? new Recordings();
    }

    public void Save(string samplesDirectory)
    {
        var path = Path.Combine(samplesDirectory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
