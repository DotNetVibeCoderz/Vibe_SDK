using DepthAI.Devices;

namespace DepthAI.Pipelines;

/// <summary>Port keluaran sebuah node — sumber sebuah link.</summary>
public sealed class NodeOutput
{
    internal NodeOutput(PipelineNode node, string name)
    {
        Node = node;
        Name = name;
    }

    public PipelineNode Node { get; }

    public string Name { get; }

    /// <summary>Path unik dalam pipeline, misal <c>rgb.preview</c>.</summary>
    public string Path => $"{Node.Name}.{Name}";

    /// <summary>Menyambungkan keluaran ini ke sebuah masukan. Mengembalikan node tujuan agar bisa dirantai.</summary>
    public PipelineNode LinkTo(NodeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Node.RequireOwner().AddLink(this, input);
        return input.Node;
    }

    public override string ToString() => Path;
}

/// <summary>Port masukan sebuah node — tujuan sebuah link.</summary>
public sealed class NodeInput
{
    internal NodeInput(PipelineNode node, string name)
    {
        Node = node;
        Name = name;
    }

    public PipelineNode Node { get; }

    public string Name { get; }

    public string Path => $"{Node.Name}.{Name}";

    /// <summary>
    /// Ukuran antrean pada perangkat. Antrean besar menahan frame lebih lama (latensi naik)
    /// tapi menyerap lonjakan beban.
    /// </summary>
    public int QueueSize { get; set; } = 4;

    /// <summary>
    /// Bila true, produsen menunggu saat antrean penuh; bila false, frame terlama dibuang.
    /// Non-blocking adalah bawaan karena pipeline realtime lebih baik menjatuhkan frame
    /// daripada menahan kamera.
    /// </summary>
    public bool Blocking { get; set; }

    public override string ToString() => Path;
}

/// <summary>Sambungan berarah dari sebuah keluaran ke sebuah masukan.</summary>
public sealed record NodeLink(string From, string To)
{
    public override string ToString() => $"{From} → {To}";
}

/// <summary>
/// Basis semua node pipeline. Turunan mendeklarasikan port pada konstruktor dan
/// memaparkan properti konfigurasi bertipe.
/// </summary>
public abstract class PipelineNode
{
    private readonly List<NodeOutput> _outputs = [];
    private readonly List<NodeInput> _inputs = [];

    protected PipelineNode(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Nama unik node dalam pipeline; dipakai untuk link dan diagnostik.</summary>
    public string Name { get; }

    /// <summary>Pengenal tipe stabil yang dipakai di JSON pipeline.</summary>
    public abstract string NodeType { get; }

    public IReadOnlyList<NodeOutput> Outputs => _outputs;

    public IReadOnlyList<NodeInput> Inputs => _inputs;

    internal Pipeline? Owner { get; set; }

    protected NodeOutput DefineOutput(string name)
    {
        var output = new NodeOutput(this, name);
        _outputs.Add(output);
        return output;
    }

    protected NodeInput DefineInput(string name)
    {
        var input = new NodeInput(this, name);
        _inputs.Add(input);
        return input;
    }

    public NodeOutput GetOutput(string name)
        => _outputs.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Node '{Name}' ({NodeType}) tidak punya keluaran '{name}'. Yang tersedia: {string.Join(", ", _outputs.Select(o => o.Name))}.");

    public NodeInput GetInput(string name)
        => _inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Node '{Name}' ({NodeType}) tidak punya masukan '{name}'. Yang tersedia: {string.Join(", ", _inputs.Select(i => i.Name))}.");

    /// <summary>Properti node yang diserialisasi ke JSON pipeline.</summary>
    internal abstract IDictionary<string, object?> GetProperties();

    /// <summary>Mengembalikan properti dari JSON pipeline.</summary>
    internal abstract void ApplyProperties(IReadOnlyDictionary<string, object?> properties);

    /// <summary>
    /// Memeriksa apakah node bisa berjalan pada perangkat tertentu. Menambahkan pesan
    /// yang bisa ditindaklanjuti ke <paramref name="errors"/> alih-alih melempar, supaya
    /// pemakai melihat seluruh masalah sekaligus, bukan satu per satu.
    /// </summary>
    internal virtual void Validate(DeviceCapabilities capabilities, IList<string> errors) { }

    internal Pipeline RequireOwner()
        => Owner ?? throw new InvalidOperationException(
            $"Node '{Name}' belum ditambahkan ke Pipeline. Buat node lewat Pipeline.Add*() sebelum menyambungkannya.");

    public override string ToString() => $"{NodeType} '{Name}'";
}
