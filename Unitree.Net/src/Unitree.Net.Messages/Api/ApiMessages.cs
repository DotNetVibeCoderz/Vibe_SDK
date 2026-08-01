using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Messages.Api;

/// <summary>
/// Identifies a single request/response exchange, matching <c>unitree_api::msg::dds_::RequestIdentity_</c>.
/// </summary>
/// <param name="Id">Caller-generated correlation identifier.</param>
/// <param name="ApiId">The Unitree API being invoked.</param>
public readonly record struct RequestIdentity(long Id, long ApiId);

/// <summary>
/// Lease information, matching <c>unitree_api::msg::dds_::RequestLease_</c>.
/// </summary>
/// <param name="Id">Lease identifier; zero for stateless calls.</param>
public readonly record struct RequestLease(long Id);

/// <summary>
/// Delivery policy, matching <c>unitree_api::msg::dds_::RequestPolicy_</c>.
/// </summary>
/// <param name="Priority">Scheduling priority on the robot.</param>
/// <param name="NoReply">When set, the robot performs the action without publishing a response.</param>
public readonly record struct RequestPolicy(int Priority, bool NoReply);

/// <summary>
/// Envelope header for a service request.
/// </summary>
/// <param name="Identity">Correlation and API identifiers.</param>
/// <param name="Lease">Lease information.</param>
/// <param name="Policy">Delivery policy.</param>
public readonly record struct RequestHeader(RequestIdentity Identity, RequestLease Lease, RequestPolicy Policy);

/// <summary>
/// A service request published on an <c>rt/api/&lt;service&gt;/request</c> topic, matching
/// <c>unitree_api::msg::dds_::Request_</c>.
/// </summary>
/// <remarks>
/// The <see cref="Parameter"/> field carries a JSON document whose shape depends on the API being
/// invoked. Unitree's own clients send compact JSON with no whitespace; the firmware tolerates either.
/// </remarks>
public sealed class ApiRequest : ICdrSerializable<ApiRequest>
{
    /// <summary>
    /// Upper bound on the encoded size. Requests carrying large binary payloads must be sized explicitly.
    /// </summary>
    public const int DefaultMaxSize = 8 * 1024;

    /// <summary>The request envelope header.</summary>
    public RequestHeader Header { get; set; }

    /// <summary>JSON parameter document. Empty when the API takes no arguments.</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>Optional binary payload.</summary>
    public ReadOnlyMemory<byte> Binary { get; set; }

    /// <inheritdoc />
    public static string DdsTypeName => "unitree_api::msg::dds_::Request_";

    /// <inheritdoc />
    public static int MaxSerializedSize => DefaultMaxSize;

    /// <summary>
    /// Creates a request for <paramref name="apiId"/>.
    /// </summary>
    /// <param name="apiId">The Unitree API identifier.</param>
    /// <param name="parameter">JSON parameter document, or <see langword="null"/> for none.</param>
    /// <param name="requestId">Correlation identifier. Defaults to a monotonic value.</param>
    /// <param name="noReply">Whether to suppress the response.</param>
    public static ApiRequest Create(long apiId, string? parameter = null, long requestId = 0, bool noReply = false) => new()
    {
        Header = new RequestHeader(
            new RequestIdentity(requestId == 0 ? RequestIdGenerator.Next() : requestId, apiId),
            new RequestLease(0),
            new RequestPolicy(0, noReply)),
        Parameter = parameter ?? string.Empty,
    };

    /// <inheritdoc />
    public int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);

        writer.WriteInt64(Header.Identity.Id);
        writer.WriteInt64(Header.Identity.ApiId);
        writer.WriteInt64(Header.Lease.Id);
        writer.WriteInt32(Header.Policy.Priority);
        writer.WriteBool(Header.Policy.NoReply);
        writer.WriteString(Parameter);
        writer.WriteByteSequence(Binary.Span);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static ApiRequest Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);

        long id = reader.ReadInt64();
        long apiId = reader.ReadInt64();
        long leaseId = reader.ReadInt64();
        int priority = reader.ReadInt32();
        bool noReply = reader.ReadBool();
        string parameter = reader.ReadString();
        byte[] binary = reader.ReadByteSequence().ToArray();

        return new ApiRequest
        {
            Header = new RequestHeader(
                new RequestIdentity(id, apiId),
                new RequestLease(leaseId),
                new RequestPolicy(priority, noReply)),
            Parameter = parameter,
            Binary = binary,
        };
    }
}

/// <summary>
/// Result status of a service call, matching <c>unitree_api::msg::dds_::ResponseStatus_</c>.
/// </summary>
/// <param name="Code">Zero on success; any other value is an error specific to the API.</param>
public readonly record struct ResponseStatus(int Code)
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess => Code == 0;
}

/// <summary>
/// A service response published on an <c>rt/api/&lt;service&gt;/response</c> topic, matching
/// <c>unitree_api::msg::dds_::Response_</c>.
/// </summary>
public sealed class ApiResponse : ICdrSerializable<ApiResponse>
{
    /// <summary>Upper bound on the encoded size.</summary>
    public const int DefaultMaxSize = 64 * 1024;

    /// <summary>Correlation identifiers echoed from the request.</summary>
    public RequestIdentity Identity { get; set; }

    /// <summary>Call status.</summary>
    public ResponseStatus Status { get; set; }

    /// <summary>JSON response document.</summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>Optional binary payload.</summary>
    public ReadOnlyMemory<byte> Binary { get; set; }

    /// <inheritdoc />
    public static string DdsTypeName => "unitree_api::msg::dds_::Response_";

    /// <inheritdoc />
    public static int MaxSerializedSize => DefaultMaxSize;

    /// <summary>Throws <see cref="UnitreeServiceException"/> when <see cref="Status"/> indicates failure.</summary>
    public void EnsureSuccess()
    {
        if (!Status.IsSuccess)
        {
            throw new UnitreeServiceException(Identity.ApiId, Status.Code);
        }
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);

        writer.WriteInt64(Identity.Id);
        writer.WriteInt64(Identity.ApiId);
        writer.WriteInt32(Status.Code);
        writer.WriteString(Data);
        writer.WriteByteSequence(Binary.Span);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static ApiResponse Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);

        long id = reader.ReadInt64();
        long apiId = reader.ReadInt64();
        int code = reader.ReadInt32();
        string data = reader.ReadString();
        byte[] binary = reader.ReadByteSequence().ToArray();

        return new ApiResponse
        {
            Identity = new RequestIdentity(id, apiId),
            Status = new ResponseStatus(code),
            Data = data,
            Binary = binary,
        };
    }
}

/// <summary>
/// Produces process-unique request correlation identifiers.
/// </summary>
internal static class RequestIdGenerator
{
    private static long _counter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Gets the next identifier.</summary>
    internal static long Next() => Interlocked.Increment(ref _counter);
}
