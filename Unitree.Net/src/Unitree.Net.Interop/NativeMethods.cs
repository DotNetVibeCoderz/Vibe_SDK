using System.Runtime.InteropServices;

namespace Unitree.Net.Interop;

/// <summary>
/// Status codes returned by the native shim.
/// </summary>
public enum NativeStatus
{
    /// <summary>The call succeeded.</summary>
    Ok = 0,

    /// <summary>The shim has not been initialised, or was already shut down.</summary>
    NotInitialised = -1,

    /// <summary>An argument was null or otherwise invalid.</summary>
    InvalidArgument = -2,

    /// <summary>The requested DDS type name is not in the shim's descriptor registry.</summary>
    UnknownType = -3,

    /// <summary>Cyclone DDS refused to create the participant, topic, or endpoint.</summary>
    DdsError = -4,

    /// <summary>The endpoint handle does not exist.</summary>
    UnknownHandle = -5,

    /// <summary>A memory allocation failed.</summary>
    OutOfMemory = -6,
}

/// <summary>
/// Invoked by the native shim when a sample arrives.
/// </summary>
/// <param name="topic">Null-terminated UTF-8 topic name.</param>
/// <param name="data">Pointer to the serialised CDR payload, including the encapsulation header.</param>
/// <param name="length">Payload length in bytes.</param>
/// <param name="userData">The opaque pointer supplied at reader creation.</param>
/// <remarks>
/// This runs on a Cyclone DDS listener thread. The buffer is only valid for the duration of the call,
/// so anything retained must be copied out.
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public unsafe delegate void NativeMessageCallback(byte* topic, byte* data, int length, nint userData);

/// <summary>
/// P/Invoke surface of <c>unitree_net_native</c>, the Cyclone DDS shim.
/// </summary>
/// <remarks>
/// <para>
/// The shim exists because Unitree robots speak RTPS with the <c>unitree_go</c> / <c>unitree_hg</c> IDL
/// types, and DDS requires a registered type descriptor per topic — there is no supported way to
/// publish opaque bytes onto a typed topic from managed code alone. The shim registers the generated
/// descriptors and exchanges pre-serialised CDR with them, which keeps all encoding in C# where it can
/// be tested.
/// </para>
/// <para>
/// Build instructions are in <c>native/README.md</c>. The library is not required unless the
/// <c>CycloneNative</c> transport is selected.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>The native library name, resolved per-platform by the default probing rules.</summary>
    internal const string LibraryName = "unitree_net_native";

    /// <summary>Initialises the shim and joins the DDS domain.</summary>
    /// <param name="domainId">The DDS domain identifier.</param>
    /// <param name="networkInterface">Interface name to bind, or null to let Cyclone choose.</param>
    [LibraryImport(LibraryName, EntryPoint = "un_init", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int Init(int domainId, string? networkInterface);

    /// <summary>Tears down every endpoint and leaves the domain.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int Shutdown();

    /// <summary>Creates a writer for a topic.</summary>
    /// <param name="topic">DDS topic name, e.g. <c>rt/lowcmd</c>.</param>
    /// <param name="typeName">Registered type name, e.g. <c>unitree_go::msg::dds_::LowCmd_</c>.</param>
    /// <param name="handle">Receives the endpoint handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "un_create_writer", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int CreateWriter(string topic, string typeName, out int handle);

    /// <summary>Publishes a pre-serialised CDR payload through a writer.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_write")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int Write(int handle, byte* data, int length);

    /// <summary>Creates a reader for a topic, delivering raw CDR to <paramref name="callback"/>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_create_reader", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int CreateReader(
        string topic,
        string typeName,
        nint callback,
        nint userData,
        out int handle);

    /// <summary>Destroys a reader or writer.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_destroy_endpoint")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int DestroyEndpoint(int handle);

    /// <summary>Gets the most recent native error message.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial byte* LastError();

    /// <summary>Gets the shim version string.</summary>
    [LibraryImport(LibraryName, EntryPoint = "un_version")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial byte* Version();

    /// <summary>Reads a null-terminated UTF-8 string returned by the shim.</summary>
    internal static string ReadUtf8(byte* pointer) =>
        pointer is null ? string.Empty : Marshal.PtrToStringUTF8((nint)pointer) ?? string.Empty;
}
