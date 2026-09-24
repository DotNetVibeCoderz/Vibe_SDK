using System.Net;

namespace TypeSafeSdk;

/// <summary>Akar seluruh error SDK TypeSafe.</summary>
public class TypeSafeException : Exception
{
    public TypeSafeException(string message) : base(message) { }
    public TypeSafeException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Response HTTP yang tidak sukses dari API TypeSafe.</summary>
public class TypeSafeApiException : TypeSafeException
{
    /// <summary>Kode status HTTP. Bernilai 0 untuk kegagalan sebelum request terkirim.</summary>
    public int StatusCode { get; }
    /// <summary>Body response mentah, dipangkas pada pesan tetapi utuh di sini.</summary>
    public string? Body { get; }
    /// <summary>Header response, kosong bila request gagal sebelum ada response.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
    /// <summary>Endpoint yang dipanggil ketika error terjadi.</summary>
    public string? Endpoint { get; }
    /// <summary>Nilai header <c>x-typesafe-request-id</c> untuk korelasi observability.</summary>
    public string? RequestId => Headers.TryGetValue(TypeSafeConstants.RequestIdHeader, out var id) ? id : null;

    public TypeSafeApiException(int statusCode, string message) : this(statusCode, message, null, null, null) { }

    public TypeSafeApiException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
        : base(message)
    {
        StatusCode = statusCode; Body = body ?? message; Endpoint = endpoint;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Endpoint ?? "typesafe"}: {StatusCode} {Message}{(RequestId is null ? "" : $" (request_id={RequestId})")}";
}

/// <summary>HTTP 400 — request tidak valid.</summary>
public sealed class TypeSafeBadRequestException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>HTTP 401 — API key tidak valid atau tidak dikirim.</summary>
public sealed class TypeSafeAuthenticationException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>HTTP 403 — akses ditolak untuk resource tersebut.</summary>
public sealed class TypeSafePermissionDeniedException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>HTTP 404 — resource tidak ditemukan.</summary>
public sealed class TypeSafeNotFoundException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>HTTP 422 — payload valid secara sintaks tetapi ditolak secara semantik.</summary>
public sealed class TypeSafeUnprocessableEntityException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>HTTP 429 — kuota rate limit terlampaui.</summary>
public sealed class TypeSafeRateLimitException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint)
{
    /// <summary>Jeda yang disarankan server, dibaca dari <c>retry-after-ms</c> atau <c>retry-after</c>.</summary>
    public TimeSpan? RetryAfter => ReadRetryAfter(Headers);

    internal static TimeSpan? ReadRetryAfter(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue(TypeSafeConstants.RetryAfterMsHeader, out var ms) && double.TryParse(ms, out var milliseconds) && milliseconds >= 0)
            return TimeSpan.FromMilliseconds(milliseconds);
        if (headers.TryGetValue(TypeSafeConstants.RetryAfterHeader, out var s) && double.TryParse(s, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        return null;
    }
}

/// <summary>HTTP 5xx — kegagalan sisi server.</summary>
public sealed class TypeSafeInternalServerException(int statusCode, string message, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    : TypeSafeApiException(statusCode, message, body, headers, endpoint);

/// <summary>Request gagal tanpa sempat menerima response HTTP.</summary>
public class TypeSafeApiConnectionException(string message, Exception? innerException = null) : TypeSafeException(message, innerException);

/// <summary>Request melewati batas timeout yang dikonfigurasi.</summary>
public sealed class TypeSafeApiTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : TypeSafeApiConnectionException($"Request timed out (timeout={timeout.TotalSeconds:0.##}s).", innerException)
{
    /// <summary>Timeout efektif yang berlaku saat request dibatalkan.</summary>
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>Response sukses tetapi strukturnya tidak sesuai kontrak API.</summary>
public sealed class TypeSafeApiResponseValidationException(string fieldPath, string? body = null, Exception? innerException = null)
    : TypeSafeException($"Invalid response data at '{fieldPath}'.", innerException)
{
    /// <summary>Path field yang gagal divalidasi, misalnya <c>answers.tone.confidence</c>.</summary>
    public string FieldPath { get; } = fieldPath;
    /// <summary>Body response mentah yang gagal diparsing.</summary>
    public string? Body { get; } = body;
}

/// <summary>Pemetaan status HTTP ke tipe exception spesifik, sejajar dengan <c>api_error()</c> Python SDK.</summary>
public static class TypeSafeErrorFactory
{
    /// <summary>Membangun exception yang tepat untuk sebuah response gagal.</summary>
    public static TypeSafeApiException Create(int statusCode, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
    {
        var message = Summarize(statusCode, body);
        return statusCode switch
        {
            400 => new TypeSafeBadRequestException(statusCode, message, body, headers, endpoint),
            401 => new TypeSafeAuthenticationException(statusCode, message, body, headers, endpoint),
            403 => new TypeSafePermissionDeniedException(statusCode, message, body, headers, endpoint),
            404 => new TypeSafeNotFoundException(statusCode, message, body, headers, endpoint),
            422 => new TypeSafeUnprocessableEntityException(statusCode, message, body, headers, endpoint),
            429 => new TypeSafeRateLimitException(statusCode, message, body, headers, endpoint),
            >= 500 and <= 599 => new TypeSafeInternalServerException(statusCode, message, body, headers, endpoint),
            _ => new TypeSafeApiException(statusCode, message, body, headers, endpoint)
        };
    }

    internal static TypeSafeApiException Create(HttpStatusCode statusCode, string? body, IReadOnlyDictionary<string, string>? headers, string? endpoint)
        => Create((int)statusCode, body, headers, endpoint);

    private static string Summarize(int statusCode, string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return $"TypeSafe API returned HTTP {statusCode}.";
        return text.Length <= TypeSafeConstants.MaxErrorBodyLength ? text : text[..TypeSafeConstants.MaxErrorBodyLength] + "…";
    }
}
