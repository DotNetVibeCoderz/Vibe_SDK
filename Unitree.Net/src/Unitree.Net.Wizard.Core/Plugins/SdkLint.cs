using System.Text.RegularExpressions;

namespace Unitree.Net.Wizard.Core.Plugins;

/// <summary>
/// One SDK member that does not exist, and the one that does.
/// </summary>
/// <param name="Pattern">Matches the mistake.</param>
/// <param name="Correction">What to say instead.</param>
/// <param name="Note">Why the mistake is plausible, so the fix is understood rather than copied.</param>
internal readonly record struct SdkTrap(Regex Pattern, string Correction, string Note);

/// <summary>
/// Checks generated code for SDK members that do not exist.
/// </summary>
/// <remarks>
/// <para>
/// This exists because instructions do not work. The assistant is told, unconditionally and in two
/// places, to call <c>describe_sdk</c> before writing SDK code — and it still writes
/// <c>BatteryStatus.StateOfCharge</c>, because it is confident rather than uncertain, and confidence
/// is exactly what stops a model reaching for a lookup tool.
/// </para>
/// <para>
/// A deterministic check in the tool result cannot be skipped the way a prompt can. The file is still
/// written — the operator may have asked for precisely that — but the reply names the real member, so
/// the next turn corrects it instead of the compiler doing so several minutes later.
/// </para>
/// <para>
/// This is a lint for known traps, not a compiler. It catches the mistakes actually observed while
/// building the template catalogue; the build is still what proves the code correct.
/// </para>
/// </remarks>
internal static partial class SdkLint
{
    private static readonly SdkTrap[] Traps =
    [
        new(StateOfCharge(), "BatteryStatus.StateOfChargePercent",
            "the property carries its unit in the name"),

        new(VoltageVolts(), "BatteryStatus.PackVoltage",
            "there is no VoltageVolts"),

        new(NavigationReached(), "NavigationResult.Arrived",
            "the values are Arrived, Cancelled, Stalled and NoOdometry"),

        new(NavigationNotReady(), "NavigationResult.NoOdometry",
            "the values are Arrived, Cancelled, Stalled and NoOdometry"),

        new(ControllerStatistics(), "LowLevelController.LoopStatistics",
            "Statistics belongs to RealtimeLoop, not to the controller"),

        new(SetJoint(), "LowLevelController.SetJointPosition(index, position, kp, kd)",
            "there is also SetJointTorque; neither is called SetJoint"),

        new(StartWithCallback(), "LowLevelController.Start() with no arguments",
            "set the joints first, then Start; subscribe to StateUpdated for per-tick work"),

        new(GaitAnomalyDetector(), "GaitAnalyzer, with ToSample, Analyze and DetectAnomalies",
            "there is no GaitAnomalyDetector"),

        new(DualArmFromRobot(), "new DualArmCoordinator(leftArmController, rightArmController)",
            "the coordinator is built from two ArmControllers, not from the robot"),

        new(RouterNotFound(), "the Router's NotFoundPage parameter",
            "the <NotFound> child element was removed in .NET 10, and leaving one in stops Found's Context binding"),

        new(ConfigureServices(), "builder.Services.AddUnitreeRobot(builder.Configuration) directly",
            "ConfigureServices belongs to the older IHostBuilder from Host.CreateDefaultBuilder; "
            + "Host.CreateApplicationBuilder returns a HostApplicationBuilder with a Services property"),
    ];

    /// <summary>
    /// Reports SDK members in <paramref name="content"/> that do not exist.
    /// </summary>
    /// <param name="relativePath">The file being written, used to skip irrelevant checks.</param>
    /// <param name="content">The file's text.</param>
    /// <returns>One line per problem, empty when nothing was recognised.</returns>
    internal static IReadOnlyList<string> Check(string relativePath, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        string extension = Path.GetExtension(relativePath).ToLowerInvariant();

        if (extension is not (".cs" or ".razor"))
        {
            return [];
        }

        var problems = new List<string>();

        foreach (SdkTrap trap in Traps)
        {
            Match match = trap.Pattern.Match(content);

            if (match.Success)
            {
                problems.Add($"'{match.Value.Trim()}' does not exist — use {trap.Correction} ({trap.Note}).");
            }
        }

        if (extension == ".cs")
        {
            // The extension methods live in their own namespace, and the resulting error names
            // IServiceCollection rather than anything recognisable. It caught every template once.
            RequireUsing(
                content, problems,
                "AddUnitree", "Unitree.Net.Extensions.DependencyInjection",
                "without it the error blames IServiceCollection and says nothing about the cause");

            // VelocityCommand, EulerAngles and the safety types are in Core even though everything
            // that consumes them is in Control, so a file can reference Control alone and still fail.
            RequireUsing(
                content, problems,
                "VelocityCommand", "Unitree.Net.Core",
                "VelocityCommand lives in Core, not in Control");

            RequireUsing(
                content, problems,
                "UnitreeConnectionException", "Unitree.Net.Core",
                "the exception types live in Core");
        }

        return problems;
    }

    /// <summary>Reports a type used without the namespace that declares it.</summary>
    private static void RequireUsing(
        string content,
        List<string> problems,
        string marker,
        string namespaceName,
        string why)
    {
        if (content.Contains(marker, StringComparison.Ordinal)
            && !content.Contains($"using {namespaceName};", StringComparison.Ordinal)
            && !content.Contains($"{namespaceName}.{marker}", StringComparison.Ordinal))
        {
            problems.Add($"'{marker}' needs 'using {namespaceName};' — {why}.");
        }
    }

    [GeneratedRegex(@"\.StateOfCharge\b(?!Percent)")]
    private static partial Regex StateOfCharge();

    [GeneratedRegex(@"\.VoltageVolts\b")]
    private static partial Regex VoltageVolts();

    [GeneratedRegex(@"NavigationResult\.Reached\b")]
    private static partial Regex NavigationReached();

    [GeneratedRegex(@"NavigationResult\.NotReady\b")]
    private static partial Regex NavigationNotReady();

    [GeneratedRegex(@"\bcontroller\.Statistics\b")]
    private static partial Regex ControllerStatistics();

    [GeneratedRegex(@"\.SetJoint\s*\(")]
    private static partial Regex SetJoint();

    [GeneratedRegex(@"\.Start\s*\(\s*(tick|_|\(|\w+\s*=>)")]
    private static partial Regex StartWithCallback();

    [GeneratedRegex(@"\bGaitAnomalyDetector\b")]
    private static partial Regex GaitAnomalyDetector();

    [GeneratedRegex(@"new\s+DualArmCoordinator\s*\(\s*robot\s*\)")]
    private static partial Regex DualArmFromRobot();

    [GeneratedRegex(@"<NotFound>")]
    private static partial Regex RouterNotFound();

    [GeneratedRegex(@"CreateApplicationBuilder[\s\S]{0,120}?\.ConfigureServices\s*\(")]
    private static partial Regex ConfigureServices();
}
