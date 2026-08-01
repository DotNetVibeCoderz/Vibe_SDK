using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Api;

namespace Unitree.Net.Control;

/// <summary>
/// Which on-board controller currently owns the robot's motors.
/// </summary>
/// <param name="Name">Controller name, e.g. <c>normal</c> or <c>ai</c>. Empty when none is active.</param>
/// <param name="Form">Robot form reported alongside the mode.</param>
public readonly record struct MotionMode(string Name, string Form)
{
    /// <summary>Whether any controller currently owns the motors.</summary>
    public bool IsActive => !string.IsNullOrEmpty(Name);
}

/// <summary>
/// Starts, stops and queries the on-board motion controller.
/// </summary>
/// <remarks>
/// This is the gate in front of low-level control. While a motion controller is active it drives the
/// motors at 500 Hz, and anything published on <c>rt/lowcmd</c> is simply overwritten — the symptom is
/// a robot that ignores commands without reporting any error at all.
/// </remarks>
public sealed class MotionSwitcherClient : IDisposable
{
    private readonly ServiceClient _service;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates a client over <paramref name="participant"/>.</summary>
    public MotionSwitcherClient(IDdsParticipant participant, TimeSpan requestTimeout, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        _logger = logger ?? NullLogger.Instance;
        _service = new ServiceClient(participant, Services.MotionSwitcher, requestTimeout, _logger);
    }

    /// <summary>Queries which motion controller currently owns the robot.</summary>
    public async Task<MotionMode> GetCurrentModeAsync(CancellationToken cancellationToken = default)
    {
        ApiResponse response = await _service
            .CallAsync(MotionSwitcherApi.CheckMode, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.Data))
        {
            return new MotionMode(string.Empty, string.Empty);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Data);
            JsonElement root = document.RootElement;

            string name = root.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            string form = root.TryGetProperty("form", out JsonElement formElement)
                ? formElement.GetString() ?? string.Empty
                : string.Empty;

            return new MotionMode(name, form);
        }
        catch (JsonException ex)
        {
            throw new UnitreeException($"Could not parse the motion mode response: '{response.Data}'.", ex);
        }
    }

    /// <summary>
    /// Releases the active motion controller, freeing the motors for low-level control.
    /// </summary>
    /// <remarks>
    /// The robot lowers itself under damping as part of releasing. Give it a moment to settle before
    /// starting a low-level session, and make sure it is on the ground or a stand first.
    /// </remarks>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _service.CallAsync(MotionSwitcherApi.ReleaseMode, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Released the on-board motion controller; motors are now available for low-level control.");
    }

    /// <summary>Selects a motion controller by name.</summary>
    /// <param name="name">Controller name, e.g. <c>normal</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task SelectAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _service
            .CallAsync(MotionSwitcherApi.SelectMode, $"{{\"name\":\"{name}\"}}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Selected motion controller '{Name}'.", name);
    }

    /// <summary>
    /// Ensures no motion controller owns the motors, releasing one if necessary.
    /// </summary>
    /// <returns><see langword="true"/> if a controller was released by this call.</returns>
    public async Task<bool> EnsureReleasedAsync(CancellationToken cancellationToken = default)
    {
        MotionMode mode = await GetCurrentModeAsync(cancellationToken).ConfigureAwait(false);

        if (!mode.IsActive)
        {
            return false;
        }

        _logger.LogInformation("Motion controller '{Name}' is active; releasing it before low-level control.", mode.Name);
        await ReleaseAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Dispose();
    }
}
