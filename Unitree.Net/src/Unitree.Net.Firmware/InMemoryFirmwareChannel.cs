using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unitree.Net.Firmware;

/// <summary>
/// An in-memory firmware channel for tests and dry runs.
/// </summary>
/// <remarks>
/// Models the two-slot arrangement real OTA systems use: an active image and a previous one that
/// rollback restores. Failure modes can be injected so that the manager's rollback path is exercisable
/// without a robot.
/// </remarks>
public sealed class InMemoryFirmwareChannel(ILogger? logger = null) : IFirmwareChannel
{
    private readonly Dictionary<FirmwareComponent, string> _active = new();
    private readonly Dictionary<FirmwareComponent, string> _previous = new();
    private readonly Dictionary<FirmwareComponent, byte[]> _staged = new();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Makes <see cref="ActivateAsync"/> throw, to exercise the rollback path.</summary>
    public bool FailActivation { get; set; }

    /// <summary>Makes <see cref="VerifyHealthAsync"/> report unhealthy, to exercise the rollback path.</summary>
    public bool FailHealthCheck { get; set; }

    /// <summary>Makes <see cref="RollbackAsync"/> throw, to exercise the unrecoverable path.</summary>
    public bool FailRollback { get; set; }

    /// <summary>Bytes staged per component, for assertions.</summary>
    public IReadOnlyDictionary<FirmwareComponent, byte[]> StagedPayloads => _staged;

    /// <summary>Seeds the currently installed version of a component.</summary>
    public void SetInstalledVersion(FirmwareComponent component, string version) => _active[component] = version;

    /// <inheritdoc />
    public Task<string> GetInstalledVersionAsync(
        FirmwareComponent component,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_active.TryGetValue(component, out string? version) ? version : "0.0.0");

    /// <inheritdoc />
    public async Task StageAsync(
        FirmwareManifest manifest,
        Stream payload,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long copied = 0;
        int read;

        while ((read = await payload.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            if (manifest.SizeBytes > 0)
            {
                progress?.Report(Math.Min(1.0, copied / (double)manifest.SizeBytes));
            }
        }

        _staged[manifest.Component] = buffer.ToArray();
        _logger.LogInformation("Staged {Bytes:N0} bytes for {Component}.", copied, manifest.Component);
    }

    /// <inheritdoc />
    public Task ActivateAsync(FirmwareComponent component, string version, CancellationToken cancellationToken = default)
    {
        if (FailActivation)
        {
            throw new FirmwareException($"Injected activation failure for {component}.");
        }

        if (!_staged.ContainsKey(component))
        {
            throw new FirmwareException($"Nothing is staged for {component}; call StageAsync first.");
        }

        if (_active.TryGetValue(component, out string? current))
        {
            _previous[component] = current;
        }

        _active[component] = version;
        _staged.Remove(component);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(FirmwareComponent component, CancellationToken cancellationToken = default)
    {
        if (FailRollback)
        {
            throw new FirmwareException($"Injected rollback failure for {component}.");
        }

        if (!_previous.TryGetValue(component, out string? previous))
        {
            throw new FirmwareException($"No previous image is retained for {component}.");
        }

        _active[component] = previous;
        _previous.Remove(component);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> VerifyHealthAsync(FirmwareComponent component, CancellationToken cancellationToken = default) =>
        Task.FromResult(!FailHealthCheck);
}
