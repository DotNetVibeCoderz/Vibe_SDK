using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Core.Safety;
using PollenRobotics.Net.Kinematics;
using Reachy;
using Reachy.Part;
using Reachy.Part.Arm;
using Reachy.Part.Hand;
using Reachy.Part.Head;
using Reachy.Part.Mobile.Base.Mobility;
using Reachy.Part.Mobile.Base.Utility;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// Reachy 2: two seven-axis arms, an Orbita neck, two grippers and an optional mobile base.
/// </summary>
/// <remarks>
/// <para>
/// Built on Pollen's own gRPC contract - the <c>.proto</c> files under <c>Protos/</c> are vendored
/// verbatim from <c>reachy2-sdk-api</c>, so the wire format is the robot's, not this SDK's reading
/// of it. Refresh them with <c>tools/sync-protos.ps1</c> when the robot's firmware moves on.
/// </para>
/// <para>
/// Parts are discovered at connect time from <c>GetReachy</c>, so a robot without a mobile base
/// simply reports <see cref="MobileBase"/> as null rather than failing. Check for null rather than
/// assuming: the same code is expected to run against both configurations.
/// </para>
/// <para>
/// Typical use:
/// </para>
/// <code>
/// await using var reachy = Reachy2Client.Connect(Reachy2Options.ForHost("reachy.local"));
/// await reachy.ConnectAsync();
/// await reachy.TurnOnAsync();
/// await reachy.RightArm!.GotoPoseAsync(pose, TimeSpan.FromSeconds(2));
/// await reachy.RightGripper!.CloseAsync();
/// </code>
/// </remarks>
public sealed class Reachy2Client : IRobotClient
{
    private readonly Reachy2Options _options;
    private readonly ILogger _logger;
    private readonly JointLimitGuard _guard;

    private GrpcChannel? _channel;
    private ReachyService.ReachyServiceClient? _reachy;
    private ReachyId? _reachyId;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _disposed;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.Reachy2;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.Reachy2;

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public string Endpoint => _options.Address.ToString();

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <summary>The right arm, or null when the robot does not report one.</summary>
    public Reachy2Arm? RightArm { get; private set; }

    /// <summary>The left arm, or null when the robot does not report one.</summary>
    public Reachy2Arm? LeftArm { get; private set; }

    /// <summary>The head, or null when the robot does not report one.</summary>
    public Reachy2Head? Head { get; private set; }

    /// <summary>The right gripper, or null.</summary>
    public Reachy2Gripper? RightGripper { get; private set; }

    /// <summary>The left gripper, or null.</summary>
    public Reachy2Gripper? LeftGripper { get; private set; }

    /// <summary>The mobile base, or null on a robot without one.</summary>
    public Reachy2MobileBase? MobileBase { get; private set; }

    /// <summary>The movement queue client, for cancelling everything at once.</summary>
    internal GoToService.GoToServiceClient? Movements { get; private set; }

    /// <summary>Serial number and firmware versions, available once connected.</summary>
    public ReachyInfo? Info { get; private set; }

    /// <summary>The robot's name, as it reports it.</summary>
    public string RobotName => _reachyId?.Name ?? "reachy";

    /// <summary>Creates a client. Nothing connects until <see cref="ConnectAsync"/> runs.</summary>
    public Reachy2Client(Reachy2Options? options = null, ILogger<Reachy2Client>? logger = null)
    {
        _options = options ?? Reachy2Options.Default;
        _logger = logger ?? NullLogger<Reachy2Client>.Instance;
        _guard = new JointLimitGuard(Description, _options.Safety, _logger);
    }

    /// <summary>Creates a client for the given options.</summary>
    public static Reachy2Client Connect(Reachy2Options? options = null, ILoggerFactory? loggerFactory = null) =>
        new(options, loggerFactory?.CreateLogger<Reachy2Client>());

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_state == ConnectionState.Connected)
        {
            return;
        }

        SetState(ConnectionState.Connecting);

        try
        {
            _channel = GrpcChannel.ForAddress(_options.Address, new GrpcChannelOptions
            {
                // The SDK server serves plaintext HTTP/2 on the robot's network. Without an
                // explicit handler the runtime will not negotiate h2c over a bare http:// address.
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                },
                MaxReceiveMessageSize = 16 * 1024 * 1024,
            });

            _reachy = new ReachyService.ReachyServiceClient(_channel);

            Reachy.Reachy description = await _reachy.GetReachyAsync(
                new Empty(),
                deadline: DateTime.UtcNow.Add(_options.RequestTimeout),
                cancellationToken: cancellationToken);

            BindParts(description);

            _reachyId = description.Id;
            Info = description.Info;

            SetState(ConnectionState.Connected);
            _logger.LogInformation(
                "Connected to {Name} at {Endpoint} (hardware {Hardware}, software {Software}).",
                RobotName, Endpoint, Info?.VersionHard ?? "?", Info?.VersionSoft ?? "?");
        }
        catch (RpcException ex)
        {
            SetState(ConnectionState.Faulted);
            throw new RobotConnectionException(
                $"Could not reach the Reachy 2 SDK server at {Endpoint} ({ex.StatusCode}). " +
                "Check that the robot is powered and that reachy2_sdk_server is running.", ex);
        }
        catch
        {
            SetState(ConnectionState.Faulted);
            throw;
        }
    }

    private void BindParts(Reachy.Reachy description)
    {
        GrpcChannel channel = _channel!;
        TimeSpan timeout = _options.RequestTimeout;

        var gotoClient = new GoToService.GoToServiceClient(channel);
        Movements = gotoClient;

        if (description.RArm?.PartId is { } rightArm)
        {
            RightArm = new Reachy2Arm(new ArmService.ArmServiceClient(channel), gotoClient, rightArm, ArmSide.Right, _guard, timeout);
        }

        if (description.LArm?.PartId is { } leftArm)
        {
            LeftArm = new Reachy2Arm(new ArmService.ArmServiceClient(channel), gotoClient, leftArm, ArmSide.Left, _guard, timeout);
        }

        if (description.Head?.PartId is { } head)
        {
            Head = new Reachy2Head(new HeadService.HeadServiceClient(channel), gotoClient, head, _guard, timeout);
        }

        if (description.RHand?.PartId is { } rightHand)
        {
            RightGripper = new Reachy2Gripper(new HandService.HandServiceClient(channel), rightHand, ArmSide.Right, timeout);
        }

        if (description.LHand?.PartId is { } leftHand)
        {
            LeftGripper = new Reachy2Gripper(new HandService.HandServiceClient(channel), leftHand, ArmSide.Left, timeout);
        }

        if (description.MobileBase?.PartId is { } mobileBase)
        {
            MobileBase = new Reachy2MobileBase(
                new MobileBaseUtilityService.MobileBaseUtilityServiceClient(channel),
                new MobileBaseMobilityService.MobileBaseMobilityServiceClient(channel),
                mobileBase,
                timeout);
        }

        _logger.LogDebug(
            "Parts: r_arm={RightArm} l_arm={LeftArm} head={Head} r_hand={RightHand} l_hand={LeftHand} mobile_base={Base}.",
            RightArm is not null, LeftArm is not null, Head is not null,
            RightGripper is not null, LeftGripper is not null, MobileBase is not null);
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Teardown();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    private void Teardown()
    {
        RightArm = null;
        LeftArm = null;
        Head = null;
        RightGripper = null;
        LeftGripper = null;
        MobileBase = null;
        _reachy = null;
        _reachyId = null;
        Movements = null;

        _channel?.Dispose();
        _channel = null;
    }

    /// <summary>
    /// Energises every part the robot reported.
    /// </summary>
    /// <remarks>
    /// The arms come on before the grippers. A gripper energised while its arm is still compliant
    /// can swing the whole limb by its own reaction torque.
    /// </remarks>
    public async Task TurnOnAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        if (RightArm is { } rightArm)
        {
            await rightArm.TurnOnAsync(cancellationToken).ConfigureAwait(false);
        }

        if (LeftArm is { } leftArm)
        {
            await leftArm.TurnOnAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Head is { } head)
        {
            await head.TurnOnAsync(cancellationToken).ConfigureAwait(false);
        }

        if (RightGripper is { } rightGripper)
        {
            await rightGripper.TurnOnAsync(cancellationToken).ConfigureAwait(false);
        }

        if (LeftGripper is { } leftGripper)
        {
            await leftGripper.TurnOnAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("{Name} is on.", RobotName);
    }

    /// <summary>
    /// Makes every part compliant.
    /// </summary>
    /// <remarks>
    /// The arms will sag. Anything held by a gripper will drop. Neither is a fault, but both are
    /// surprising if the robot was holding something above a table.
    /// </remarks>
    public async Task TurnOffAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        if (RightGripper is { } rightGripper)
        {
            await rightGripper.TurnOffAsync(cancellationToken).ConfigureAwait(false);
        }

        if (LeftGripper is { } leftGripper)
        {
            await leftGripper.TurnOffAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Head is { } head)
        {
            await head.TurnOffAsync(cancellationToken).ConfigureAwait(false);
        }

        if (RightArm is { } rightArm)
        {
            await rightArm.TurnOffAsync(cancellationToken).ConfigureAwait(false);
        }

        if (LeftArm is { } leftArm)
        {
            await leftArm.TurnOffAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("{Name} is off.", RobotName);
    }

    /// <inheritdoc />
    public Task EnableMotorsAsync(CancellationToken cancellationToken = default) => TurnOnAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisableMotorsAsync(CancellationToken cancellationToken = default) => TurnOffAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<double[]> GetJointPositionsAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        double[] joints = new double[Description.JointCount];

        if (RightArm is { } rightArm)
        {
            double[] right = await rightArm.GetJointPositionsAsync(cancellationToken).ConfigureAwait(false);
            right.CopyTo(joints, Description["r_arm.shoulder.pitch"].Index);
        }

        if (LeftArm is { } leftArm)
        {
            double[] left = await leftArm.GetJointPositionsAsync(cancellationToken).ConfigureAwait(false);
            left.CopyTo(joints, Description["l_arm.shoulder.pitch"].Index);
        }

        if (Head is { } head)
        {
            (Core.Geometry.Angle roll, Core.Geometry.Angle pitch, Core.Geometry.Angle yaw) =
                await head.GetOrientationAsync(cancellationToken).ConfigureAwait(false);

            joints[Description["head.neck.roll"].Index] = roll.Radians;
            joints[Description["head.neck.pitch"].Index] = pitch.Radians;
            joints[Description["head.neck.yaw"].Index] = yaw.Radians;
        }

        return joints;
    }

    /// <summary>Reads a full state snapshot for every part in one call.</summary>
    public async Task<ReachyState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        return await _reachy!.GetReachyStateAsync(
            _reachyId,
            deadline: DateTime.UtcNow.Add(_options.RequestTimeout),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Streams state snapshots until cancelled.
    /// </summary>
    /// <remarks>
    /// Preferred over polling for anything mirroring the robot in real time. The stream runs at
    /// <see cref="Reachy2Options.StateStreamFrequencyHz"/>; a consumer that cannot keep up applies
    /// backpressure to the whole channel, so do the work elsewhere and keep the loop body cheap.
    /// </remarks>
    public async IAsyncEnumerable<ReachyState> StreamStateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireConnected();

        var request = new ReachyStreamStateRequest
        {
            Id = _reachyId,
            PublishFrequency = (float)_options.StateStreamFrequencyHz,
        };

        using AsyncServerStreamingCall<ReachyState> call = _reachy!.StreamReachyState(request, cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return call.ResponseStream.Current;
        }
    }

    /// <summary>Reads the robot's own audit report: which parts are reporting errors.</summary>
    public async Task<ReachyStatus> AuditAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        return await _reachy!.AuditAsync(
            _reachyId,
            deadline: DateTime.UtcNow.Add(_options.RequestTimeout),
            cancellationToken: cancellationToken);
    }

    private void RequireConnected()
    {
        if (_state != ConnectionState.Connected || _reachy is null)
        {
            throw new RobotConnectionException("Not connected. Call ConnectAsync first.");
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Teardown();
        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }
}
