using System.Runtime.InteropServices;
using System.Text;

namespace DepthAI.Interop;

/// <summary>
/// Menentukan apakah pustaka native depthai bisa dimuat, sekali per proses.
/// </summary>
/// <remarks>
/// Probing dilakukan lewat percobaan pemanggilan sungguhan, bukan cek keberadaan berkas:
/// pustaka bisa ada tapi gagal dimuat karena dependensi transitif hilang, dan kegagalan
/// itu baru terlihat saat benar-benar dipanggil.
/// </remarks>
public static class NativeRuntime
{
    private static readonly Lazy<ProbeResult> Probe = new(RunProbe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True bila pustaka native berhasil dimuat dan menjawab.</summary>
    public static bool IsAvailable => Probe.Value.Available;

    /// <summary>Versi depthai-core yang dilaporkan, atau null bila native tidak tersedia.</summary>
    public static string? Version => Probe.Value.Version;

    /// <summary>Alasan native tidak tersedia — ditampilkan ke pengguna oleh CLI dan wizard.</summary>
    public static string? UnavailableReason => Probe.Value.Reason;

    /// <summary>Nama berkas pustaka yang dicari runtime pada platform saat ini.</summary>
    public static string ExpectedLibraryFileName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $"{NativeMethods.LibraryName}.dll";
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? $"lib{NativeMethods.LibraryName}.dylib"
                : $"lib{NativeMethods.LibraryName}.so";
        }
    }

    /// <summary>Membaca pesan error terakhir dari pustaka native.</summary>
    internal static string GetLastError()
    {
        if (!IsAvailable)
        {
            return "pustaka native tidak dimuat";
        }

        try
        {
            Span<byte> buffer = stackalloc byte[512];
            var length = NativeMethods.GetLastError(buffer, buffer.Length);
            return length > 0 ? Encoding.UTF8.GetString(buffer[..length]) : "tidak ada detail error";
        }
        catch (Exception ex)
        {
            return $"gagal membaca error native: {ex.Message}";
        }
    }

    /// <summary>Melempar dengan konteks bila <paramref name="status"/> menandakan kegagalan.</summary>
    internal static void ThrowIfFailed(int status, string operation)
    {
        if (status == (int)DaiStatus.Ok)
        {
            return;
        }

        var code = Enum.IsDefined(typeof(DaiStatus), status) ? (DaiStatus)status : DaiStatus.Unknown;
        throw new DepthAiException($"{operation} gagal ({code}): {GetLastError()}");
    }

    private static ProbeResult RunProbe()
    {
        try
        {
            Span<byte> buffer = stackalloc byte[128];
            var length = NativeMethods.GetVersion(buffer, buffer.Length);

            return length <= 0
                ? new ProbeResult(false, null, "pustaka native dimuat tapi tidak melaporkan versi")
                : new ProbeResult(true, Encoding.UTF8.GetString(buffer[..length]), null);
        }
        catch (DllNotFoundException)
        {
            return new ProbeResult(
                false,
                null,
                $"'{ExpectedLibraryFileName}' tidak ditemukan. Pasang paket runtime native "
                + "(DepthAI.Net.Runtime.<rid>) atau letakkan pustaka di samping aplikasi.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return new ProbeResult(
                false,
                null,
                $"'{ExpectedLibraryFileName}' ditemukan tapi tidak memaparkan ABI depthai-c "
                + $"yang diharapkan ({ex.Message}). Kemungkinan versi pustaka tidak cocok.");
        }
        catch (BadImageFormatException)
        {
            return new ProbeResult(
                false,
                null,
                $"'{ExpectedLibraryFileName}' punya arsitektur yang salah untuk proses "
                + $"{RuntimeInformation.ProcessArchitecture} ini.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, null, $"gagal memuat pustaka native: {ex.Message}");
        }
    }

    private sealed record ProbeResult(bool Available, string? Version, string? Reason);
}

/// <summary>Kesalahan yang berasal dari perangkat atau lapisan native.</summary>
public class DepthAiException : Exception
{
    public DepthAiException() { }

    public DepthAiException(string message) : base(message) { }

    public DepthAiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Tidak ada perangkat yang cocok dengan kriteria.</summary>
public sealed class DeviceNotFoundException : DepthAiException
{
    public DeviceNotFoundException() : base("Tidak ada perangkat OAK yang ditemukan.") { }

    public DeviceNotFoundException(string message) : base(message) { }

    public DeviceNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
