using System.Buffers;

namespace DepthAI.Streaming;

/// <summary>Layout piksel pada <see cref="ImageFrame"/>.</summary>
public enum PixelFormat
{
    Unknown = 0,
    Gray8,
    Bgr888,
    Rgb888,
    Bgra8888,
    Rgba8888,
    /// <summary>Planar YUV 4:2:0 — format native banyak node kamera OAK.</summary>
    Nv12,
    /// <summary>Byte JPEG utuh (dari node encoder).</summary>
    Jpeg,
}

/// <summary>
/// Basis semua paket yang mengalir dari perangkat.
/// </summary>
/// <remarks>
/// Frame memiliki buffer yang dipinjam dari <see cref="ArrayPool{T}"/>: buang frame
/// segera setelah dipakai. Bila frame perlu hidup lebih lama dari callback
/// (misalnya disimpan untuk UI), panggil <see cref="ImageFrame.Clone"/> —
/// meneruskan frame yang sudah dibuang akan melempar exception, bukan diam-diam
/// membaca memori yang sudah didaur ulang.
/// </remarks>
public abstract class Frame : IDisposable
{
    private int _disposed;

    /// <summary>Nomor urut monotonik yang diberikan perangkat. Berguna mendeteksi frame yang hilang.</summary>
    public long SequenceNumber { get; internal set; }

    /// <summary>Timestamp perangkat (bukan waktu host) sejak perangkat boot.</summary>
    public TimeSpan DeviceTimestamp { get; internal set; }

    /// <summary>Waktu host saat frame diterima. Dipakai untuk mengukur latensi end-to-end.</summary>
    public DateTimeOffset HostTimestamp { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Nama output stream asal frame ini.</summary>
    public string StreamName { get; internal set; } = string.Empty;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    protected void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(
                GetType().Name,
                "Frame sudah dibuang. Panggil Clone() bila frame perlu hidup lebih lama dari callback stream.");
        }
    }

    protected virtual void ReleaseBuffers() { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseBuffers();
        }

        GC.SuppressFinalize(this);
    }
}
