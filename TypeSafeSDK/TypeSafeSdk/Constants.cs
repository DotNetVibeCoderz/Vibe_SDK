namespace TypeSafeSdk;

/// <summary>Konstanta publik SDK, sejajar dengan modul <c>typesafe_sdk.constants</c> pada Python SDK.</summary>
public static class TypeSafeConstants
{
    /// <summary>Nama environment variable untuk API key.</summary>
    public const string ApiKeyEnv = "TYPESAFE_API_KEY";
    /// <summary>Nama environment variable untuk base URL.</summary>
    public const string BaseUrlEnv = "TYPESAFE_BASE_URL";
    /// <summary>Nama environment variable untuk model default.</summary>
    public const string DefaultModelEnv = "TYPESAFE_DEFAULT_MODEL";
    /// <summary>Nama environment variable untuk level log.</summary>
    public const string LogLevelEnv = "TYPESAFE_LOG_LEVEL";
    /// <summary>Nama environment variable untuk mengaktifkan simulator lokal.</summary>
    public const string SimulatorEnv = "TYPESAFE_SIMULATOR";
    /// <summary>Nama environment variable untuk path file API key.</summary>
    public const string ApiKeyFileEnv = "TYPESAFE_API_KEY_FILE";

    /// <summary>Base URL default API TypeSafe.</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";
    /// <summary>Model default yang dipakai ketika pemanggil tidak menentukan model.</summary>
    public const string DefaultModel = "jev-latest";
    /// <summary>Timeout default per request HTTP.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Path endpoint System One.</summary>
    public const string SystemOnePath = "v1/systemone";
    /// <summary>Path endpoint daftar model.</summary>
    public const string ModelsPath = "v1/models";

    /// <summary>Nama SDK yang dikirim pada header identifikasi.</summary>
    public const string SdkName = "typesafe-sdk-dotnet";
    /// <summary>Versi SDK, satu sumber kebenaran untuk header dan paket NuGet.</summary>
    public const string SdkVersion = "1.1.0";

    /// <summary>Header identifikasi SDK.</summary>
    public const string SdkHeader = "X-TypeSafe-SDK";
    /// <summary>Header identifikasi runtime pemanggil.</summary>
    public const string RuntimeHeader = "X-TypeSafe-Runtime";
    /// <summary>Header jumlah percobaan ulang pada request saat ini.</summary>
    public const string RetryCountHeader = "X-TypeSafe-Retry-Count";
    /// <summary>Header request id yang dikembalikan API.</summary>
    public const string RequestIdHeader = "x-typesafe-request-id";
    /// <summary>Header <c>Retry-After</c> dalam detik.</summary>
    public const string RetryAfterHeader = "retry-after";
    /// <summary>Header <c>Retry-After</c> dalam milidetik.</summary>
    public const string RetryAfterMsHeader = "retry-after-ms";

    /// <summary>Panjang maksimum body error yang disertakan pada pesan exception.</summary>
    public const int MaxErrorBodyLength = 200;

    /// <summary>Header yang tidak pernah boleh muncul pada log.</summary>
    public static readonly IReadOnlySet<string> SecretHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "authorization", "proxy-authorization", "x-api-key", "api-key", "cookie", "set-cookie" };

    /// <summary>Nilai header <c>X-TypeSafe-SDK</c>.</summary>
    public static string SdkHeaderValue => $"{SdkName}/{SdkVersion}";
    /// <summary>Nilai header <c>X-TypeSafe-Runtime</c>.</summary>
    public static string RuntimeHeaderValue => $".NET/{Environment.Version}";

    /// <summary>Menyensor nilai header rahasia agar aman untuk log.</summary>
    public static string RedactHeader(string name, string value) => SecretHeaders.Contains(name) ? "***" : value;
}
