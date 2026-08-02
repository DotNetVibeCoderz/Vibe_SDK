using System.Buffers;
using System.Runtime.InteropServices;

namespace DepthAI.Streaming;

/// <summary>
/// Peta kedalaman. Tiap piksel adalah jarak 16-bit tak bertanda dalam milimeter,
/// dengan 0 berarti "tidak ada pengukuran" (oklusi, permukaan tanpa tekstur, atau
/// di luar jangkauan) — bukan "jarak nol".
/// </summary>
public sealed class DepthFrame : Frame
{
    private ushort[]? _pooled;

    private DepthFrame(ushort[] buffer, bool pooled)
    {
        Buffer = buffer;
        _pooled = pooled ? buffer : null;
    }

    internal ushort[] Buffer { get; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    /// <summary>Jarak minimum yang bisa diukur sensor, meter.</summary>
    public float MinDepthMeters { get; init; } = 0.2f;

    /// <summary>Jarak maksimum yang masih dianggap valid, meter.</summary>
    public float MaxDepthMeters { get; init; } = 10.0f;

    /// <summary>Panjang fokus dalam piksel; dibutuhkan untuk deproyeksi ke titik 3D.</summary>
    public float FocalLengthPixels { get; init; }

    /// <summary>Jarak antar kamera stereo dalam sentimeter.</summary>
    public float BaselineCentimeters { get; init; }

    public ReadOnlySpan<ushort> Millimeters
    {
        get
        {
            ThrowIfDisposed();
            return Buffer.AsSpan(0, Width * Height);
        }
    }

    public static DepthFrame Wrap(ushort[] buffer, int width, int height, bool pooled = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, width * height);

        return new DepthFrame(buffer, pooled) { Width = width, Height = height };
    }

    /// <summary>Menafsirkan buffer byte little-endian dari perangkat sebagai peta kedalaman.</summary>
    public static DepthFrame CopyFrom(ReadOnlySpan<byte> source, int width, int height)
    {
        var pixels = width * height;
        var buffer = ArrayPool<ushort>.Shared.Rent(pixels);
        MemoryMarshal.Cast<byte, ushort>(source)[..pixels].CopyTo(buffer);
        return Wrap(buffer, width, height, pooled: true);
    }

    /// <summary>Kedalaman mentah dalam milimeter; 0 berarti tidak ada pengukuran.</summary>
    public ushort GetMillimeters(int x, int y)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Buffer[(y * Width) + x];
    }

    /// <summary>
    /// Jarak dalam meter, atau <see langword="null"/> bila piksel tidak punya pengukuran valid.
    /// Bentuk nullable dipilih supaya piksel kosong tidak diam-diam terbaca sebagai 0 m.
    /// </summary>
    public float? GetDistanceMeters(int x, int y)
    {
        var mm = GetMillimeters(x, y);
        if (mm == 0)
        {
            return null;
        }

        var meters = mm / 1000f;
        return meters < MinDepthMeters || meters > MaxDepthMeters ? null : meters;
    }

    /// <summary>
    /// Deproyeksi piksel ke titik 3D dalam koordinat kamera (meter, sumbu Z ke depan).
    /// Perlu <see cref="FocalLengthPixels"/> dari kalibrasi perangkat.
    /// </summary>
    public (float X, float Y, float Z)? GetPoint3D(int x, int y)
    {
        var z = GetDistanceMeters(x, y);
        if (z is null || FocalLengthPixels <= 0)
        {
            return null;
        }

        var cx = Width / 2f;
        var cy = Height / 2f;
        return ((x - cx) * z.Value / FocalLengthPixels, (y - cy) * z.Value / FocalLengthPixels, z.Value);
    }

    /// <summary>
    /// Menyalin kedalaman ke matriks <c>float[height, width]</c> dalam meter, dengan
    /// <see cref="float.NaN"/> untuk piksel tanpa pengukuran — bentuk yang langsung
    /// bisa dipakai ML.NET / SciSharp / NumSharp.
    /// </summary>
    public float[,] ToMeterMatrix()
    {
        ThrowIfDisposed();
        var matrix = new float[Height, Width];
        var src = Millimeters;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var mm = src[(y * Width) + x];
                matrix[y, x] = mm == 0 ? float.NaN : mm / 1000f;
            }
        }

        return matrix;
    }

    /// <summary>Menyalin kedalaman mentah ke array datar milimeter.</summary>
    public ushort[] ToArray()
    {
        ThrowIfDisposed();
        return Millimeters.ToArray();
    }

    public DepthFrame Clone()
    {
        ThrowIfDisposed();
        var copy = GC.AllocateUninitializedArray<ushort>(Width * Height);
        Millimeters.CopyTo(copy);

        return new DepthFrame(copy, pooled: false)
        {
            Width = Width,
            Height = Height,
            MinDepthMeters = MinDepthMeters,
            MaxDepthMeters = MaxDepthMeters,
            FocalLengthPixels = FocalLengthPixels,
            BaselineCentimeters = BaselineCentimeters,
            SequenceNumber = SequenceNumber,
            DeviceTimestamp = DeviceTimestamp,
            HostTimestamp = HostTimestamp,
            StreamName = StreamName,
        };
    }

    protected override void ReleaseBuffers()
    {
        var pooled = Interlocked.Exchange(ref _pooled, null);
        if (pooled is not null)
        {
            ArrayPool<ushort>.Shared.Return(pooled);
        }
    }
}
