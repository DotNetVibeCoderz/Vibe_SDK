using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines.Nodes;

namespace DepthAI.Pipelines;

/// <summary>Stream keluaran bernama yang bisa dilanggan host.</summary>
public sealed record OutputStreamDefinition
{
    public required string Name { get; init; }

    /// <summary>Path port keluaran sumber, misal <c>rgb.preview</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Kedalaman antrean host.</summary>
    public int MaxSize { get; init; } = 4;

    /// <summary>
    /// Bila false (bawaan), frame terlama dibuang saat host tertinggal. Setel true hanya
    /// bila setiap frame wajib sampai dan Anda siap menahan laju perangkat.
    /// </summary>
    public bool Blocking { get; init; }

    public override string ToString() => $"{Name} ← {Source}";
}

/// <summary>Hasil validasi pipeline terhadap kemampuan perangkat.</summary>
public sealed record PipelineValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Melempar bila tidak valid, dengan seluruh masalah tergabung dalam satu pesan.</summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "Pipeline tidak valid untuk perangkat ini:" + Environment.NewLine
                + string.Join(Environment.NewLine, Errors.Select(static e => "  • " + e)));
        }
    }

    public static PipelineValidationResult Success { get; } = new([], []);
}

/// <summary>
/// Graf node yang dijalankan perangkat. Bangun dengan metode <c>Add*</c> lalu sambungkan
/// port, atau pakai <see cref="PipelineBuilder"/> untuk gaya fluent.
/// </summary>
public sealed partial class Pipeline
{
    private readonly List<PipelineNode> _nodes = [];
    private readonly List<NodeLink> _links = [];
    private readonly List<OutputStreamDefinition> _streams = [];

    public IReadOnlyList<PipelineNode> Nodes => _nodes;

    public IReadOnlyList<NodeLink> Links => _links;

    public IReadOnlyList<OutputStreamDefinition> OutputStreams => _streams;

    /// <summary>Membuat pipeline kosong.</summary>
    public static Pipeline Create() => new();

    /// <summary>Membuat builder fluent yang mengisi pipeline baru.</summary>
    public static PipelineBuilder CreateBuilder() => new(new Pipeline());

    /// <summary>Menambahkan node yang sudah dibuat sendiri — jalur untuk tipe node kustom.</summary>
    public T Add<T>(T node)
        where T : PipelineNode
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Owner is not null)
        {
            throw new InvalidOperationException($"Node '{node.Name}' sudah dimiliki pipeline lain.");
        }

        if (_nodes.Any(n => string.Equals(n.Name, node.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Pipeline sudah punya node bernama '{node.Name}'. Nama node harus unik.", nameof(node));
        }

        node.Owner = this;
        _nodes.Add(node);
        return node;
    }

    public ColorCameraNode AddColorCamera(string name = "rgb", Action<ColorCameraNode>? configure = null)
        => AddConfigured(new ColorCameraNode(name), configure);

    public MonoCameraNode AddMonoCamera(string name, Action<MonoCameraNode>? configure = null)
        => AddConfigured(new MonoCameraNode(name), configure);

    public StereoDepthNode AddStereoDepth(string name = "stereo", Action<StereoDepthNode>? configure = null)
        => AddConfigured(new StereoDepthNode(name), configure);

    public NeuralNetworkNode AddNeuralNetwork(string name = "nn", Action<NeuralNetworkNode>? configure = null)
        => AddConfigured(new NeuralNetworkNode(name), configure);

    public DetectionNetworkNode AddDetectionNetwork(string name = "detector", Action<DetectionNetworkNode>? configure = null)
        => AddConfigured(new DetectionNetworkNode(name), configure);

    public SpatialDetectionNetworkNode AddSpatialDetectionNetwork(
        string name = "spatialDetector",
        Action<SpatialDetectionNetworkNode>? configure = null)
        => AddConfigured(new SpatialDetectionNetworkNode(name), configure);

    public ImageManipNode AddImageManip(string name = "manip", Action<ImageManipNode>? configure = null)
        => AddConfigured(new ImageManipNode(name), configure);

    public VideoEncoderNode AddVideoEncoder(string name = "encoder", Action<VideoEncoderNode>? configure = null)
        => AddConfigured(new VideoEncoderNode(name), configure);

    public ImuNode AddImu(string name = "imu", Action<ImuNode>? configure = null)
        => AddConfigured(new ImuNode(name), configure);

    /// <summary>
    /// Memaparkan port keluaran sebagai stream bernama yang bisa dilanggan host.
    /// Nama bawaan mengikuti nama node supaya kasus lazim tidak perlu menamai manual.
    /// </summary>
    public OutputStreamDefinition AddOutputStream(
        NodeOutput output,
        string? name = null,
        int maxSize = 4,
        bool blocking = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!_nodes.Contains(output.Node))
        {
            throw new ArgumentException(
                $"Keluaran '{output.Path}' berasal dari node yang bukan bagian pipeline ini.", nameof(output));
        }

        var streamName = name ?? output.Node.Name;
        if (_streams.Any(s => string.Equals(s.Name, streamName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Sudah ada output stream bernama '{streamName}'.", nameof(name));
        }

        var definition = new OutputStreamDefinition
        {
            Name = streamName,
            Source = output.Path,
            MaxSize = maxSize,
            Blocking = blocking,
        };

        _streams.Add(definition);
        return definition;
    }

    public PipelineNode GetNode(string name)
        => _nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Tidak ada node bernama '{name}'. Node yang ada: {string.Join(", ", _nodes.Select(n => n.Name))}.");

    public T GetNode<T>(string name)
        where T : PipelineNode
        => GetNode(name) as T
            ?? throw new InvalidCastException($"Node '{name}' bukan bertipe {typeof(T).Name}.");

    /// <summary>Menyelesaikan path port seperti <c>rgb.preview</c> menjadi objek keluaran.</summary>
    public NodeOutput ResolveOutput(string path)
    {
        var (nodeName, portName) = SplitPath(path);
        return GetNode(nodeName).GetOutput(portName);
    }

    public NodeInput ResolveInput(string path)
    {
        var (nodeName, portName) = SplitPath(path);
        return GetNode(nodeName).GetInput(portName);
    }

    /// <summary>Menyambungkan keluaran ke masukan lewat path string — jalur yang dipakai loader JSON.</summary>
    public void Link(string fromOutputPath, string toInputPath)
        => AddLink(ResolveOutput(fromOutputPath), ResolveInput(toInputPath));

    /// <summary>
    /// Memeriksa pipeline terhadap kemampuan perangkat dan konsistensi graf.
    /// Panggil sebelum start supaya kesalahan konfigurasi muncul di host.
    /// </summary>
    public PipelineValidationResult Validate(DeviceCapabilities? capabilities = null)
    {
        var caps = capabilities ?? DeviceCapabilities.Unknown;
        var errors = new List<string>();
        var warnings = new List<string>();

        if (_nodes.Count == 0)
        {
            errors.Add("Pipeline tidak punya node.");
        }

        if (_streams.Count == 0)
        {
            warnings.Add("Pipeline tidak punya output stream, jadi host tidak akan menerima data apa pun.");
        }

        foreach (var node in _nodes)
        {
            node.Validate(caps, errors);
        }

        // Masukan wajib yang menggantung adalah penyebab paling umum pipeline "diam"
        // tanpa error, jadi dilaporkan sebagai error, bukan warning.
        foreach (var node in _nodes)
        {
            foreach (var input in RequiredInputsOf(node))
            {
                if (!_links.Any(l => string.Equals(l.To, input.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Masukan '{input.Path}' wajib disambungkan tapi tidak ada link ke sana.");
                }
            }
        }

        foreach (var stream in _streams)
        {
            var (nodeName, portName) = SplitPath(stream.Source);
            if (!_nodes.Any(n => string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Output stream '{stream.Name}' merujuk node '{nodeName}' yang tidak ada.");
            }
            else if (!GetNode(nodeName).Outputs.Any(o => string.Equals(o.Name, portName, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Output stream '{stream.Name}' merujuk port '{portName}' yang tidak ada pada node '{nodeName}'.");
            }
        }

        return new PipelineValidationResult(errors, warnings);
    }

    internal void AddLink(NodeOutput output, NodeInput input)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        if (!_nodes.Contains(output.Node) || !_nodes.Contains(input.Node))
        {
            throw new InvalidOperationException(
                $"Tidak bisa menyambung '{output.Path}' → '{input.Path}': kedua node harus berada di pipeline yang sama.");
        }

        if (ReferenceEquals(output.Node, input.Node))
        {
            throw new InvalidOperationException($"Node '{output.Node.Name}' tidak bisa disambungkan ke dirinya sendiri.");
        }

        var link = new NodeLink(output.Path, input.Path);
        if (!_links.Contains(link))
        {
            _links.Add(link);
        }
    }

    private T AddConfigured<T>(T node, Action<T>? configure)
        where T : PipelineNode
    {
        var added = Add(node);
        configure?.Invoke(added);
        return added;
    }

    /// <summary>
    /// Masukan yang tanpa link membuat node tidak bisa menghasilkan apa pun. Port
    /// kendali opsional seperti <c>inputControl</c> sengaja dikecualikan.
    /// </summary>
    private static IEnumerable<NodeInput> RequiredInputsOf(PipelineNode node) => node switch
    {
        StereoDepthNode stereo => [stereo.Left, stereo.Right],
        SpatialDetectionNetworkNode spatial => [spatial.Input, spatial.DepthInput],
        NeuralNetworkNode nn => [nn.Input],
        ImageManipNode manip => [manip.Input],
        VideoEncoderNode encoder => [encoder.Input],
        _ => [],
    };

    private static (string NodeName, string PortName) SplitPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var separator = path.LastIndexOf('.');
        if (separator <= 0 || separator == path.Length - 1)
        {
            throw new FormatException(
                $"Path port '{path}' tidak valid. Format yang diharapkan: 'namaNode.namaPort', misalnya 'rgb.preview'.");
        }

        return (path[..separator], path[(separator + 1)..]);
    }
}
