using System.Buffers;

namespace DepthAI.Streaming;

/// <summary>
/// Frame gambar dua dimensi. Buffer piksel dipinjam dari pool — lihat catatan siklus
/// hidup pada <see cref="Frame"/>.
/// </summary>
public sealed class ImageFrame : Frame
{
    private byte[]? _pooled;
    private readonly int _length;

    private ImageFrame(byte[] buffer, int length, bool pooled)
    {
        Buffer = buffer;
        _length = length;
        _pooled = pooled ? buffer : null;
    }

    /// <summary>Buffer mentah. Hanya <c>Buffer.AsSpan(0, ByteLength)</c> yang valid.</summary>
    internal byte[] Buffer { get; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public PixelFormat Format { get; private init; }

    /// <summary>Jumlah byte per baris piksel, termasuk padding.</summary>
    public int Stride { get; private init; }

    /// <summary>Jumlah byte yang benar-benar terpakai di <see cref="Buffer"/>.</summary>
    public int ByteLength => _length;

    /// <summary>Jumlah byte per piksel; 0 untuk format terkompresi seperti JPEG.</summary>
    public int BytesPerPixel => Format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgr888 or PixelFormat.Rgb888 => 3,
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        _ => 0,
    };

    /// <summary>Akses hanya-baca ke piksel tanpa menyalin.</summary>
    public ReadOnlySpan<byte> Pixels
    {
        get
        {
            ThrowIfDisposed();
            return Buffer.AsSpan(0, _length);
        }
    }

    /// <summary>
    /// Membungkus buffer yang sudah ada tanpa menyalin. Frame menjadi pemilik
    /// <paramref name="buffer"/> bila <paramref name="pooled"/> true.
    /// </summary>
    public static ImageFrame Wrap(
        byte[] buffer,
        int width,
        int height,
        PixelFormat format,
        int stride = 0,
        int? length = null,
        bool pooled = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var effectiveLength = length ?? buffer.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectiveLength, buffer.Length);

        return new ImageFrame(buffer, effectiveLength, pooled)
        {
            Width = width,
            Height = height,
            Format = format,
            Stride = stride > 0 ? stride : width * Math.Max(1, BytesPerPixelOf(format)),
        };
    }

    /// <summary>Mengalokasikan frame dari pool lalu menyalin <paramref name="source"/> ke dalamnya.</summary>
    public static ImageFrame CopyFrom(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        PixelFormat format,
        int stride = 0)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(buffer);
        return Wrap(buffer, width, height, format, stride, source.Length, pooled: true);
    }

    /// <summary>
    /// Salinan lepas yang tidak terikat siklus hidup pool, aman disimpan tanpa batas waktu.
    /// </summary>
    public ImageFrame Clone()
    {
        ThrowIfDisposed();
        var copy = GC.AllocateUninitializedArray<byte>(_length);
        Buffer.AsSpan(0, _length).CopyTo(copy);

        return new ImageFrame(copy, _length, pooled: false)
        {
            Width = Width,
            Height = Height,
            Format = Format,
            Stride = Stride,
            SequenceNumber = SequenceNumber,
            DeviceTimestamp = DeviceTimestamp,
            HostTimestamp = HostTimestamp,
            StreamName = StreamName,
        };
    }

    /// <summary>Menyalin piksel ke array baru — jembatan untuk API yang menuntut <c>byte[]</c>.</summary>
    public byte[] ToArray()
    {
        ThrowIfDisposed();
        return Buffer.AsSpan(0, _length).ToArray();
    }

    /// <summary>Baris piksel ke-<paramref name="y"/>, menghormati stride.</summary>
    public ReadOnlySpan<byte> GetRow(int y)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Buffer.AsSpan(y * Stride, Math.Min(Stride, _length - (y * Stride)));
    }

    protected override void ReleaseBuffers()
    {
        var pooled = Interlocked.Exchange(ref _pooled, null);
        if (pooled is not null)
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    private static int BytesPerPixelOf(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgr888 or PixelFormat.Rgb888 => 3,
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        _ => 1,
    };
}
