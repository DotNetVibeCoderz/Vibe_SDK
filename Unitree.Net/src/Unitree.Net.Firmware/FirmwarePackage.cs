using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unitree.Net.Core;

namespace Unitree.Net.Firmware;

/// <summary>
/// Which subsystem a firmware image targets.
/// </summary>
public enum FirmwareComponent
{
    /// <summary>The main on-board computer.</summary>
    MainController,

    /// <summary>A joint motor controller.</summary>
    MotorController,

    /// <summary>The battery management system.</summary>
    BatteryManagement,

    /// <summary>The LiDAR unit.</summary>
    Lidar,

    /// <summary>The wireless remote receiver.</summary>
    RemoteReceiver,
}

/// <summary>
/// A firmware image plus the metadata needed to verify and target it.
/// </summary>
/// <param name="Component">Which subsystem the image is for.</param>
/// <param name="Version">Semantic version of the image.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the payload.</param>
/// <param name="SizeBytes">Payload size.</param>
/// <param name="SupportedModels">Robot models this image may be applied to.</param>
/// <param name="MinimumCurrentVersion">
/// Lowest currently-installed version this image may upgrade from, or <see langword="null"/> for any.
/// </param>
/// <param name="ReleaseNotes">Human-readable notes.</param>
public sealed record FirmwareManifest(
    FirmwareComponent Component,
    string Version,
    string Sha256,
    long SizeBytes,
    IReadOnlyList<RobotModel> SupportedModels,
    string? MinimumCurrentVersion = null,
    string? ReleaseNotes = null)
{
    /// <summary>Whether this image may be applied to <paramref name="model"/>.</summary>
    public bool SupportsModel(RobotModel model) => SupportedModels.Contains(model);
}

/// <summary>
/// A firmware package on disk: a manifest plus its payload.
/// </summary>
public sealed class FirmwarePackage
{
    private FirmwarePackage(FirmwareManifest manifest, string payloadPath)
    {
        Manifest = manifest;
        PayloadPath = payloadPath;
    }

    /// <summary>Metadata describing the image.</summary>
    public FirmwareManifest Manifest { get; }

    /// <summary>Path to the payload file.</summary>
    public string PayloadPath { get; }

    /// <summary>
    /// Loads a package from a directory containing <c>manifest.json</c> and <c>payload.bin</c>.
    /// </summary>
    /// <exception cref="FirmwareException">The package is missing files or fails verification.</exception>
    public static async Task<FirmwarePackage> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string manifestPath = Path.Combine(directory, "manifest.json");
        string payloadPath = Path.Combine(directory, "payload.bin");

        if (!File.Exists(manifestPath))
        {
            throw new FirmwareException($"No manifest.json found in '{directory}'.");
        }

        if (!File.Exists(payloadPath))
        {
            throw new FirmwareException($"No payload.bin found in '{directory}'.");
        }

        await using FileStream manifestStream = File.OpenRead(manifestPath);

        FirmwareManifest? manifest = await JsonSerializer
            .DeserializeAsync(manifestStream, FirmwareJsonContext.Default.FirmwareManifest, cancellationToken)
            .ConfigureAwait(false);

        if (manifest is null)
        {
            throw new FirmwareException($"manifest.json in '{directory}' is empty or malformed.");
        }

        var package = new FirmwarePackage(manifest, payloadPath);
        await package.VerifyAsync(cancellationToken).ConfigureAwait(false);
        return package;
    }

    /// <summary>
    /// Verifies the payload's size and SHA-256 against the manifest.
    /// </summary>
    /// <exception cref="FirmwareException">Verification failed.</exception>
    /// <remarks>
    /// Verification happens on load and again immediately before upload. Checking twice is cheap next to
    /// the cost of flashing a corrupted image onto a motor controller.
    /// </remarks>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(PayloadPath);

        if (!file.Exists)
        {
            throw new FirmwareException($"Payload '{PayloadPath}' no longer exists.");
        }

        if (file.Length != Manifest.SizeBytes)
        {
            throw new FirmwareException(
                $"Payload size mismatch for {Manifest.Component} {Manifest.Version}: " +
                $"manifest says {Manifest.SizeBytes} bytes, file is {file.Length}.");
        }

        string actual = await ComputeSha256Async(PayloadPath, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmwareException(
                $"Payload checksum mismatch for {Manifest.Component} {Manifest.Version}: " +
                $"expected {Manifest.Sha256}, computed {actual}.");
        }
    }

    /// <summary>Opens the payload for reading.</summary>
    public Stream OpenPayload() =>
        new FileStream(PayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

    /// <summary>Computes the lowercase hex SHA-256 of a file.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>
/// Raised when a firmware operation cannot proceed.
/// </summary>
public sealed class FirmwareException : UnitreeException
{
    /// <summary>Creates an instance with a message.</summary>
    public FirmwareException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an instance with a message and inner cause.</summary>
    public FirmwareException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Source-generated JSON context for firmware metadata.
/// </summary>
/// <remarks>
/// Source generation rather than reflection keeps this usable from a trimmed or native-AOT deployment,
/// which matters for the robot-side hosts this SDK targets.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = [typeof(JsonStringEnumConverter<FirmwareComponent>), typeof(JsonStringEnumConverter<RobotModel>)])]
[JsonSerializable(typeof(FirmwareManifest))]
[JsonSerializable(typeof(FirmwareInstallRecord))]
[JsonSerializable(typeof(List<FirmwareInstallRecord>))]
internal sealed partial class FirmwareJsonContext : JsonSerializerContext;
