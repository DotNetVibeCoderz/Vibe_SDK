using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TypeSafeSdk;

/// <summary>
/// Aset media yang dirujuk dari isi request, sejajar dengan <c>typesafe_sdk.Asset</c> pada Python SDK.
/// Saat ini hanya gambar (<see cref="ImageAsset"/>) yang didukung.
/// </summary>
/// <remarks>
/// Setiap aset punya rujukan unik, <c>&lt;image:…&gt;</c>, yang dikembalikan <see cref="ToString"/>. Aset yang
/// diletakkan langsung di <c>state</c> atau di dalam pertanyaan dikirim sebagai rujukan tersebut, dan
/// rujukan yang muncul di dalam teks ikut dikirim bila asetnya dilewatkan lewat parameter <c>assets</c> atau
/// dipasang dengan <see cref="TypeSafeClient.AttachAssets(Asset[])"/>.
/// </remarks>
[JsonConverter(typeof(AssetJsonConverter))]
public abstract class Asset
{
    private protected Asset(string kind) => Reference = $"<{kind}:{Guid.NewGuid():N}>";

    /// <summary>Rujukan unik aset ini, untuk dipakai di dalam teks seperti interpolasi string.</summary>
    public string Reference { get; }

    /// <summary>Rujukan unik aset ini; sama dengan <see cref="Reference"/>.</summary>
    public override string ToString() => Reference;

    /// <summary>Membaca dan memvalidasi isi aset sekali, lalu memakai ulang hasilnya.</summary>
    internal abstract ValueTask<ResolvedAsset> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>Aset yang sudah dibaca: content type hasil deteksi signature beserta byte filenya.</summary>
internal sealed record ResolvedAsset(string ContentType, ReadOnlyMemory<byte> Data);

/// <summary>
/// Aset gambar dari byte file gambar atau path file, sejajar dengan <c>typesafe_sdk.Image</c>.
/// Gambar dibaca sekali pada pemakaian pertama dan dipakai ulang oleh setiap request yang mengirim objek yang sama.
/// </summary>
/// <remarks>Butuh model vision-language, misalnya <c>jev-preview</c>.</remarks>
[JsonConverter(typeof(AssetJsonConverter))]
public sealed class ImageAsset : Asset
{
    private static readonly (byte[] Signature, string ContentType)[] Signatures =
    [
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png"),
        ([0xFF, 0xD8, 0xFF], "image/jpeg")
    ];

    private readonly ReadOnlyMemory<byte>? _data;
    private readonly string? _path;
    private ResolvedAsset? _resolved;

    private ImageAsset(ReadOnlyMemory<byte>? data, string? path) : base("image") { _data = data; _path = path; }

    /// <summary>Path file sumber, bila gambar dibuat dari file.</summary>
    public string? Path => _path;

    /// <summary>Membuat gambar dari byte file gambar (PNG, JPEG, atau format lain yang dideteksi API).</summary>
    public static ImageAsset FromBytes(ReadOnlyMemory<byte> data) => new(data.ToArray(), null);

    /// <summary>Membuat gambar dari path file. File baru dibaca ketika request pertama dikirim.</summary>
    public static ImageAsset FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(null, path);
    }

    /// <summary>Membuat gambar dari teks base64, misalnya hasil upload. Spasi dan baris baru diabaikan.</summary>
    /// <exception cref="TypeSafeException">Teks kosong atau bukan base64 yang valid.</exception>
    public static ImageAsset FromBase64(string data)
    {
        var text = string.Concat((data ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (text.Length == 0 || !Ascii.IsValid(text)) throw new TypeSafeException("Image data must contain non-empty ASCII base64 text.");
        try { return new(Convert.FromBase64String(text), null); }
        catch (FormatException ex) { throw new TypeSafeException("Image data must contain valid base64 text.", ex); }
    }

    /// <summary>Membuat gambar dengan menyalin isi stream ke memori, sehingga stream boleh ditutup sesudahnya.</summary>
    public static ImageAsset FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new(copy.ToArray(), null);
    }

    internal override async ValueTask<ResolvedAsset> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved is not null) return _resolved;
        ReadOnlyMemory<byte> data;
        if (_path is not null)
        {
            try { data = await File.ReadAllBytesAsync(_path, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new TypeSafeException($"Image file '{_path}' could not be read: {ex.Message}", ex); }
        }
        else data = _data!.Value;
        if (data.IsEmpty) throw new TypeSafeException("Image data is empty.");
        return _resolved = new ResolvedAsset(DetectContentType(data.Span), data);
    }

    /// <summary>Mengenali PNG dan JPEG dari signature; format lain dikirim sebagai data biner generik dan dideteksi API.</summary>
    internal static string DetectContentType(ReadOnlySpan<byte> header)
    {
        foreach (var (signature, contentType) in Signatures)
            if (header.StartsWith(signature)) return contentType;
        return "application/octet-stream";
    }
}

/// <summary>
/// Menulis aset sebagai alias-nya (bila dilewatkan lewat <c>assets</c>) atau sebagai rujukannya, sambil
/// mencatat aset yang ditulis sebagai rujukan. Tanpa konteks request, aset selalu ditulis sebagai rujukan.
/// </summary>
internal sealed class AssetJsonConverter : JsonConverter<Asset>
{
    private readonly AssetReferences? _references;

    public AssetJsonConverter() { }
    public AssetJsonConverter(AssetReferences references) => _references = references;

    public override bool CanConvert(Type typeToConvert) => typeof(Asset).IsAssignableFrom(typeToConvert);

    public override Asset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Assets are request-only and cannot be deserialized.");

    public override void Write(Utf8JsonWriter writer, Asset value, JsonSerializerOptions options)
        => writer.WriteStringValue(_references?.Serialize(value) ?? value.Reference);
}

/// <summary>Konteks serialisasi satu request: alias aset yang dilewatkan, dan aset yang ditulis sebagai rujukan.</summary>
internal sealed class AssetReferences
{
    private readonly Dictionary<Asset, string> _aliases = new(ReferenceEqualityComparer.Instance);

    public AssetReferences(IReadOnlyDictionary<string, Asset>? assets)
    {
        foreach (var (name, asset) in assets ?? new Dictionary<string, Asset>()) _aliases[asset] = name;
        Options = new JsonSerializerOptions(JsonDefaults.Options) { Converters = { new AssetJsonConverter(this) } };
    }

    /// <summary>Opsi serializer yang menulis aset lewat konteks ini.</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>Aset yang diletakkan langsung di isi request, menurut rujukannya.</summary>
    public Dictionary<string, Asset> Found { get; } = new(StringComparer.Ordinal);

    public string Serialize(Asset asset)
    {
        if (_aliases.TryGetValue(asset, out var alias)) return alias;
        Found[asset.Reference] = asset;
        return asset.Reference;
    }
}

/// <summary>Aset yang dipasang pada client, dibagi oleh seluruh pemanggilan dari thread mana pun.</summary>
internal sealed class AttachedAssets
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
    // Blok yang tumpang-tindih boleh memasang aset yang sama; aset tetap terpasang sampai blok terakhir selesai.
    private readonly Dictionary<string, int> _holds = new(StringComparer.Ordinal);

    public IDisposable Attach(IEnumerable<Asset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var attached = assets.ToList();
        foreach (var asset in attached)
            if (asset is null) throw new TypeSafeException("Attached value must be an asset.");
        lock (_lock)
            foreach (var asset in attached)
            {
                _assets[asset.Reference] = asset;
                _holds[asset.Reference] = _holds.GetValueOrDefault(asset.Reference) + 1;
            }
        return new Release(this, attached);
    }

    public Dictionary<string, Asset> Snapshot()
    {
        lock (_lock) return new(_assets, StringComparer.Ordinal);
    }

    private void Detach(List<Asset> attached)
    {
        lock (_lock)
            foreach (var asset in attached)
            {
                if (--_holds[asset.Reference] > 0) continue;
                _holds.Remove(asset.Reference);
                _assets.Remove(asset.Reference);
            }
    }

    private sealed class Release(AttachedAssets owner, List<Asset> attached) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Detach(attached); }
    }
}

/// <summary>Body request yang sudah dienkode sekali dan dipakai ulang oleh setiap percobaan.</summary>
internal sealed record RequestBody(byte[] Content, string ContentType);

/// <summary>Menyusun body System One: JSON biasa, atau <c>multipart/form-data</c> bila request membawa aset.</summary>
internal static partial class SystemOneRequestBuilder
{
    private const string JsonContentType = "application/json; charset=utf-8";

    // Rujukan bisa muncul apa adanya atau dengan '<' dan '>' di-escape oleh encoder JSON bawaan.
    [GeneratedRegex(@"(?:<|\\u003[Cc])image:([0-9a-f]{32})(?:>|\\u003[Ee])")]
    private static partial Regex ReferencePattern();

    public static async Task<RequestBody> BuildAsync(
        Dictionary<string, object?> body,
        IReadOnlyDictionary<string, Asset>? assets,
        IReadOnlyDictionary<string, Asset> attached,
        CancellationToken cancellationToken)
    {
        if (assets is not null)
            foreach (var (name, asset) in assets)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new TypeSafeException("Asset aliases must be non-empty strings.");
                if (asset is null) throw new TypeSafeException($"Asset '{name}' must be an asset.");
            }

        var references = new AssetReferences(assets);
        var json = Serialize(body, references.Options);
        // extraBody yang mengisi "assets" sendiri menggantikan aset SDK: tidak ada yang dibaca atau dienkode.
        if (body.ContainsKey("assets")) return new RequestBody(json, JsonContentType);

        var pending = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var (name, asset) in assets ?? new Dictionary<string, Asset>()) pending[name] = asset;
        var known = new Dictionary<string, Asset>(attached, StringComparer.Ordinal);
        foreach (var asset in pending.Values) known[asset.Reference] = asset;
        foreach (var (reference, asset) in references.Found) known[reference] = asset;
        if (known.Count > 0)
            foreach (Match match in ReferencePattern().Matches(Encoding.UTF8.GetString(json)))
            {
                var reference = $"<image:{match.Groups[1].Value}>";
                if (known.TryGetValue(reference, out var asset)) pending.TryAdd(reference, asset);
            }
        // API menolak "assets" kosong, jadi field itu tidak dikirim sama sekali.
        if (pending.Count == 0) return new RequestBody(json, JsonContentType);

        // Setiap part dinamai menurut posisinya, jadi body bisa menyebutnya sebelum aset dibaca.
        var names = pending.Keys.ToList();
        body["assets"] = names.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => (object?)new Dictionary<string, string> { ["type"] = "image", ["part"] = PartName(x.index) }, StringComparer.Ordinal);
        json = Serialize(body, references.Options);

        // Resolusi dilakukan sekali per objek aset, walau satu gambar dikirim dengan beberapa nama.
        var resolved = new Dictionary<Asset, ResolvedAsset>(ReferenceEqualityComparer.Instance);
        var parts = new List<(string Name, string ContentType, ReadOnlyMemory<byte> Content)>();
        for (var index = 0; index < names.Count; index++)
        {
            var asset = pending[names[index]];
            if (!resolved.TryGetValue(asset, out var image))
            {
                try { image = resolved[asset] = await asset.ResolveAsync(cancellationToken); }
                catch (TypeSafeException ex) { throw new TypeSafeException($"Asset '{names[index]}': {ex.Message}", ex); }
            }
            parts.Add((PartName(index), image.ContentType, image.Data));
        }
        return EncodeMultipart(json, parts);
    }

    private static byte[] Serialize(Dictionary<string, object?> body, JsonSerializerOptions options)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(body, options); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        { throw new TypeSafeException("The request body could not be encoded as JSON", ex); }
    }

    private static string PartName(int index) => $"asset-{index}";

    /// <summary>
    /// Body <c>multipart/form-data</c> (RFC 7578): JSON request sebagai part pertama bernama <c>request</c>,
    /// diikuti byte mentah setiap aset pada part yang disebut entri asetnya.
    /// </summary>
    private static RequestBody EncodeMultipart(byte[] json, List<(string Name, string ContentType, ReadOnlyMemory<byte> Content)> assets)
    {
        var boundary = Guid.NewGuid().ToString("N");
        using var stream = new MemoryStream();
        void Part(string name, string contentType, ReadOnlySpan<byte> content, string? filename)
        {
            var disposition = $"form-data; name=\"{name}\"" + (filename is null ? "" : $"; filename=\"{filename}\"");
            stream.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: {disposition}\r\nContent-Type: {contentType}\r\n\r\n"));
            stream.Write(content);
            stream.Write("\r\n"u8);
        }
        Part("request", "application/json", json, null);
        foreach (var (name, contentType, content) in assets) Part(name, contentType, content.Span, name);
        stream.Write(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"));
        return new RequestBody(stream.ToArray(), $"multipart/form-data; boundary={boundary}");
    }
}
