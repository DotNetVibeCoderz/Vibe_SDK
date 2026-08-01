using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;

namespace Unitree.Net.Firmware;

/// <summary>
/// The channel through which firmware reaches the robot.
/// </summary>
/// <remarks>
/// <para>
/// Unitree does not publish a documented OTA protocol, and it differs between platforms and firmware
/// generations. This interface is the seam: <see cref="FirmwareManager"/> owns the parts that are the
/// same everywhere — verification, staging order, health gating, rollback — and a concrete channel
/// carries the bytes for a specific robot.
/// </para>
/// <para>
/// Implementations must be idempotent for <see cref="ActivateAsync"/>: the manager may retry after a
/// transport failure without knowing whether the previous attempt landed.
/// </para>
/// </remarks>
public interface IFirmwareChannel
{
    /// <summary>Reads the version currently installed for <paramref name="component"/>.</summary>
    Task<string> GetInstalledVersionAsync(FirmwareComponent component, CancellationToken cancellationToken = default);

    /// <summary>Uploads a payload to the robot's staging area without activating it.</summary>
    /// <param name="manifest">Metadata for the image being staged.</param>
    /// <param name="payload">The image bytes.</param>
    /// <param name="progress">Receives fraction complete, 0 to 1.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    Task StageAsync(
        FirmwareManifest manifest,
        Stream payload,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Activates a previously staged image, usually rebooting the component.</summary>
    Task ActivateAsync(FirmwareComponent component, string version, CancellationToken cancellationToken = default);

    /// <summary>Reverts <paramref name="component"/> to its previous image.</summary>
    Task RollbackAsync(FirmwareComponent component, CancellationToken cancellationToken = default);

    /// <summary>Whether the component is healthy after an update.</summary>
    Task<bool> VerifyHealthAsync(FirmwareComponent component, CancellationToken cancellationToken = default);
}

/// <summary>
/// A record of one install attempt.
/// </summary>
/// <param name="Component">Which subsystem was targeted.</param>
/// <param name="FromVersion">The version present before the attempt.</param>
/// <param name="ToVersion">The version being installed.</param>
/// <param name="StartedAt">When the attempt began.</param>
/// <param name="CompletedAt">When it finished, successfully or not.</param>
/// <param name="Outcome">The result.</param>
/// <param name="Detail">Failure detail, if any.</param>
public sealed record FirmwareInstallRecord(
    FirmwareComponent Component,
    string FromVersion,
    string ToVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    FirmwareInstallOutcome Outcome,
    string? Detail = null);

/// <summary>
/// How an install attempt ended.
/// </summary>
public enum FirmwareInstallOutcome
{
    /// <summary>Still running.</summary>
    InProgress,

    /// <summary>Installed and verified healthy.</summary>
    Succeeded,

    /// <summary>Failed before anything was activated; the robot is unchanged.</summary>
    FailedBeforeActivation,

    /// <summary>Failed after activation and was rolled back successfully.</summary>
    RolledBack,

    /// <summary>Failed after activation and the rollback also failed. Manual recovery is required.</summary>
    RollbackFailed,

    /// <summary>Skipped because the installed version already matched.</summary>
    AlreadyInstalled,
}

/// <summary>
/// Installs firmware with verification, health gating and automatic rollback.
/// </summary>
/// <remarks>
/// <para>
/// The install sequence is deliberately conservative, because a robot bricked by a bad flash is an
/// expensive mistake: verify the package, confirm model and version compatibility, stage the bytes
/// without activating, activate, then check health. Only a passing health check makes the update final;
/// anything else triggers a rollback.
/// </para>
/// <para>
/// Every attempt is journalled to disk before it starts, so an install interrupted by a power loss is
/// still visible afterwards.
/// </para>
/// </remarks>
public sealed class FirmwareManager
{
    private readonly IFirmwareChannel _channel;
    private readonly RobotModel _model;
    private readonly string _journalPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>Creates a manager for <paramref name="model"/> over <paramref name="channel"/>.</summary>
    /// <param name="channel">The channel carrying firmware to the robot.</param>
    /// <param name="model">The robot being updated, used for compatibility checks.</param>
    /// <param name="journalPath">Where install history is written.</param>
    /// <param name="logger">Logger.</param>
    public FirmwareManager(
        IFirmwareChannel channel,
        RobotModel model,
        string journalPath,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        _channel = channel;
        _model = model;
        _journalPath = journalPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How long to wait after activation before checking health.</summary>
    public TimeSpan ActivationSettleDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Installs <paramref name="package"/>, rolling back if the result is unhealthy.
    /// </summary>
    /// <param name="package">The package to install.</param>
    /// <param name="progress">Receives upload progress, 0 to 1.</param>
    /// <param name="force">Install even when the version already matches.</param>
    /// <param name="cancellationToken">Cancels before activation; cancellation after activation is ignored.</param>
    public async Task<FirmwareInstallRecord> InstallAsync(
        FirmwarePackage package,
        IProgress<double>? progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await InstallCoreAsync(package, progress, force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task<FirmwareInstallRecord> InstallCoreAsync(
        FirmwarePackage package,
        IProgress<double>? progress,
        bool force,
        CancellationToken cancellationToken)
    {
        FirmwareManifest manifest = package.Manifest;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        if (!manifest.SupportsModel(_model))
        {
            throw new FirmwareException(
                $"Package {manifest.Component} {manifest.Version} does not list {_model} among its supported models " +
                $"({string.Join(", ", manifest.SupportedModels)}).");
        }

        string currentVersion = await _channel
            .GetInstalledVersionAsync(manifest.Component, cancellationToken)
            .ConfigureAwait(false);

        if (!force && string.Equals(currentVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "{Component} is already at {Version}; nothing to do.",
                manifest.Component,
                manifest.Version);

            return await JournalAsync(new FirmwareInstallRecord(
                manifest.Component,
                currentVersion,
                manifest.Version,
                startedAt,
                DateTimeOffset.UtcNow,
                FirmwareInstallOutcome.AlreadyInstalled)).ConfigureAwait(false);
        }

        if (manifest.MinimumCurrentVersion is { } minimum &&
            CompareVersions(currentVersion, minimum) < 0)
        {
            throw new FirmwareException(
                $"{manifest.Component} is at {currentVersion} but {manifest.Version} requires at least {minimum}. " +
                "Install the intermediate release first.");
        }

        // Journal before touching the robot, so an interrupted install leaves a trace.
        await JournalAsync(new FirmwareInstallRecord(
            manifest.Component,
            currentVersion,
            manifest.Version,
            startedAt,
            null,
            FirmwareInstallOutcome.InProgress)).ConfigureAwait(false);

        try
        {
            await package.VerifyAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Staging {Component} {FromVersion} → {ToVersion} ({SizeBytes:N0} bytes).",
                manifest.Component,
                currentVersion,
                manifest.Version,
                manifest.SizeBytes);

            await using Stream payload = package.OpenPayload();
            await _channel.StageAsync(manifest, payload, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not FirmwareException)
        {
            _logger.LogError(ex, "Staging failed for {Component}; the robot is unchanged.", manifest.Component);

            return await JournalAsync(new FirmwareInstallRecord(
                manifest.Component,
                currentVersion,
                manifest.Version,
                startedAt,
                DateTimeOffset.UtcNow,
                FirmwareInstallOutcome.FailedBeforeActivation,
                ex.Message)).ConfigureAwait(false);
        }

        // Past this point cancellation is no longer honoured: interrupting between activation and the
        // health check is precisely how a half-updated component ends up unrecoverable.
        _logger.LogWarning(
            "Activating {Component} {Version}. Do not power off the robot until this completes.",
            manifest.Component,
            manifest.Version);

        try
        {
            await _channel.ActivateAsync(manifest.Component, manifest.Version, CancellationToken.None)
                .ConfigureAwait(false);

            await Task.Delay(ActivationSettleDelay, CancellationToken.None).ConfigureAwait(false);

            bool healthy = await _channel.VerifyHealthAsync(manifest.Component, CancellationToken.None)
                .ConfigureAwait(false);

            if (healthy)
            {
                _logger.LogInformation(
                    "{Component} updated to {Version} and reported healthy.",
                    manifest.Component,
                    manifest.Version);

                return await JournalAsync(new FirmwareInstallRecord(
                    manifest.Component,
                    currentVersion,
                    manifest.Version,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    FirmwareInstallOutcome.Succeeded)).ConfigureAwait(false);
            }

            return await RollBackAsync(manifest, currentVersion, startedAt, "health check failed after activation")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackAsync(manifest, currentVersion, startedAt, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<FirmwareInstallRecord> RollBackAsync(
        FirmwareManifest manifest,
        string previousVersion,
        DateTimeOffset startedAt,
        string reason)
    {
        _logger.LogError(
            "{Component} update to {Version} failed ({Reason}); rolling back to {PreviousVersion}.",
            manifest.Component,
            manifest.Version,
            reason,
            previousVersion);

        try
        {
            await _channel.RollbackAsync(manifest.Component, CancellationToken.None).ConfigureAwait(false);

            return await JournalAsync(new FirmwareInstallRecord(
                manifest.Component,
                previousVersion,
                manifest.Version,
                startedAt,
                DateTimeOffset.UtcNow,
                FirmwareInstallOutcome.RolledBack,
                reason)).ConfigureAwait(false);
        }
        catch (Exception rollbackException)
        {
            _logger.LogCritical(
                rollbackException,
                "Rollback of {Component} failed. The robot may be in an inconsistent state and needs manual recovery.",
                manifest.Component);

            return await JournalAsync(new FirmwareInstallRecord(
                manifest.Component,
                previousVersion,
                manifest.Version,
                startedAt,
                DateTimeOffset.UtcNow,
                FirmwareInstallOutcome.RollbackFailed,
                $"{reason}; rollback also failed: {rollbackException.Message}")).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the install journal, newest first.</summary>
    public async Task<IReadOnlyList<FirmwareInstallRecord>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(_journalPath);

        List<FirmwareInstallRecord>? records = await JsonSerializer
            .DeserializeAsync(stream, FirmwareJsonContext.Default.ListFirmwareInstallRecord, cancellationToken)
            .ConfigureAwait(false);

        records?.Reverse();
        return records ?? [];
    }

    private async Task<FirmwareInstallRecord> JournalAsync(FirmwareInstallRecord record)
    {
        try
        {
            List<FirmwareInstallRecord> records = [];

            if (File.Exists(_journalPath))
            {
                await using FileStream readStream = File.OpenRead(_journalPath);

                records = await JsonSerializer
                    .DeserializeAsync(readStream, FirmwareJsonContext.Default.ListFirmwareInstallRecord)
                    .ConfigureAwait(false) ?? [];
            }

            // An in-progress entry for the same attempt is replaced rather than duplicated, so the
            // journal holds one row per attempt with its final outcome.
            records.RemoveAll(existing =>
                existing.Component == record.Component &&
                existing.StartedAt == record.StartedAt);

            records.Add(record);

            string? directory = Path.GetDirectoryName(_journalPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using FileStream writeStream = File.Create(_journalPath);

            await JsonSerializer
                .SerializeAsync(writeStream, records, FirmwareJsonContext.Default.ListFirmwareInstallRecord)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A journal write failure must not abort an otherwise healthy update.
            _logger.LogWarning(ex, "Could not write the firmware journal at '{Path}'.", _journalPath);
        }

        return record;
    }

    /// <summary>
    /// Compares two dotted version strings numerically.
    /// </summary>
    /// <returns>Negative when <paramref name="left"/> is older, zero when equal, positive when newer.</returns>
    /// <remarks>
    /// <para>
    /// Deliberately lenient about segment counts and non-numeric noise: firmware versions in the wild
    /// are inconsistent, and refusing to install over an oddly-named version is worse than ordering it
    /// approximately. <c>2.0</c> and <c>2.0.0</c> therefore compare equal.
    /// </para>
    /// <para>
    /// Pre-release suffixes are the one place leniency is not acceptable. Following semver,
    /// <c>1.2.3-beta</c> sorts <em>before</em> <c>1.2.3</c>. Treating them as equal would let a beta
    /// satisfy a <see cref="FirmwareManifest.MinimumCurrentVersion"/> gate that exists precisely to
    /// require the released build.
    /// </para>
    /// </remarks>
    internal static int CompareVersions(string left, string right)
    {
        (string leftCore, string leftPreRelease) = SplitPreRelease(left);
        (string rightCore, string rightPreRelease) = SplitPreRelease(right);

        string[] leftParts = leftCore.Split('.');
        string[] rightParts = rightCore.Split('.');
        int length = Math.Max(leftParts.Length, rightParts.Length);

        for (int i = 0; i < length; i++)
        {
            int leftValue = i < leftParts.Length && int.TryParse(leftParts[i], out int l) ? l : 0;
            int rightValue = i < rightParts.Length && int.TryParse(rightParts[i], out int r) ? r : 0;

            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        // Equal cores: absence of a pre-release suffix outranks its presence.
        return (leftPreRelease.Length, rightPreRelease.Length) switch
        {
            (0, 0) => 0,
            (0, _) => 1,
            (_, 0) => -1,
            _ => string.CompareOrdinal(leftPreRelease, rightPreRelease),
        };
    }

    /// <summary>
    /// Splits a version into its numeric core and its pre-release suffix.
    /// </summary>
    private static (string Core, string PreRelease) SplitPreRelease(string version)
    {
        int separator = version.IndexOfAny(['-', '_', '+']);

        return separator < 0
            ? (version, string.Empty)
            : (version[..separator], version[(separator + 1)..]);
    }
}
