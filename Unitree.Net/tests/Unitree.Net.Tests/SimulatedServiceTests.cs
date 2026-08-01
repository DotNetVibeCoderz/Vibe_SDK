using Shouldly;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Sensors;
using Unitree.Net.Simulation;

namespace Unitree.Net.Tests;

/// <summary>
/// Tests that an application can actually drive the simulator.
/// </summary>
/// <remarks>
/// Before the simulator answered the sport service it published telemetry and nothing else, so any
/// application that commanded motion connected, read state happily, and then timed out on its first
/// <c>StandUpAsync</c> with "Service 'sport' did not respond". Nine of the sixteen wizard templates do
/// exactly that, which made the claim that every template runs against the simulator untrue.
/// </remarks>
public sealed class SimulatedServiceTests
{
    /// <summary>A simulator and a client wired to each other over loopback.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        internal required SimulationHost Host { get; init; }
        internal required UnitreeRobot Robot { get; init; }
        internal required SimulationLog Log { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Robot.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync()
    {
        // A port per test run, so parallel tests do not join each other's multicast group and see
        // each other's traffic.
        int port = 7600 + Random.Shared.Next(300);

        var log = new SimulationLog();
        var host = new SimulationHost(log);

        await host.StartAsync(new SimulationOptions
        {
            Model = RobotModel.Go2,
            MulticastAddress = "239.255.0.1",
            MulticastPort = port,
            LowStateRateHz = 200,
        });

        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.ManagedMulticast,
            MulticastAddress = "239.255.0.1",
            MulticastPort = port,
        };

        var transport = new ManagedMulticastTransport(options);
        var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        var robot = new UnitreeRobot(participant, options);
        await robot.ConnectAsync();

        return new Rig { Host = host, Robot = robot, Log = log };
    }

    [Fact]
    public async Task StandUpIsAnsweredRatherThanTimingOut()
    {
        await using Rig rig = await StartAsync();

        // The call that used to throw TimeoutException after five seconds.
        await Should.NotThrowAsync(() => rig.Robot.Sport.StandUpAsync());
        await Should.NotThrowAsync(() => rig.Robot.Sport.BalanceStandAsync());

        rig.Host.Robot.Gait.ShouldNotBe(SimulatedGait.Resting);
    }

    [Fact]
    public async Task VelocityCommandsReachTheSimulatedRobot()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();
        await rig.Robot.Sport.BalanceStandAsync();
        await rig.Robot.Sport.MoveAsync(new VelocityCommand(0.4f, 0f, 0.2f));

        rig.Host.Robot.Command.Forward.ShouldBe(0.4f, 0.001f);
        rig.Host.Robot.Command.YawRate.ShouldBe(0.2f, 0.001f);

        // Gait is derived on the control loop's next tick, not at the moment the command lands.
        await Task.Delay(100);
        rig.Host.Robot.Gait.ShouldBe(SimulatedGait.Walking);
    }

    [Fact]
    public async Task MotionIsRefusedWhileTheRobotIsResting()
    {
        await using Rig rig = await StartAsync();

        // The simulator starts resting, exactly as a robot does after power-on, and refuses to drive
        // until it has been stood up. An application that forgets StandUp fails here rather than
        // working in the simulator and failing silently on hardware.
        rig.Host.Robot.Gait.ShouldBe(SimulatedGait.Resting);

        await Should.ThrowAsync<UnitreeServiceException>(
            () => rig.Robot.Sport.MoveAsync(new VelocityCommand(0.5f, 0f, 0f)));
    }

    [Fact]
    public async Task PosturesDriveTheSimulatedRobot()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();
        rig.Host.Robot.Gait.ShouldNotBe(SimulatedGait.Resting);

        await rig.Robot.Sport.StandDownAsync();
        rig.Host.Robot.Gait.ShouldBe(SimulatedGait.Resting);

        await rig.Robot.Sport.RecoveryStandAsync();
        rig.Host.Robot.Gait.ShouldNotBe(SimulatedGait.Resting);

        await rig.Robot.Sport.DampAsync();
        rig.Host.Robot.Gait.ShouldBe(SimulatedGait.Resting);
    }

    [Fact]
    public async Task StopMoveHaltsTheRobotWithoutSittingDown()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();
        await rig.Robot.Sport.BalanceStandAsync();
        await rig.Robot.Sport.MoveAsync(new VelocityCommand(0.5f, 0f, 0f));

        await rig.Robot.Sport.StopMoveAsync();

        rig.Host.Robot.Command.IsMoving.ShouldBeFalse();
        rig.Host.Robot.Gait.ShouldNotBe(SimulatedGait.Resting, "stopping is not the same as lying down");
    }

    [Fact]
    public async Task SettingsAreAcceptedRatherThanRefused()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();

        // These change how the robot moves rather than whether it does. Refusing them would make the
        // simulator look broken when the application is fine.
        await Should.NotThrowAsync(() => rig.Robot.Sport.SetBodyHeightAsync(0.05f));
        await Should.NotThrowAsync(() => rig.Robot.Sport.SetFootRaiseHeightAsync(0.09f));
        await Should.NotThrowAsync(() => rig.Robot.Sport.SetSpeedLevelAsync(1));
        await Should.NotThrowAsync(() => rig.Robot.Sport.HelloAsync());
    }

    [Fact]
    public async Task VelocityStreamRunsWithoutTrippingWhileACommandIsHeld()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();
        await rig.Robot.Sport.BalanceStandAsync();

        using VelocityStream stream = rig.Robot.Sport.StartVelocityStream();
        stream.Command = new VelocityCommand(0.3f, 0f, 0f);

        // Holding one velocity for well over the robot's own 500 ms expiry is ordinary — a dance
        // step, a leg of a patrol. It used to stop the robot and log a warning every half second.
        await Task.Delay(1500);

        stream.IsWatchdogTripped.ShouldBeFalse();
        stream.Command.Forward.ShouldBe(0.3f, 0.001f);
        rig.Host.Robot.Command.Forward.ShouldBe(0.3f, 0.001f);
    }

    [Fact]
    public async Task AnExplicitCommandTimeoutStillStopsTheRobot()
    {
        await using Rig rig = await StartAsync();

        await rig.Robot.Sport.StandUpAsync();
        await rig.Robot.Sport.BalanceStandAsync();

        using VelocityStream stream = rig.Robot.Sport.StartVelocityStream(
            commandTimeout: TimeSpan.FromMilliseconds(200));

        stream.Command = new VelocityCommand(0.3f, 0f, 0f);
        await Task.Delay(900);

        // Opt-in, for an application that wants "stopped reassigning" treated as a fault.
        stream.IsWatchdogTripped.ShouldBeTrue();
        stream.Command.IsStop.ShouldBeTrue();
    }

    [Fact]
    public void CallersRelayingARemoteSourceOptIntoACommandTimeout()
    {
        // The default is no expiry, which is right for a caller that holds its own command. It is
        // wrong for anything relaying a source that can vanish while the process keeps running: the
        // stream would resend the last command forever. These two are the cases in this repository,
        // and this test exists because making the default correct silently broke both of them.
        string bridge = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Unitree.Net.Ros2", "Ros2Bridge.cs"));

        string dashboard = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "Unitree.Net.Dashboard", "Components", "Pages", "Control.razor"));

        bridge.Contains("commandTimeout:", StringComparison.Ordinal).ShouldBeTrue(
            "a cmd_vel publisher can die while the bridge is fine, and the robot must not coast");

        dashboard.Contains("commandTimeout:", StringComparison.Ordinal).ShouldBeTrue(
            "a browser tab can close mid-press, and StopDrive never runs");
    }

    private static string RepositoryRoot() =>
        Unitree.Net.Wizard.Core.Projects.ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Tests must run from inside the repository.");

    [Fact]
    public async Task TelemetryStillFlowsAlongsideTheServices()
    {
        await using Rig rig = await StartAsync();

        var telemetry = new TelemetryHub(rig.Robot.Participant);
        await rig.Robot.Sport.StandUpAsync();

        // The services must not have displaced the telemetry path they sit beside.
        await Task.Delay(400);

        telemetry.GetSnapshot().ShouldNotBeNull();
        telemetry.LowStateCount.ShouldBeGreaterThan(0);

        telemetry.Dispose();
    }
}
