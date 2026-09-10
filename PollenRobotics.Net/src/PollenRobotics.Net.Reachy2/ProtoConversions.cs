using System.Numerics;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Kinematics;
using Reachy.Kinematics;
using ProtoMatrix4x4 = Reachy.Kinematics.Matrix4x4;
using ProtoQuaternion = Reachy.Kinematics.Quaternion;
using Reachy.Part.Arm;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// Translates between this SDK's types and the generated protobuf messages.
/// </summary>
/// <remarks>
/// <para>
/// Two conventions have to be reconciled here and both are easy to get subtly wrong.
/// </para>
/// <para>
/// First, the API wraps scalars in <c>FloatValue</c> rather than using bare floats. That is
/// deliberate on Pollen's side: proto3 cannot distinguish an unset field from one explicitly set to
/// zero, and "hold this joint at exactly 0 rad" is a real command. So a null wrapper means "leave
/// it alone" and <c>FloatValue(0)</c> means "go to zero" - passing a bare 0.0 through would silently
/// turn the second into the first.
/// </para>
/// <para>
/// Second, <c>Matrix4x4.data</c> is a flat row-major homogeneous matrix, the same layout the Reachy
/// Mini daemon uses, and the transpose of <see cref="System.Numerics.Matrix4x4"/>. <see cref="Pose"/>
/// already handles that crossing; everything here goes through it.
/// </para>
/// </remarks>
internal static class ProtoConversions
{
    /// <summary>Narrows a value into the wrapper, or returns null to mean "unchanged".</summary>
    public static float? Wrap(double? value) => value is { } v ? (float)v : null;

    /// <summary>Narrows a value that is always meant to be sent.</summary>
    public static float? Wrap(double value) => (float)value;

    /// <summary>Reads a wrapper, treating an unset one as zero.</summary>
    public static double Unwrap(float? value) => value ?? 0;

    /// <summary>Converts a pose to the flat row-major matrix the API expects.</summary>
    public static ProtoMatrix4x4 ToMatrix(Pose pose)
    {
        var matrix = new ProtoMatrix4x4();
        matrix.Data.AddRange(pose.ToRowMajor());
        return matrix;
    }

    /// <summary>Reads a flat row-major matrix back into a pose.</summary>
    public static Pose ToPose(ProtoMatrix4x4? matrix)
    {
        if (matrix is null || matrix.Data.Count != 16)
        {
            return Pose.Identity;
        }

        Span<double> values = stackalloc double[16];
        for (int i = 0; i < 16; i++)
        {
            values[i] = matrix.Data[i];
        }

        return Pose.FromRowMajor(values);
    }

    /// <summary>Builds a rotation from roll/pitch/yaw, which is how the neck is commanded.</summary>
    public static Rotation3d ToRotation(Angle roll, Angle pitch, Angle yaw) => new()
    {
        Rpy = new ExtEulerAngles
        {
            Roll = Wrap(roll.Radians),
            Pitch = Wrap(pitch.Radians),
            Yaw = Wrap(yaw.Radians),
        },
    };

    /// <summary>
    /// Reads a rotation into roll/pitch/yaw, whichever of the three representations it carries.
    /// </summary>
    public static (Angle Roll, Angle Pitch, Angle Yaw) ToRpy(Rotation3d? rotation)
    {
        if (rotation is null)
        {
            return (Angle.Zero, Angle.Zero, Angle.Zero);
        }

        switch (rotation.RotationCase)
        {
            case Rotation3d.RotationOneofCase.Rpy:
                return (Angle.FromRadians(Unwrap(rotation.Rpy.Roll)),
                        Angle.FromRadians(Unwrap(rotation.Rpy.Pitch)),
                        Angle.FromRadians(Unwrap(rotation.Rpy.Yaw)));

            case Rotation3d.RotationOneofCase.Q:
                ProtoQuaternion q = rotation.Q;
                return Rotation.ToRpy(new System.Numerics.Quaternion(
                    (float)q.X, (float)q.Y, (float)q.Z, (float)q.W));

            case Rotation3d.RotationOneofCase.Matrix when rotation.Matrix.Data.Count == 9:
                // A bare 3x3 needs padding into a homogeneous 4x4 before Pose can read it.
                Span<double> homogeneous = stackalloc double[16];
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        homogeneous[(row * 4) + column] = rotation.Matrix.Data[(row * 3) + column];
                    }
                }

                homogeneous[15] = 1;
                return Pose.FromRowMajor(homogeneous).Rpy;

            default:
                return (Angle.Zero, Angle.Zero, Angle.Zero);
        }
    }

    /// <summary>
    /// Packs the seven arm joint angles into the API's shoulder/elbow/wrist grouping.
    /// </summary>
    /// <remarks>
    /// The arm is built from two Orbita2d actuators and one Orbita3d, so the API groups the joints
    /// by actuator rather than listing seven of them. The order inside each group is the order of
    /// <c>ArmJoints</c>: shoulder pitch then roll, elbow yaw then pitch, wrist roll/pitch/yaw.
    /// </remarks>
    public static ArmPosition ToArmPosition(ReadOnlySpan<double> joints)
    {
        if (joints.Length != 7)
        {
            throw new ArgumentException($"A Reachy 2 arm has 7 joints, got {joints.Length}.", nameof(joints));
        }

        return new ArmPosition
        {
            ShoulderPosition = new Component.Orbita2D.Pose2d
            {
                Axis1 = Wrap(joints[0]),
                Axis2 = Wrap(joints[1]),
            },
            ElbowPosition = new Component.Orbita2D.Pose2d
            {
                Axis1 = Wrap(joints[2]),
                Axis2 = Wrap(joints[3]),
            },
            WristPosition = ToRotation(
                Angle.FromRadians(joints[4]),
                Angle.FromRadians(joints[5]),
                Angle.FromRadians(joints[6])),
        };
    }

    /// <summary>Unpacks an arm position into the seven joint angles, in catalogue order.</summary>
    public static double[] FromArmPosition(ArmPosition? position)
    {
        if (position is null)
        {
            return new double[7];
        }

        (Angle roll, Angle pitch, Angle yaw) = ToRpy(position.WristPosition);

        return
        [
            Unwrap(position.ShoulderPosition?.Axis1),
            Unwrap(position.ShoulderPosition?.Axis2),
            Unwrap(position.ElbowPosition?.Axis1),
            Unwrap(position.ElbowPosition?.Axis2),
            roll.Radians,
            pitch.Radians,
            yaw.Radians,
        ];
    }
}
