namespace PollenRobotics.Net.Core.Robots;

/// <summary>
/// The joint models for every robot this SDK supports.
/// </summary>
/// <remarks>
/// <para>
/// These are transcribed from the published Pollen Robotics documentation, not measured from
/// hardware. Where the documentation gives a limit it is used verbatim; where it does not, the
/// value is a conservative guess and is marked as such in the comment above it. See
/// <c>docs/joint-models.md</c> for the source of each number.
/// </para>
/// <para>
/// The order of each list is the wire order, and <see cref="RobotDescription"/> asserts that the
/// declared indices agree with it. Never resolve a joint by a hard-coded integer - ask for it by
/// name, because a variant with a different joint count shifts every index after the one that
/// changed.
/// </para>
/// </remarks>
public static class RobotCatalog
{
    /// <summary>
    /// Reachy Mini: body yaw, six Stewart-platform neck motors, two antennas. Nine actuators.
    /// </summary>
    /// <remarks>
    /// The wire order matches the daemon <c>head_joint_positions</c> field, which puts body yaw at
    /// index 0 and the six neck branches at 1..6, then reports the antennas separately in
    /// <c>antennas_joint_positions</c> as [right, left]. This description flattens both into one
    /// vector, appending the antennas after the neck.
    /// </remarks>
    public static RobotDescription ReachyMini { get; } = new(
        RobotKind.ReachyMini,
        "Reachy Mini",
        [
            // Documented limit: body yaw is constrained to +/-160 degrees.
            JointDescriptor.Degrees("body.yaw", 0, -160, 160),

            // The six Stewart branches are not commanded individually by user code - the head pose
            // is. The published documentation does not state a per-branch limit, so these are a
            // deliberately wide guess; the pose-level limits below are the ones that actually bind.
            JointDescriptor.Degrees("neck.branch_1", 1, -90, 90),
            JointDescriptor.Degrees("neck.branch_2", 2, -90, 90),
            JointDescriptor.Degrees("neck.branch_3", 3, -90, 90),
            JointDescriptor.Degrees("neck.branch_4", 4, -90, 90),
            JointDescriptor.Degrees("neck.branch_5", 5, -90, 90),
            JointDescriptor.Degrees("neck.branch_6", 6, -90, 90),

            // Antennas are continuous in hardware but a full turn tangles the cabling; the SDK
            // keeps them inside one revolution.
            JointDescriptor.Degrees("antenna.right", 7, -180, 180),
            JointDescriptor.Degrees("antenna.left", 8, -180, 180),
        ]);

    /// <summary>
    /// MicroDuck: fifteen servos - two five-DOF legs, a four-DOF neck and head, and a mouth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the wire order of <c>duck_ipc_proto::JOINT_NAMES</c>, transcribed from Pollen's own
    /// <c>scripts/bake-duck-mesh.py</c>, which names that list as a copy of the runtime contract.
    /// Limits come from Pollen's MuJoCo model, vendored alongside it at
    /// <c>src/PollenRobotics.Net.MicroDuck/Reference/robot_walk.xml</c> (Apache 2.0).
    /// </para>
    /// <para>
    /// <b>The head sits between the legs.</b> The order is left leg, neck and head, mouth, then the
    /// right leg - so the right leg starts at index 10, not 5 and not 9.
    /// </para>
    /// <para>
    /// <b><c>mouth</c> is a servo with no MuJoCo joint.</b> The physics model fixes the jaw as a
    /// geom because it does not affect locomotion, so reading joint names out of the MJCF alone
    /// gives fourteen and silently drops it - which is exactly the mistake 0.2.0 shipped. The
    /// runtime still reports fifteen values, and everything from index 10 onward is a leg.
    /// </para>
    /// </remarks>
    public static RobotDescription MicroDuck { get; } = new(
        RobotKind.MicroDuck,
        "MicroDuck",
        [
            JointDescriptor.Degrees("left_hip_yaw", 0, -25, 30),
            JointDescriptor.Degrees("left_hip_roll", 1, -22, 22),
            JointDescriptor.Degrees("left_hip_pitch", 2, -90, 90),
            JointDescriptor.Degrees("left_knee", 3, -90, 90),
            JointDescriptor.Degrees("left_ankle", 4, -90, 90),

            JointDescriptor.Degrees("neck_pitch", 5, -90, 60),
            JointDescriptor.Degrees("head_pitch", 6, -90, 90),
            JointDescriptor.Degrees("head_yaw", 7, -170, 170),
            JointDescriptor.Degrees("head_roll", 8, -25, 25),

            // The only guessed row left in this table. `mouth` is absent from the MuJoCo model, so
            // its travel is not published anywhere this project can read; the range is deliberately
            // conservative. Correct it against `robotctl monitor` on a real duck.
            JointDescriptor.Degrees("mouth", 9, 0, 45),

            // Hip yaw mirrors: the left reaches further outward, the right further inward, so the
            // same value on both legs does not produce a symmetric stance.
            JointDescriptor.Degrees("right_hip_yaw", 10, -30, 25),
            JointDescriptor.Degrees("right_hip_roll", 11, -22, 22),
            JointDescriptor.Degrees("right_hip_pitch", 12, -90, 90),
            JointDescriptor.Degrees("right_knee", 13, -90, 90),
            JointDescriptor.Degrees("right_ankle", 14, -90, 90),
        ]);

    /// <summary>
    /// Reachy 2: two 7-DOF arms, a three-DOF Orbita neck, two antennas and two grippers.
    /// </summary>
    /// <remarks>
    /// Joint names match the dotted paths the Python SDK exposes (<c>r_arm.shoulder.pitch</c> and
    /// friends) so that code generated by the wizard reads the same in both languages. The mobile
    /// base is not a joint chain and is modelled separately.
    /// </remarks>
    public static RobotDescription Reachy2 { get; } = new(
        RobotKind.Reachy2,
        "Reachy 2",
        [
            JointDescriptor.Degrees("r_arm.shoulder.pitch", 0, -180, 90),
            JointDescriptor.Degrees("r_arm.shoulder.roll", 1, -180, 10),
            JointDescriptor.Degrees("r_arm.elbow.yaw", 2, -90, 90),
            JointDescriptor.Degrees("r_arm.elbow.pitch", 3, -125, 0),
            JointDescriptor.Degrees("r_arm.wrist.roll", 4, -45, 45),
            JointDescriptor.Degrees("r_arm.wrist.pitch", 5, -45, 45),
            JointDescriptor.Degrees("r_arm.wrist.yaw", 6, -45, 45),

            JointDescriptor.Degrees("l_arm.shoulder.pitch", 7, -180, 90),
            JointDescriptor.Degrees("l_arm.shoulder.roll", 8, -10, 180),
            JointDescriptor.Degrees("l_arm.elbow.yaw", 9, -90, 90),
            JointDescriptor.Degrees("l_arm.elbow.pitch", 10, -125, 0),
            JointDescriptor.Degrees("l_arm.wrist.roll", 11, -45, 45),
            JointDescriptor.Degrees("l_arm.wrist.pitch", 12, -45, 45),
            JointDescriptor.Degrees("l_arm.wrist.yaw", 13, -45, 45),

            JointDescriptor.Degrees("head.neck.roll", 14, -45, 45),
            JointDescriptor.Degrees("head.neck.pitch", 15, -45, 45),
            JointDescriptor.Degrees("head.neck.yaw", 16, -90, 90),

            JointDescriptor.Degrees("head.r_antenna", 17, -150, 150),
            JointDescriptor.Degrees("head.l_antenna", 18, -150, 150),

            JointDescriptor.Degrees("r_arm.gripper", 19, -5, 130),
            JointDescriptor.Degrees("l_arm.gripper", 20, -5, 130),
        ]);

    /// <summary>Looks a description up by robot family.</summary>
    /// <summary>
    /// Amazing Hand: four fingers, two servos each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transcribed from Pollen's own MuJoCo model, vendored at
    /// <c>src/PollenRobotics.Net.AmazingHand/Reference/robot.xml</c> (Apache 2.0, upstream commit
    /// 3e8241074df3). The order is the actuator order in that file, which also matches the servo
    /// ids 1-8 in <c>Demo/AHControl/config/r_hand.toml</c> - the model and the wire agree here,
    /// which is worth saying because for the MicroDuck they did not.
    /// </para>
    /// <para>
    /// Every finger is a parallel four-bar: two servos drive one finger through a linkage, and the
    /// finger's bend and its sideways splay are both consequences of the pair. Neither motor maps
    /// to a human joint, so they are named for what they are. Driving them as if <c>motor1</c> were
    /// a knuckle and <c>motor2</c> a fingertip produces nonsense; <c>AmazingHandPose</c> in the
    /// AmazingHand package carries the pair-to-posture mapping. Core cannot reference it - the
    /// dependency runs the other way - which is why this is prose rather than a cref.
    /// </para>
    /// <para>
    /// The hand is handed. This table is the right hand; the left mirrors, and a left-hand servo
    /// takes the opposite sign for the same posture.
    /// </para>
    /// </remarks>
    public static RobotDescription AmazingHand { get; } = new(
        RobotKind.AmazingHand,
        "Amazing Hand",
        [
            JointDescriptor.Degrees("finger1_motor1", 0, -90, 90),
            JointDescriptor.Degrees("finger1_motor2", 1, -90, 90),
            JointDescriptor.Degrees("finger2_motor1", 2, -90, 90),
            JointDescriptor.Degrees("finger2_motor2", 3, -90, 90),
            JointDescriptor.Degrees("finger3_motor1", 4, -90, 90),
            JointDescriptor.Degrees("finger3_motor2", 5, -90, 90),
            JointDescriptor.Degrees("finger4_motor1", 6, -90, 90),
            JointDescriptor.Degrees("finger4_motor2", 7, -90, 90),
        ]);

    public static RobotDescription For(RobotKind kind) => kind switch
    {
        RobotKind.ReachyMini => ReachyMini,
        RobotKind.MicroDuck => MicroDuck,
        RobotKind.Reachy2 => Reachy2,
        RobotKind.AmazingHand => AmazingHand,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown robot kind."),
    };

    /// <summary>Every description, in the order the pickers list them.</summary>
    public static IReadOnlyList<RobotDescription> All { get; } = [ReachyMini, MicroDuck, Reachy2, AmazingHand];
}
