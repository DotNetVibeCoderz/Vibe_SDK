using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DepthAI.Interop;

/// <summary>
/// Memuat <c>depthai-c</c> dari <c>runtimes/&lt;rid&gt;/native</c> di samping aplikasi bila
/// pencarian bawaan .NET tidak menemukannya.
/// </summary>
/// <remarks>
/// Paket NuGet runtime tidak butuh ini: host .NET sudah memetakan aset native-nya lewat
/// deps.json. Resolver ini untuk build di dalam repo dan tool CLI, yang membawa pustaka
/// linux-x64 dan linux-arm64 sekaligus. Keduanya bernama <c>libdepthai-c.so</c>, jadi tidak
/// bisa diletakkan rata di folder aplikasi; folder per RID memisahkannya.
/// </remarks>
internal static class NativeLibraryResolver
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Resolver harus terpasang sebelum P/Invoke pertama, dan hanya memengaruhi assembly ini.")]
    internal static void Register()
        => NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName)
        {
            return IntPtr.Zero;
        }

        // Resolver dipanggil sebelum pencarian bawaan. IntPtr.Zero berarti "lanjutkan seperti
        // biasa", jadi bila folder per RID tidak ada (Windows, paket NuGet) perilakunya tidak
        // berubah, dan bila pustakanya memang tidak ada .NET tetap melempar
        // DllNotFoundException sehingga SDK jatuh ke simulasi.
        var candidate = Path.Combine(
            AppContext.BaseDirectory, "runtimes", CurrentRid, "native", NativeRuntime.ExpectedLibraryFileName);

        return File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>RID portabel proses ini, mis. <c>linux-arm64</c>.</summary>
    internal static string CurrentRid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            return $"{os}-{arch}";
        }
    }
}
