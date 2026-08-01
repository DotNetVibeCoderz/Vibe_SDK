using System.ComponentModel.DataAnnotations;

namespace Unitree.Net.Core;

/// <summary>
/// Which DDS transport implementation to use.
/// </summary>
public enum DdsTransportKind
{
    /// <summary>
    /// Cyclone DDS through the native <c>unitree_net_native</c> shim. Wire-compatible with the robot.
    /// </summary>
    /// <remarks>Requires the native library; see <c>native/README.md</c>.</remarks>
    CycloneNative,

    /// <summary>
    /// Pure-managed UDP multicast transport. Runs anywhere with no native dependency.
    /// </summary>
    /// <remarks>
    /// Wire format is Unitree.Net's own framing, <em>not</em> RTPS. Use it for host-to-host links,
    /// simulators and integration tests — it cannot talk to robot firmware.
    /// </remarks>
    ManagedMulticast,

    /// <summary>In-process loopback. Publishers feed subscribers directly. For unit tests.</summary>
    Loopback,
}

/// <summary>
/// Connection and transport configuration for a single robot.
/// </summary>
/// <remarks>
/// Bind from configuration section <c>Unitree</c>.
/// </remarks>
public sealed class UnitreeOptions
{
    /// <summary>Configuration section name for binding from <c>appsettings.json</c>.</summary>
    public const string SectionName = "Unitree";

    /// <summary>The robot platform being controlled.</summary>
    [Required]
    public RobotModel Model { get; set; } = RobotModel.Go2;

    /// <summary>Which transport to use.</summary>
    public DdsTransportKind Transport { get; set; } = DdsTransportKind.CycloneNative;

    /// <summary>
    /// Name of the host network interface bound to the robot, for example <c>eth0</c> or <c>Ethernet 2</c>.
    /// </summary>
    /// <remarks>
    /// Leaving this empty lets DDS pick an interface, which on a multi-homed host is a coin flip and the
    /// single most common cause of "the robot never appears". Set it explicitly.
    /// </remarks>
    public string NetworkInterface { get; set; } = string.Empty;

    /// <summary>The DDS domain identifier. Unitree robots default to zero.</summary>
    [Range(0, 232)]
    public int DomainId { get; set; }

    /// <summary>Multicast group used by <see cref="DdsTransportKind.ManagedMulticast"/>.</summary>
    public string MulticastAddress { get; set; } = "239.255.0.1";

    /// <summary>Multicast port used by <see cref="DdsTransportKind.ManagedMulticast"/>.</summary>
    [Range(1, 65535)]
    public int MulticastPort { get; set; } = 7447;

    /// <summary>
    /// Multicast time-to-live. One keeps traffic on the local segment.
    /// </summary>
    public int MulticastTimeToLive { get; set; } = 1;

    /// <summary>How long <c>ConnectAsync</c> waits for the first state message before giving up.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Default timeout for request/response service calls.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Low-level control loop frequency. Defaults to the model's recommended rate when left at zero.
    /// </summary>
    public int ControlFrequencyHz { get; set; }

    /// <summary>
    /// Bounded capacity of each inbound telemetry channel.
    /// </summary>
    /// <remarks>
    /// Telemetry channels drop the oldest item when full. A slow consumer must not be able to stall the
    /// receive path or grow memory without bound.
    /// </remarks>
    [Range(1, 100_000)]
    public int TelemetryQueueCapacity { get; set; } = 256;

    /// <summary>Safety envelope applied to every outbound command.</summary>
    public RobotSafetyOptions Safety { get; set; } = new();

    /// <summary>Gets the effective control frequency, resolving the model default when unset.</summary>
    public int GetEffectiveControlFrequencyHz() =>
        ControlFrequencyHz > 0 ? ControlFrequencyHz : RobotModelInfo.GetControlFrequencyHz(Model);

    /// <summary>
    /// Validates cross-field consistency beyond what data annotations cover.
    /// </summary>
    /// <exception cref="OptionsValidationFailure">The configuration cannot produce a working connection.</exception>
    public void Validate()
    {
        if (Model == RobotModel.Unknown)
        {
            throw new OptionsValidationFailure($"{SectionName}:Model must be set to a supported robot model.");
        }

        if (Transport == DdsTransportKind.ManagedMulticast &&
            !System.Net.IPAddress.TryParse(MulticastAddress, out System.Net.IPAddress? address))
        {
            throw new OptionsValidationFailure($"{SectionName}:MulticastAddress '{MulticastAddress}' is not a valid IP address.");
        }
        else if (Transport == DdsTransportKind.ManagedMulticast)
        {
            System.Net.IPAddress parsed = System.Net.IPAddress.Parse(MulticastAddress);
            byte firstOctet = parsed.GetAddressBytes()[0];
            if (firstOctet is < 224 or > 239)
            {
                throw new OptionsValidationFailure(
                    $"{SectionName}:MulticastAddress '{MulticastAddress}' is not in the multicast range 224.0.0.0–239.255.255.255.");
            }
        }

        if (ControlFrequencyHz is not 0 and (< 1 or > 2000))
        {
            throw new OptionsValidationFailure($"{SectionName}:ControlFrequencyHz must be between 1 and 2000, or 0 for the model default.");
        }
    }
}

/// <summary>
/// Raised when <see cref="UnitreeOptions"/> cannot yield a working connection.
/// </summary>
public sealed class OptionsValidationFailure : UnitreeException
{
    /// <summary>Creates an instance with a message.</summary>
    public OptionsValidationFailure(string message)
        : base(message)
    {
    }
}
