using System.Buffers;
using System.Text.Json;
using DepthAI.Inference;
using DepthAI.Pipelines.Nodes;

namespace DepthAI.Pipelines;

/// <summary>Opsi saat memuat pipeline dari JSON.</summary>
public sealed class PipelineLoadOptions
{
    /// <summary>
    /// Menyelesaikan nama model di JSON menjadi model yang benar-benar termuat.
    /// JSON hanya menyimpan nama — payload model bisa ratusan megabyte, jadi tidak ikut
    /// diserialisasi. Tanpa resolver, node NN termuat tanpa model dan validasi akan menolaknya.
    /// </summary>
    public Func<string, NeuralModel?>? ModelResolver { get; init; }

    /// <summary>Pabrik untuk tipe node kustom di luar bawaan SDK.</summary>
    public IReadOnlyDictionary<string, Func<string, PipelineNode>> CustomNodeFactories { get; init; }
        = new Dictionary<string, Func<string, PipelineNode>>();
}

public sealed partial class Pipeline
{
    /// <summary>Versi skema JSON pipeline. Dinaikkan bila format berubah tidak kompatibel.</summary>
    public const string SchemaVersion = "1.0";

    private static readonly Dictionary<string, Func<string, PipelineNode>> BuiltInFactories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ColorCamera"] = static name => new ColorCameraNode(name),
            ["MonoCamera"] = static name => new MonoCameraNode(name),
            ["StereoDepth"] = static name => new StereoDepthNode(name),
            ["NeuralNetwork"] = static name => new NeuralNetworkNode(name),
            ["DetectionNetwork"] = static name => new DetectionNetworkNode(name),
            ["SpatialDetectionNetwork"] = static name => new SpatialDetectionNetworkNode(name),
            ["ImageManip"] = static name => new ImageManipNode(name),
            ["VideoEncoder"] = static name => new VideoEncoderNode(name),
            ["Imu"] = static name => new ImuNode(name),
        };

    /// <summary>Tipe node yang dikenali loader — dipakai tooling untuk melengkapi editor.</summary>
    public static IReadOnlyCollection<string> KnownNodeTypes => BuiltInFactories.Keys;

    /// <summary>
    /// Menyerialisasi graf ke JSON pipeline. Payload model tidak disertakan — hanya namanya.
    /// </summary>
    public string ToJson(bool indented = true)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", SchemaVersion);

            writer.WriteStartArray("nodes");
            foreach (var node in _nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("name", node.Name);
                writer.WriteString("type", node.NodeType);

                writer.WriteStartObject("properties");
                foreach (var (key, value) in node.GetProperties())
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("links");
            foreach (var link in _links)
            {
                writer.WriteStartObject();
                writer.WriteString("from", link.From);
                writer.WriteString("to", link.To);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("streams");
            foreach (var stream in _streams)
            {
                writer.WriteStartObject();
                writer.WriteString("name", stream.Name);
                writer.WriteString("source", stream.Source);
                writer.WriteNumber("maxSize", stream.MaxSize);
                writer.WriteBoolean("blocking", stream.Blocking);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Membangun ulang pipeline dari JSON yang dihasilkan <see cref="ToJson"/>.</summary>
    public static Pipeline FromJson(string json, PipelineLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        options ??= new PipelineLoadOptions();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        var pipeline = new Pipeline();

        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in nodes.EnumerateArray())
            {
                pipeline.Add(CreateNode(element, options));
            }
        }

        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in links.EnumerateArray())
            {
                var from = element.GetProperty("from").GetString();
                var to = element.GetProperty("to").GetString();

                if (from is not null && to is not null)
                {
                    pipeline.Link(from, to);
                }
            }
        }

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in streams.EnumerateArray())
            {
                var source = element.GetProperty("source").GetString()
                    ?? throw new FormatException("Entri stream tidak punya 'source'.");

                pipeline.AddOutputStream(
                    pipeline.ResolveOutput(source),
                    element.TryGetProperty("name", out var name) ? name.GetString() : null,
                    element.TryGetProperty("maxSize", out var maxSize) ? maxSize.GetInt32() : 4,
                    element.TryGetProperty("blocking", out var blocking) && blocking.GetBoolean());
            }
        }

        return pipeline;
    }

    /// <summary>Memuat pipeline dari berkas <c>.pipeline.json</c>.</summary>
    public static async Task<Pipeline> LoadFromFileAsync(
        string path,
        PipelineLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return FromJson(json, options);
    }

    /// <summary>Menulis pipeline ke berkas JSON.</summary>
    public Task SaveToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.WriteAllTextAsync(path, ToJson(), cancellationToken);
    }

    private static PipelineNode CreateNode(JsonElement element, PipelineLoadOptions options)
    {
        var name = element.GetProperty("name").GetString()
            ?? throw new FormatException("Entri node tidak punya 'name'.");
        var type = element.GetProperty("type").GetString()
            ?? throw new FormatException($"Node '{name}' tidak punya 'type'.");

        Func<string, PipelineNode>? factory = null;
        if (!options.CustomNodeFactories.TryGetValue(type, out factory)
            && !BuiltInFactories.TryGetValue(type, out factory))
        {
            throw new NotSupportedException(
                $"Tipe node '{type}' tidak dikenal. Tipe bawaan: {string.Join(", ", BuiltInFactories.Keys)}. "
                + "Tipe kustom bisa didaftarkan lewat PipelineLoadOptions.CustomNodeFactories.");
        }

        var node = factory(name);

        if (element.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.EnumerateObject())
            {
                bag[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
            }

            node.ApplyProperties(bag);
        }

        // Sambungkan kembali payload model setelah properti diterapkan, karena
        // ApplyProperties-lah yang memulihkan ModelName dari JSON.
        if (node is NeuralNetworkNode nn && nn.ModelName is { Length: > 0 } modelName)
        {
            nn.Model = options.ModelResolver?.Invoke(modelName);
        }

        return node;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float[] array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
                break;
            case Enum e:
                writer.WriteStringValue(e.ToString());
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
