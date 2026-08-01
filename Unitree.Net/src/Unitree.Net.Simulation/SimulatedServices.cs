using System.Globalization;
using System.Text.Json;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Api;

namespace Unitree.Net.Simulation;

/// <summary>
/// Answers requests on one <c>rt/api/&lt;service&gt;/request</c> topic.
/// </summary>
/// <remarks>
/// <para>
/// Without this the simulator publishes telemetry and nothing else, so any application that commands
/// motion — which is most of them — connects, reads state happily, and then times out on the first
/// <c>StandUpAsync</c> with "Service 'sport' did not respond". The application is correct; there is
/// simply nothing on the other end.
/// </para>
/// <para>
/// Responses are correlated by <see cref="RequestIdentity.Id"/>, and a request whose policy sets
/// <c>NoReply</c> is acted on without one — that is the path <c>VelocityStream</c> uses at 20 Hz, and
/// answering it would be pure noise.
/// </para>
/// </remarks>
internal sealed class SimulatedService : IAsyncDisposable
{
    private readonly IDdsSubscriber<ApiRequest> _requests;
    private readonly IDdsPublisher<ApiResponse> _responses;
    private readonly Func<ApiRequest, ServiceReply> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;

    internal SimulatedService(
        IDdsParticipant participant,
        string serviceName,
        Func<ApiRequest, ServiceReply> handler)
    {
        _handler = handler;
        _requests = participant.CreateSubscriber<ApiRequest>(Topics.RequestTopic(serviceName), 64);
        _responses = participant.CreatePublisher<ApiResponse>(Topics.ResponseTopic(serviceName));
        _pump = Task.Run(() => PumpAsync(_cancellation.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ApiRequest request in
                _requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ServiceReply reply;

                try
                {
                    reply = _handler(request);
                }
                catch (Exception exception)
                {
                    // A handler that throws must still answer, or the caller waits out its full
                    // timeout for what is really an immediate failure.
                    reply = ServiceReply.Failed(exception.Message);
                }

                if (request.Header.Policy.NoReply)
                {
                    continue;
                }

                _responses.Publish(new ApiResponse
                {
                    Identity = request.Header.Identity,
                    Status = new ResponseStatus(reply.Code),
                    Data = reply.Data,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cancellation.Dispose();
    }
}

/// <summary>What a simulated service returns for one request.</summary>
/// <param name="Code">Zero for success; anything else is reported to the caller as a failure.</param>
/// <param name="Data">JSON payload, empty when there is nothing to return.</param>
internal readonly record struct ServiceReply(int Code, string Data)
{
    internal static ServiceReply Ok(string data = "") => new(0, data);

    internal static ServiceReply Failed(string reason) =>
        new(-1, JsonSerializer.Serialize(new { error = reason }));
}

/// <summary>
/// The services a real robot exposes, backed by a <see cref="SimulatedRobot"/>.
/// </summary>
/// <remarks>
/// Enough of the sport API to run the applications this SDK generates: postures, velocity, gait and
/// the settings that go with them. Commands that would need real dynamics — flips, pounces — are
/// accepted and logged rather than refused, because refusing them would make the simulator look
/// broken when the application is fine.
/// </remarks>
internal sealed class SimulatedServiceHub : IAsyncDisposable
{
    private readonly SimulatedRobot _robot;
    private readonly SimulationLog _log;
    private readonly List<SimulatedService> _services = [];

    internal SimulatedServiceHub(IDdsParticipant participant, SimulatedRobot robot, SimulationLog log)
    {
        _robot = robot;
        _log = log;

        _services.Add(new SimulatedService(participant, Services.Sport, HandleSport));
        _services.Add(new SimulatedService(participant, Services.MotionSwitcher, HandleMotionSwitcher));
        _services.Add(new SimulatedService(participant, Services.RobotState, HandleRobotState));
    }

    private ServiceReply HandleSport(ApiRequest request)
    {
        long api = request.Header.Identity.ApiId;

        switch (api)
        {
            case SportApi.StandUp:
            case SportApi.RecoveryStand:
            case SportApi.RiseSit:
                _robot.StandUp();
                Trace(api, "stand up");
                return ServiceReply.Ok();

            case SportApi.BalanceStand:
                // Balanced standing is what makes velocity commands take effect at all. The simulator
                // treats it as standing, but the application still has to ask — the same gate the
                // real robot enforces silently.
                _robot.StandUp();
                Trace(api, "balance stand");
                return ServiceReply.Ok();

            case SportApi.StandDown:
            case SportApi.Damp:
            case SportApi.Sit:
                _robot.StandDown();
                Trace(api, "stand down");
                return ServiceReply.Ok();

            case SportApi.StopMove:
                _robot.Command = SimulatedVelocity.Zero;
                Trace(api, "stop");
                return ServiceReply.Ok();

            case SportApi.Move:
                return HandleMove(request);

            case SportApi.GetState:
                SimulationSnapshot snapshot = _robot.Capture();
                return ServiceReply.Ok(JsonSerializer.Serialize(new
                {
                    gait = snapshot.Gait.ToString(),
                    bodyHeight = snapshot.Height,
                    speed = snapshot.Speed,
                    battery = snapshot.BatterySoc,
                }));

            // Accepted and ignored: they change how the robot moves rather than whether it does, and
            // this simulator has one gait.
            case SportApi.BodyHeight:
            case SportApi.FootRaiseHeight:
            case SportApi.SpeedLevel:
            case SportApi.SwitchGait:
            case SportApi.ContinuousGait:
            case SportApi.Euler:
            case SportApi.EconomicGait:
            case SportApi.SwitchJoystick:
                Trace(api, "setting accepted");
                return ServiceReply.Ok();

            case SportApi.GetBodyHeight:
                return ServiceReply.Ok($"{{\"data\":{Json(_robot.Capture().Height)}}}");

            case SportApi.GetFootRaiseHeight:
                return ServiceReply.Ok("{\"data\":0.09}");

            case SportApi.GetSpeedLevel:
                return ServiceReply.Ok("{\"data\":0}");

            default:
                // Tricks and gestures: Hello, Stretch, Dance1, WiggleHips, FrontFlip and the rest.
                // Accepted rather than refused — refusing would make the simulator look broken when
                // the application is fine. They simply do not animate.
                Trace(api, "accepted; not animated by the simulator");
                return ServiceReply.Ok();
        }
    }

    private ServiceReply HandleMove(ApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Parameter))
        {
            return ServiceReply.Failed("move needs an {\"x\":..,\"y\":..,\"z\":..} parameter");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(request.Parameter);
            JsonElement root = document.RootElement;

            var command = new SimulatedVelocity(
                Read(root, "x"),
                Read(root, "y"),
                Read(root, "z"));

            // Motion is refused while the robot is resting, which mirrors the real thing: the sport
            // service will not drive a robot that has not stood up.
            if (_robot.Gait == SimulatedGait.Resting && command.IsMoving)
            {
                return ServiceReply.Failed("the robot is not standing; call StandUp then BalanceStand first");
            }

            _robot.Command = command;
            return ServiceReply.Ok();
        }
        catch (JsonException exception)
        {
            return ServiceReply.Failed($"could not read the move parameter: {exception.Message}");
        }

        static float Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.TryGetSingle(out float number)
                ? number
                : 0f;
    }

    private ServiceReply HandleMotionSwitcher(ApiRequest request)
    {
        switch (request.Header.Identity.ApiId)
        {
            case MotionSwitcherApi.CheckMode:
                // The shape BeginLowLevelSessionAsync reads to decide whether the sport service still
                // owns the motors.
                return ServiceReply.Ok(
                    _robot.Gait == SimulatedGait.Resting
                        ? "{\"name\":\"\",\"form\":\"\"}"
                        : "{\"name\":\"normal\",\"form\":\"sport\"}");

            case MotionSwitcherApi.ReleaseMode:
                // Releasing the motors is exactly what makes low-level commands take effect. The
                // simulator lies down, which is what a real robot does when nothing is holding it up.
                _robot.StandDown();
                _log.Info("service", "Sport mode released; low-level commands would now reach the motors.");
                return ServiceReply.Ok();

            case MotionSwitcherApi.SelectMode:
            case MotionSwitcherApi.SetSilent:
                return ServiceReply.Ok();

            default:
                return ServiceReply.Ok();
        }
    }

    private ServiceReply HandleRobotState(ApiRequest request) =>
        request.Header.Identity.ApiId switch
        {
            RobotStateApi.ServiceList => ServiceReply.Ok(
                "{\"data\":[{\"name\":\"sport_mode\",\"status\":1},{\"name\":\"motion_switcher\",\"status\":1}]}"),

            _ => ServiceReply.Ok(),
        };

    private void Trace(long api, string what) =>
        _log.Write(SimulationLogLevel.Trace, "service", $"sport {api}: {what}");

    private static string Json(float value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        foreach (SimulatedService service in _services)
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }

        _services.Clear();
    }
}
