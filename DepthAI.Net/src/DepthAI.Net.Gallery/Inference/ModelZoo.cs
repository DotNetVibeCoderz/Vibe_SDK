using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DepthAI.Inference;

namespace DepthAI.Gallery.Inference;

/// <summary>Satu model di Luxonis Model Zoo, misalnya <c>luxonis/yunet:640x480</c>.</summary>
public sealed record ZooModel(string Slug, string Version)
{
    public override string ToString() => $"luxonis/{Slug}:{Version}";
}

/// <summary>
/// Mengunduh NN Archive RVC2 dari Luxonis Model Zoo dan menyimpannya di cache lokal.
/// </summary>
/// <remarks>
/// <para>
/// Arsip tidak ikut di repo: ukurannya puluhan MB dan lisensinya milik pembuat model masing-
/// masing. Endpoint unduhnya sama dengan yang dipakai <c>dai::getModelFromZoo</c> di
/// depthai-core, dan model publik Luxonis tidak butuh kunci API.
/// </para>
/// <para>
/// Arsipnya berformat <c>.tar.xz</c>, yang tidak bisa dibuka .NET tanpa pustaka tambahan,
/// jadi <c>config.json</c> tiap model disalin ke <c>ModelConfigs/</c> dan dipasang sebagai
/// berkas pendamping <c>.tar.json</c> yang dikenali <see cref="NeuralModel.LoadFromFileAsync"/>.
/// </para>
/// </remarks>
public sealed class ModelZoo(HttpClient http, string cacheDirectory, string configDirectory)
{
    private const string DownloadEndpoint = "https://easyml.cloud.luxonis.com/models/api/v1/models/download";

    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepthAI.Net", "Gallery", "models");

    /// <summary>Path arsip bila sudah ada di cache, tanpa menyentuh jaringan.</summary>
    public string? TryGetCached(ZooModel model)
    {
        var folder = FolderFor(model);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.tar.xz").FirstOrDefault() : null;
    }

    /// <summary>Memuat model dari cache, mengunduhnya dulu bila belum ada.</summary>
    public async Task<NeuralModel> LoadAsync(ZooModel model, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var archive = TryGetCached(model) ?? await DownloadAsync(model, progress, cancellationToken);

        var sidecar = Path.ChangeExtension(archive, ".json");
        if (!File.Exists(sidecar))
        {
            File.Copy(Path.Combine(configDirectory, model.Slug + ".json"), sidecar);
        }

        return await NeuralModel.LoadFromFileAsync(archive, cancellationToken: cancellationToken);
    }

    /// <summary>Metadata model dari config bawaan galeri, tanpa perlu arsipnya.</summary>
    public ModelMetadata ReadMetadata(ZooModel model)
        => NnArchiveConfig.Parse(File.ReadAllBytes(Path.Combine(configDirectory, model.Slug + ".json")));

    private string FolderFor(ZooModel model) => Path.Combine(cacheDirectory, $"{model.Slug}-{model.Version}");

    private async Task<string> DownloadAsync(ZooModel model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"Mencari {model} di Luxonis Model Zoo…");
        var url = $"{DownloadEndpoint}?slug={Uri.EscapeDataString(model.Slug)}"
            + $"&model_version_slug={Uri.EscapeDataString(model.Version)}&platform=RVC2";

        var answer = await http.GetFromJsonAsync<DownloadAnswer>(url, cancellationToken)
            ?? throw new InvalidDataException($"Model Zoo tidak menjawab untuk {model}.");
        var link = answer.DownloadLinks?.FirstOrDefault(l => new Uri(l).AbsolutePath.EndsWith(".tar.xz", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Model Zoo tidak memberi arsip RVC2 untuk {model}.");

        var folder = FolderFor(model);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(new Uri(link).AbsolutePath));
        var partial = target + ".part";

        using (var response = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(partial);

            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (done - lastReport > 512 * 1024)
                {
                    lastReport = done;
                    progress?.Report(total is > 0
                        ? $"Mengunduh {model}: {done / 1048576.0:0.0} dari {total.Value / 1048576.0:0.0} MB"
                        : $"Mengunduh {model}: {done / 1048576.0:0.0} MB");
                }
            }
        }

        // Diganti nama setelah lengkap, supaya unduhan yang terputus tidak terbaca sebagai cache.
        File.Move(partial, target, overwrite: true);
        return target;
    }

    private sealed record DownloadAnswer
    {
        [JsonPropertyName("download_links")]
        public List<string>? DownloadLinks { get; init; }
    }
}
