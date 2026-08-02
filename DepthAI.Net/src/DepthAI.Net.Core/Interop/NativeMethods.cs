using System.Runtime.InteropServices;

namespace DepthAI.Interop;

/// <summary>Kode status yang dikembalikan shim C.</summary>
internal enum DaiStatus
{
    Ok = 0,
    InvalidArgument = -1,
    DeviceNotFound = -2,
    DeviceBusy = -3,
    Timeout = -4,
    PipelineError = -5,
    CommunicationError = -6,
    NotSupported = -7,
    Unknown = -100,
}

/// <summary>Jenis paket pada level ABI; dipetakan ke <see cref="Backends.PacketKind"/>.</summary>
internal enum DaiPacketType
{
    Image = 0,
    Depth = 1,
    NeuralTensors = 2,
    Encoded = 3,
    Imu = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DaiDeviceInfo
{
    public fixed byte MxId[32];
    public fixed byte Name[64];
    public fixed byte ConnectionPath[128];
    public fixed byte FirmwareVersion[32];
    public int Protocol;
    public int State;
    public int UsbSpeed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DaiCapabilities
{
    public int ColorCameraCount;
    public int MonoCameraCount;
    public int SupportsStereoDepth;
    public int HasImu;
    public int ShaveCores;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DaiOpenOptions
{
    public int MaxUsbSpeed;
    public int BootTimeoutMs;
    public IntPtr FirmwarePath;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DaiPacket
{
    public fixed byte StreamName[64];
    public int Type;
    public IntPtr Data;
    public long DataLength;
    public int Width;
    public int Height;
    public int PixelFormat;
    public long SequenceNumber;
    public long TimestampNanos;

    /// <summary>Pegangan opsional yang harus dikembalikan ke <c>dai_packet_release</c>.</summary>
    public IntPtr NativeHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DaiTelemetry
{
    public float ChipTemperatureCelsius;
    public float LeonCssUsagePercent;
    public float LeonMssUsagePercent;
    public long DdrUsedBytes;
    public long DdrTotalBytes;
}

/// <summary>
/// Binding P/Invoke ke <c>depthai-c</c>, shim C ABI berumur panjang di atas depthai-core C++.
/// </summary>
/// <remarks>
/// <para>
/// depthai-core memaparkan API C++ dengan template dan tipe STL yang tidak punya ABI
/// stabil, jadi P/Invoke langsung ke sana rapuh. Lapisan ini menargetkan shim C tipis
/// (<c>depthai-c</c>) yang membungkus API C++ dengan tipe POD saja.
/// </para>
/// <para>
/// Model transfer datanya adalah <b>polling</b>, bukan callback: perangkat mengantre paket
/// dan host menariknya lewat <c>dai_device_poll</c>. Ini menghindari callback native→managed
/// pada jalur panas, yang mengharuskan menjaga delegate tetap hidup dan bisa menemui
/// masalah saat GC memindahkan thread.
/// </para>
/// <para>
/// Bila pustaka native tidak tersedia, <c>NativeBackend.IsAvailable</c> bernilai false dan
/// SDK memakai backend simulasi — bukan crash.
/// </para>
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>Nama pustaka tanpa awalan/akhiran; runtime menambahkannya per platform.</summary>
    internal const string LibraryName = "depthai-c";

    [LibraryImport(LibraryName, EntryPoint = "dai_get_version")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int GetVersion(Span<byte> buffer, int bufferLength);

    [LibraryImport(LibraryName, EntryPoint = "dai_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int GetLastError(Span<byte> buffer, int bufferLength);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_list")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ListDevices(Span<DaiDeviceInfo> buffer, int capacity, out int count);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_open", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int OpenDevice(string mxId, in DaiOpenOptions options, out IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_close")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int CloseDevice(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_capabilities")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int GetCapabilities(IntPtr handle, out DaiCapabilities capabilities);

    /// <summary>Mengunggah pipeline sebagai JSON dan memulai eksekusi.</summary>
    [LibraryImport(LibraryName, EntryPoint = "dai_device_start_pipeline", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int StartPipeline(IntPtr handle, string pipelineJson);

    /// <summary>Mengunggah payload model yang dirujuk node NN berdasarkan nama.</summary>
    [LibraryImport(LibraryName, EntryPoint = "dai_device_upload_model", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int UploadModel(IntPtr handle, string modelName, ReadOnlySpan<byte> payload, long length);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_stop_pipeline")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int StopPipeline(IntPtr handle);

    /// <summary>
    /// Menarik satu paket. Mengembalikan <see cref="DaiStatus.Timeout"/> bila tidak ada
    /// paket dalam <paramref name="timeoutMs"/> — kondisi normal, bukan error.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "dai_device_poll")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int PollPacket(IntPtr handle, out DaiPacket packet, int timeoutMs);

    /// <summary>Mengembalikan buffer paket ke pool native. Wajib untuk tiap poll yang sukses.</summary>
    [LibraryImport(LibraryName, EntryPoint = "dai_packet_release")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ReleasePacket(IntPtr handle, IntPtr nativeHandle);

    [LibraryImport(LibraryName, EntryPoint = "dai_device_telemetry")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int GetTelemetry(IntPtr handle, out DaiTelemetry telemetry);
}
