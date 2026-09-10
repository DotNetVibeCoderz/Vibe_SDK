using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Simulation;

namespace PollenRobotics.Net.Gallery.Demos;

/// <summary>What a demo needs to drive a robot and say what it is doing.</summary>
/// <param name="Engine">The running simulation.</param>
/// <param name="Log">Where to write progress.</param>
/// <param name="Report">Called with a one-line status for the demo surface.</param>
public readonly record struct DemoContext(SimulationEngine Engine, RobotLogSink Log, Action<string> Report);

/// <summary>
/// One gallery entry: a use case, the code that implements it, and the code running for real.
/// </summary>
/// <remarks>
/// <para>
/// The point of the gallery is that the code panel and the moving robot are the same thing. A
/// screenshot with a code sample beside it is a brochure; this is the sample, executing, against
/// the same SDK surface a user would call.
/// </para>
/// <para>
/// <see cref="Source"/> is the literal text shown in the code panel. It is written by hand rather
/// than extracted from the assembly, which means it can drift from <see cref="RunAsync"/> - so keep
/// them in step, and prefer moving real code into the source string over paraphrasing it.
/// </para>
/// </remarks>
public sealed record GalleryDemo
{
    /// <summary>Stable id.</summary>
    public required string Id { get; init; }

    /// <summary>Name in the list.</summary>
    public required string Title { get; init; }

    /// <summary>One line about what it shows.</summary>
    public required string Summary { get; init; }

    /// <summary>Which robot it drives.</summary>
    public required RobotKind Robot { get; init; }

    /// <summary>Which group it appears under.</summary>
    public required string Group { get; init; }

    /// <summary>The C# shown in the code panel.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// A note about what to watch for, shown under the title.
    /// </summary>
    /// <remarks>
    /// This is where the demo earns its place: not "this moves the head" but "watch the arc turn
    /// red as yaw runs into the body-yaw constraint". Without it a gallery is a list of things
    /// wiggling.
    /// </remarks>
    public string? WatchFor { get; init; }

    /// <summary>Runs the demo. Cancellation is expected and is not an error.</summary>
    public required Func<DemoContext, CancellationToken, Task> RunAsync { get; init; }

    /// <summary>Roughly how long a run takes, for the button label.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(8);
}
