namespace Unitree.Net.Wizard.Core.Chat;

/// <summary>
/// A ready-made prompt the operator can send to Jack with one click.
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Category">Which group it belongs to.</param>
/// <param name="Title">Short label shown on the card.</param>
/// <param name="Prompt">The text sent to Jack.</param>
public sealed record PromptExample(string Id, string Category, string Title, string Prompt);

/// <summary>
/// Example prompts shown when a chat session is empty.
/// </summary>
/// <remarks>
/// An empty chat panel with a blinking cursor is the hardest part of a tool like this: the operator
/// has no idea what it can do or how much to ask for. These are written as complete, specific
/// requests rather than as one-word hints, because that is also what teaches the right way to ask.
/// </remarks>
public static class PromptGallery
{
    /// <summary>Every example, grouped by category in <see cref="Categories"/> order.</summary>
    public static IReadOnlyList<PromptExample> All { get; } =
    [
        // ------------------------------------------------------------- Getting started
        new("first-app", "Getting started", "My first robot app",
            "I'm new to this SDK. Write me a console application that connects to a Go2, waits for " +
            "telemetry, and prints the battery level and how many feet are on the ground once a " +
            "second. Explain each part briefly as you go."),

        new("explain-sdk", "Getting started", "How does this SDK fit together?",
            "Explain how Unitree.Net is layered — what each namespace is for, and which pieces I " +
            "actually need for a simple application. Keep it short."),

        new("simulator-first", "Getting started", "Develop without a robot",
            "I don't have a robot yet. Show me how to develop and test against the simulator, what " +
            "the simulator does and does not model, and what I'll still have to verify when hardware " +
            "arrives."),

        new("why-not-moving", "Getting started", "Why won't my robot move?",
            "My velocity commands are being ignored and there's no error anywhere. Walk me through " +
            "the causes in the order I should check them."),

        // ------------------------------------------------------------------- Locomotion
        new("square-walk", "Locomotion", "Walk a square",
            "Write a program that walks the robot in a 2 m square and returns to its start, turning " +
            "90 degrees at each corner. Use the velocity stream, not one-shot move calls, and explain " +
            "why that matters."),

        new("teleop-gamepad", "Locomotion", "Gamepad teleoperation",
            "Build a teleoperation program driven by an Xbox controller: left stick translates, right " +
            "stick turns, a trigger acts as a dead-man switch that stops the robot the moment it is " +
            "released."),

        new("stair-approach", "Locomotion", "Approach and climb stairs",
            "Write a behaviour that walks toward a staircase, slows down as it gets close, switches to " +
            "a stair-climbing gait, and reports each state change. Be explicit about what this cannot " +
            "know without a depth sensor."),

        new("smooth-speed", "Locomotion", "Smooth acceleration",
            "My robot lurches when I change speed. Write a velocity ramp that accelerates smoothly to " +
            "a target and decelerates the same way, and explain a sensible acceleration limit."),

        // ------------------------------------------------------------------ Autonomy
        new("patrol-schedule", "Autonomy", "Scheduled patrol with charging",
            "Build a patrol that runs a fixed route every 30 minutes, returns to a charging dock when " +
            "the battery drops below 30%, waits until it is above 80%, and then resumes where it left " +
            "off."),

        new("follow-person", "Autonomy", "Follow a person",
            "Write a follow-me behaviour that keeps 1.5 m from a target and stops safely when it " +
            "loses sight of them. Leave the detector as a clearly-marked interface I can implement."),

        new("search-pattern", "Autonomy", "Search a room",
            "Write a lawnmower search pattern that covers a rectangular area with a configurable " +
            "spacing, logging its position as it goes. Explain how odometry drift limits how large an " +
            "area this is useful over."),

        new("return-home", "Autonomy", "Return home on signal loss",
            "Write a supervisor that watches the connection and, if telemetry goes stale for more " +
            "than five seconds, stops the robot and then walks it back toward its start position."),

        // ------------------------------------------------------------------ Telemetry
        new("csv-logger", "Telemetry & data", "Log everything to CSV",
            "Write a logger that records IMU, joint positions, foot forces and battery to a CSV file " +
            "at full rate, rotating to a new file every 100 MB. Keep it allocation-light."),

        new("live-dashboard", "Telemetry & data", "Live web dashboard",
            "Build a Blazor Server dashboard showing battery, motor temperature and speed as live " +
            "charts, plus a connection indicator. Include a health endpoint."),

        new("anomaly-alert", "Telemetry & data", "Alert on abnormal gait",
            "Use ML.NET to learn the robot's normal gait, then raise an alert when the pattern " +
            "departs from it — a dragging leg, an unbalanced load. Explain how to avoid alerting on " +
            "every surface change."),

        new("battery-report", "Telemetry & data", "Daily battery health report",
            "Write a service that records battery cycles, cell imbalance and depth of discharge, and " +
            "produces a daily Markdown report on pack health."),

        // -------------------------------------------------------------- Low-level control
        new("impedance-basics", "Low-level control", "Explain impedance control",
            "Explain the impedance control law this SDK uses, what kp and kd actually do to the robot, " +
            "and how to choose starting values safely. Then show a minimal 500 Hz loop that holds a " +
            "standing pose."),

        new("sine-sweep", "Low-level control", "Joint frequency sweep",
            "Write a low-level program that sweeps one joint through a sine at increasing frequency " +
            "and logs commanded against measured position, so I can see where the joint stops keeping " +
            "up. Include the safety gates I need before running this."),

        new("compliance-demo", "Low-level control", "Make a leg compliant",
            "Write a demo that makes one leg soft enough to push by hand while the others hold the " +
            "robot up, and explain which gains produce that and what the risk is."),

        // ------------------------------------------------------------------ Manipulation
        new("wave-hello", "Manipulation", "Wave hello with a G1",
            "Write a program that makes a G1 wave its right arm, using synchronised joint timing so " +
            "the motion looks natural rather than mechanical."),

        new("dual-arm-lift", "Manipulation", "Two-handed lift",
            "Write a coordinated two-arm lift for a G1: reach, close, lift, carry, place. Explain what " +
            "I cannot verify without force feedback."),

        // ---------------------------------------------------------------------- AI
        new("voice-robot", "AI & integration", "Voice-commanded robot",
            "Build an application where I speak a command, an LLM interprets it, and the robot acts — " +
            "with motion behind an explicit confirmation step. Use Semantic Kernel and keep the safety " +
            "gating obvious in the code."),

        new("inspection-agent", "AI & integration", "AI inspection agent",
            "Build an agent that patrols inspection stations, uses a vision model on each, and writes " +
            "a report describing anything unusual it found."),

        new("ros2-nav2", "AI & integration", "Talk to Nav2",
            "Set up the ROS 2 bridge so Nav2 can drive this robot, explain what Nav2 needs that the " +
            "bridge does not provide, and how to check it is working from the command line."),

        new("mqtt-fleet", "AI & integration", "Report to a fleet server",
            "Write a service that publishes robot telemetry to MQTT every 10 seconds and survives the " +
            "broker being unreachable without losing data or running out of memory."),

        // ------------------------------------------------------------------ Improve code
        new("review-project", "Improve my code", "Review this project",
            "Read every file in the open project and review it: correctness, safety gaps, anything " +
            "that will behave badly on real hardware. Be specific and rank by severity."),

        new("add-tests", "Improve my code", "Add tests",
            "Read the open project and write xunit tests for the logic that doesn't need a robot. Use " +
            "the loopback transport where a robot would otherwise be needed."),

        new("make-safe", "Improve my code", "Make this safe for hardware",
            "Read the open project and add the safety checks it needs before it touches a real robot: " +
            "battery gates, readiness checks, watchdogs, a clean stop on every exit path."),

        new("explain-error", "Improve my code", "Explain a build error",
            "Here is my build output. Explain what's actually wrong and fix it in the project:\n\n"),

        new("optimise-loop", "Improve my code", "Make my control loop allocation-free",
            "Read my control loop and remove every allocation from it. Explain each change and why a " +
            "garbage collection at 500 Hz shows up as jitter rather than as a memory problem."),

        // ----------------------------------------------------------------- Deployment
        new("deploy-jetson", "Deployment", "Deploy to the robot",
            "Explain how to publish this project for the robot's ARM64 compute module, copy it over, " +
            "and run it as a service that survives a reboot."),

        new("cross-platform", "Deployment", "Run on a Raspberry Pi",
            "What do I need to change to run this on a Raspberry Pi 5 next to the robot rather than " +
            "on the robot itself? Cover the network setup and what the timing implications are."),
    ];

    /// <summary>Category names, in the order the gallery shows them.</summary>
    public static IReadOnlyList<string> Categories { get; } =
    [
        "Getting started",
        "Locomotion",
        "Autonomy",
        "Telemetry & data",
        "Low-level control",
        "Manipulation",
        "AI & integration",
        "Improve my code",
        "Deployment",
    ];

    /// <summary>Examples in one category.</summary>
    /// <param name="category">The category name.</param>
    public static IReadOnlyList<PromptExample> InCategory(string category) =>
        [.. All.Where(example => string.Equals(example.Category, category, StringComparison.Ordinal))];

    /// <summary>
    /// A small, varied selection for the empty-state panel.
    /// </summary>
    /// <param name="count">How many to return.</param>
    /// <remarks>
    /// One per category rather than a random sample, so the operator sees the breadth of what Jack
    /// can do rather than four variations on walking.
    /// </remarks>
    public static IReadOnlyList<PromptExample> Featured(int count = 6) =>
        [.. Categories.Select(category => InCategory(category).FirstOrDefault())
                      .Where(example => example is not null)
                      .Take(count)!];
}
