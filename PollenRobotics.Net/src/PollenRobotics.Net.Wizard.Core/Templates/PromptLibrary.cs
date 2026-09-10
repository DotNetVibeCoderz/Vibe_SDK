using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>One example prompt, shown as a starter card in the chat panel.</summary>
/// <param name="Title">Short label on the card.</param>
/// <param name="Prompt">The text inserted into the composer when the card is clicked.</param>
/// <param name="Robot">Which robot it is about, or null for anything.</param>
/// <param name="Category">Which group it appears under.</param>
public readonly record struct PromptExample(string Title, string Prompt, RobotKind? Robot, TemplateCategory Category);

/// <summary>
/// Starter prompts for Jack.
/// </summary>
/// <remarks>
/// <para>
/// An empty chat box is the hardest part of a tool like this. These are written the way a good
/// request actually looks - concrete about the robot, the behaviour and the constraints - because
/// the examples teach the user what a useful prompt contains, not just that prompting is possible.
/// </para>
/// <para>
/// They are also deliberately in both English and Indonesian, matching the project's bilingual
/// documentation.
/// </para>
/// </remarks>
public static class PromptLibrary
{
    /// <summary>Every example.</summary>
    public static IReadOnlyList<PromptExample> All { get; } =
    [
        // ---- Reachy Mini ----
        new("Greeting behaviour",
            "Write a Reachy Mini console app that plays a friendly greeting when it first sees a face: "
            + "perk the antennas up, nod twice, then settle back to neutral. Use the daemon's face tracker "
            + "rather than doing detection yourself, and make sure it runs against the simulator with --sim.",
            RobotKind.ReachyMini, TemplateCategory.Behaviour),

        new("Desk pet",
            "Give Reachy Mini an idle personality for when nobody is interacting with it. It should breathe, "
            + "glance around at irregular intervals, and occasionally tilt its head as though curious. "
            + "Nothing should repeat on a fixed period - it needs to read as alive, not as a loop.",
            RobotKind.ReachyMini, TemplateCategory.Behaviour),

        new("Music visualiser",
            "Make Reachy Mini move in time with audio: bob the head on the beat and sweep the antennas with the "
            + "amplitude. Read the microphone through the media API. Keep the command rate at 50 Hz using "
            + "RealtimeLoop, and explain how you avoided fighting the daemon's own wobble.",
            RobotKind.ReachyMini, TemplateCategory.Perception),

        new("Pomodoro timer",
            "Build a Pomodoro timer where Reachy Mini is the clock: it sits still while the timer runs, "
            + "perks up and waves the antennas at the end of a work block, and droops during a break. "
            + "Desktop app, Avalonia, with a dark theme.",
            RobotKind.ReachyMini, TemplateCategory.Interface),

        new("Teleoperation over the network",
            "Write two programs: one that reads a gamepad and publishes head-pose commands over UDP, and one "
            + "that runs on the robot and applies them. Handle packet loss by holding the last pose, and stop "
            + "the robot if nothing arrives for half a second.",
            RobotKind.ReachyMini, TemplateCategory.Interface),

        new("Perilaku sapaan (ID)",
            "Buatkan aplikasi konsol Reachy Mini yang menyapa ketika mendeteksi wajah: antena naik, kepala "
            + "mengangguk dua kali, lalu kembali ke posisi netral. Gunakan pelacak wajah dari daemon, dan "
            + "pastikan bisa dijalankan di simulator dengan --sim.",
            RobotKind.ReachyMini, TemplateCategory.Behaviour),

        // ---- MicroDuck ----
        new("Follow the wall",
            "Write a MicroDuck behaviour that follows a wall on its left using the time-of-flight sensor: "
            + "keep about 20 cm of clearance, turn away when it gets closer, turn back when it drifts off. "
            + "Handle falls by recovering and carrying on.",
            RobotKind.MicroDuck, TemplateCategory.Navigation),

        new("Fetch routine",
            "Make MicroDuck walk forward until the depth sensor sees something within 15 cm, pick it up with "
            + "the ground_pick action, turn around, walk back the same distance and drop it. Explain how you "
            + "know the pick succeeded.",
            RobotKind.MicroDuck, TemplateCategory.Manipulation),

        new("Duck dance",
            "Choreograph a 30-second routine for MicroDuck using the action slots and velocity commands: "
            + "kicks, a roulade, some walking in a figure of eight, and a quack on the beat. Make the timing "
            + "data rather than code so it is easy to retune.",
            RobotKind.MicroDuck, TemplateCategory.Behaviour),

        new("Battery-aware patrol",
            "Write a MicroDuck patrol that walks a route indefinitely but returns to a home position and stops "
            + "when the battery drops below a threshold. Log the battery voltage and the loop rate each lap so "
            + "I can see the duck degrading before it falls over.",
            RobotKind.MicroDuck, TemplateCategory.Navigation),

        new("Policy comparison rig",
            "Build a tool that loads two ONNX walking policies and runs each for 30 seconds against the "
            + "simulator, recording distance travelled, how often the duck fell, and mean loop jitter. "
            + "Print a comparison table at the end.",
            RobotKind.MicroDuck, TemplateCategory.Tooling),

        new("Patroli hemat baterai (ID)",
            "Buatkan patroli MicroDuck yang berjalan terus mengikuti rute, tetapi kembali ke posisi awal dan "
            + "berhenti saat baterai di bawah ambang batas. Catat tegangan baterai dan laju loop setiap putaran.",
            RobotKind.MicroDuck, TemplateCategory.Navigation),

        // ---- Reachy 2 ----
        new("Table tidying",
            "Write a Reachy 2 routine that picks objects off a table one at a time and puts them into a bin. "
            + "Check reachability before each move, verify the gripper actually caught something, and stop "
            + "cleanly if it did not.",
            RobotKind.Reachy2, TemplateCategory.Manipulation),

        new("Handover",
            "Make Reachy 2 hold an object out for a person to take, detect when they have taken it by watching "
            + "the gripper force, and then withdraw the arm. Include a timeout so it does not hold its arm out "
            + "indefinitely.",
            RobotKind.Reachy2, TemplateCategory.Manipulation),

        new("Guided tour",
            "Program Reachy 2 to give a short tour: drive the mobile base between three waypoints, and at each "
            + "one turn the head towards a point of interest and gesture at it with an arm. The base and the "
            + "arms should not move at the same time.",
            RobotKind.Reachy2, TemplateCategory.Navigation),

        new("Trajectory recorder",
            "Build a tool that records the joint positions of both Reachy 2 arms at 50 Hz while I move them by "
            + "hand, saves the trajectory to JSON, and can replay it. Explain how you keep the two arms in step "
            + "on replay.",
            RobotKind.Reachy2, TemplateCategory.Tooling),

        new("Health dashboard",
            "Write a Blazor page that streams Reachy 2 state and shows joint positions, actuator temperatures "
            + "and battery level, with a warning when any actuator goes above 55 degrees. Update at 10 Hz.",
            RobotKind.Reachy2, TemplateCategory.Interface),

        // ---- Cross-robot and AI ----
        new("Voice-controlled robot",
            "Build an app where I speak a command and the robot does it. Use Semantic Kernel with function "
            + "calling so the model picks from a fixed set of robot actions rather than generating code. "
            + "Include the function definitions and explain how you stopped it inventing actions.",
            null, TemplateCategory.Ai),

        new("Robot that explains itself",
            "Give the robot a running commentary: every time it does something, it uses an LLM to say why in "
            + "one sentence, and speaks it. Keep the model call off the control loop so the robot never stalls "
            + "waiting for a reply.",
            null, TemplateCategory.Ai),

        new("Natural-language choreographer",
            "Write a tool where I describe a movement in plain language and it produces a RecordedMove I can "
            + "play back. Use the SDK reference functions to get the pose API right, and validate the result "
            + "against the safety limits before saving it.",
            RobotKind.ReachyMini, TemplateCategory.Ai),

        new("Vision question answering",
            "Take a frame from the robot's camera, send it to a multimodal model with a question about what it "
            + "can see, and have the robot nod or shake its head based on the answer. Handle the case where the "
            + "model is not multimodal.",
            null, TemplateCategory.Ai),

        new("Explain this error",
            "I am getting a RobotSafetyException on head.pitch when I run my behaviour. Explain what causes it, "
            + "what the actual limits are, and show me the two correct ways to fix it - including which one you "
            + "would choose and why.",
            null, TemplateCategory.GettingStarted),

        new("Port a Python example",
            "Here is a Python script using the reachy_mini SDK. Port it to C# against PollenRobotics.Net, "
            + "keeping the behaviour identical. Point out anywhere the .NET SDK behaves differently from the "
            + "Python one, particularly around limits and error handling.",
            RobotKind.ReachyMini, TemplateCategory.GettingStarted),

        new("Test without hardware",
            "Show me how to write unit tests for a robot behaviour without a robot. Use the simulation "
            + "transports, and give me one test that asserts the behaviour parks the robot even when it is "
            + "cancelled halfway through.",
            null, TemplateCategory.Tooling),

        new("Kontrol suara (ID)",
            "Buatkan aplikasi di mana saya mengucapkan perintah dan robot menjalankannya. Gunakan Semantic "
            + "Kernel dengan function calling supaya model memilih dari daftar aksi robot yang tetap, bukan "
            + "membuat kode baru. Sertakan definisi function-nya.",
            null, TemplateCategory.Ai),

        new("Uji tanpa perangkat keras (ID)",
            "Tunjukkan cara menulis unit test untuk perilaku robot tanpa robot sungguhan. Gunakan transport "
            + "simulasi, dan berikan satu test yang memastikan robot tetap diparkir walau dibatalkan di tengah "
            + "jalan.",
            null, TemplateCategory.Tooling),
    ];

    /// <summary>Examples for one robot, plus the ones that apply to any robot.</summary>
    public static IReadOnlyList<PromptExample> For(RobotKind robot) =>
        [.. All.Where(p => p.Robot is null || p.Robot == robot)];

    /// <summary>Examples in one category.</summary>
    public static IReadOnlyList<PromptExample> InCategory(TemplateCategory category) =>
        [.. All.Where(p => p.Category == category)];

    /// <summary>
    /// A handful of examples chosen at random, for the empty-chat state.
    /// </summary>
    /// <remarks>
    /// Shuffled rather than showing the first four every time: a fixed set trains the user to
    /// ignore the panel after the first day, and the point is to surface things they had not
    /// thought to ask for.
    /// </remarks>
    public static IReadOnlyList<PromptExample> Sample(int count = 4, RobotKind? robot = null)
    {
        IReadOnlyList<PromptExample> pool = robot is { } kind ? For(kind) : All;
        return [.. pool.OrderBy(_ => Random.Shared.Next()).Take(Math.Clamp(count, 1, pool.Count))];
    }
}
