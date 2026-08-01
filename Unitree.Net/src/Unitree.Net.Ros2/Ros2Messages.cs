using System.Numerics;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Ros2;

/// <summary>
/// A <c>std_msgs/Header</c>.
/// </summary>
/// <remarks>
/// ROS 2 removed the sequence number that existed in ROS 1, so this is timestamp plus frame only.
/// </remarks>
public struct Ros2Header
{
    /// <summary>Timestamp seconds.</summary>
    public int Seconds;

    /// <summary>Timestamp nanoseconds.</summary>
    public uint Nanoseconds;

    /// <summary>Coordinate frame identifier.</summary>
    public string FrameId;

    /// <summary>Creates a header stamped with the current time.</summary>
    public static Ros2Header Now(string frameId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long unixNanoseconds = now.ToUnixTimeMilliseconds() * 1_000_000L;

        return new Ros2Header
        {
            Seconds = (int)(unixNanoseconds / 1_000_000_000L),
            Nanoseconds = (uint)(unixNanoseconds % 1_000_000_000L),
            FrameId = frameId,
        };
    }

    /// <summary>Writes this header in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.WriteInt32(Seconds);
        writer.WriteUInt32(Nanoseconds);
        writer.WriteString(FrameId);
    }

    /// <summary>Reads a header from CDR form.</summary>
    public static Ros2Header Read(ref CdrReader reader) => new()
    {
        Seconds = reader.ReadInt32(),
        Nanoseconds = reader.ReadUInt32(),
        FrameId = reader.ReadString(),
    };
}

/// <summary>
/// A <c>geometry_msgs/Twist</c>: linear and angular velocity.
/// </summary>
/// <remarks>
/// This is the canonical ROS 2 velocity command, normally published on <c>/cmd_vel</c>. Accepting it is
/// what lets standard navigation stacks — Nav2, teleop_twist_keyboard, a joystick node — drive a
/// Unitree robot without knowing anything about Unitree's own API.
/// </remarks>
public struct Ros2Twist : ICdrSerializable<Ros2Twist>
{
    /// <summary>Linear velocity, m/s.</summary>
    public Vector3 Linear;

    /// <summary>Angular velocity, rad/s.</summary>
    public Vector3 Angular;

    /// <inheritdoc />
    public static string DdsTypeName => "geometry_msgs::msg::dds_::Twist_";

    /// <inheritdoc />
    public static int MaxSerializedSize => 4 + (6 * 8);

    /// <summary>Converts to a body-frame velocity command.</summary>
    /// <remarks>ROS 2 uses x forward, y left, z up, which matches Unitree's body frame directly.</remarks>
    public readonly VelocityCommand ToVelocityCommand() => new(Linear.X, Linear.Y, Angular.Z);

    /// <summary>Creates a twist from a velocity command.</summary>
    public static Ros2Twist FromVelocityCommand(VelocityCommand command) => new()
    {
        Linear = new Vector3(command.Forward, command.Lateral, 0f),
        Angular = new Vector3(0f, 0f, command.YawRate),
    };

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);

        // ROS 2 geometry_msgs use float64 throughout, not float32.
        writer.WriteDouble(Linear.X);
        writer.WriteDouble(Linear.Y);
        writer.WriteDouble(Linear.Z);
        writer.WriteDouble(Angular.X);
        writer.WriteDouble(Angular.Y);
        writer.WriteDouble(Angular.Z);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static Ros2Twist Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);

        return new Ros2Twist
        {
            Linear = new Vector3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble()),
            Angular = new Vector3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble()),
        };
    }
}

/// <summary>
/// A <c>sensor_msgs/Imu</c>.
/// </summary>
public struct Ros2Imu : ICdrSerializable<Ros2Imu>
{
    /// <summary>Message header.</summary>
    public Ros2Header Header;

    /// <summary>Orientation quaternion.</summary>
    public Quaternion Orientation;

    /// <summary>Angular velocity, rad/s.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Linear acceleration, m/s².</summary>
    public Vector3 LinearAcceleration;

    /// <inheritdoc />
    public static string DdsTypeName => "sensor_msgs::msg::dds_::Imu_";

    /// <inheritdoc />
    public static int MaxSerializedSize => 512;

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);
        Header.Write(ref writer);

        writer.WriteDouble(Orientation.X);
        writer.WriteDouble(Orientation.Y);
        writer.WriteDouble(Orientation.Z);
        writer.WriteDouble(Orientation.W);
        WriteCovariance(ref writer);

        writer.WriteDouble(AngularVelocity.X);
        writer.WriteDouble(AngularVelocity.Y);
        writer.WriteDouble(AngularVelocity.Z);
        WriteCovariance(ref writer);

        writer.WriteDouble(LinearAcceleration.X);
        writer.WriteDouble(LinearAcceleration.Y);
        writer.WriteDouble(LinearAcceleration.Z);
        WriteCovariance(ref writer);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static Ros2Imu Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);
        var imu = new Ros2Imu { Header = Ros2Header.Read(ref reader) };

        double x = reader.ReadDouble();
        double y = reader.ReadDouble();
        double z = reader.ReadDouble();
        double w = reader.ReadDouble();
        imu.Orientation = new Quaternion((float)x, (float)y, (float)z, (float)w);
        SkipCovariance(ref reader);

        imu.AngularVelocity = ReadVector(ref reader);
        SkipCovariance(ref reader);

        imu.LinearAcceleration = ReadVector(ref reader);
        SkipCovariance(ref reader);

        return imu;
    }

    /// <summary>
    /// Writes a 3×3 covariance matrix.
    /// </summary>
    /// <remarks>
    /// A leading −1 is ROS 2's convention for "this quantity is not reported". Unitree does not publish
    /// covariances, and claiming a zero covariance would tell a consuming filter the measurement is
    /// perfect — which would make an EKF trust the IMU absolutely and diverge.
    /// </remarks>
    private static void WriteCovariance(ref CdrWriter writer)
    {
        writer.WriteDouble(-1.0);

        for (int i = 1; i < 9; i++)
        {
            writer.WriteDouble(0.0);
        }
    }

    private static void SkipCovariance(ref CdrReader reader)
    {
        for (int i = 0; i < 9; i++)
        {
            reader.ReadDouble();
        }
    }

    private static Vector3 ReadVector(ref CdrReader reader) =>
        new((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble());
}

/// <summary>
/// A <c>nav_msgs/Odometry</c>.
/// </summary>
/// <remarks>
/// Carries the robot's dead-reckoned pose. Note that Unitree's odometry drifts and resets on power
/// cycle, so downstream consumers should treat the <c>odom</c> frame as locally consistent only — which
/// is exactly what the ROS 2 frame conventions already assume.
/// </remarks>
public struct Ros2Odometry : ICdrSerializable<Ros2Odometry>
{
    /// <summary>Message header; frame is normally <c>odom</c>.</summary>
    public Ros2Header Header;

    /// <summary>Child frame, normally <c>base_link</c>.</summary>
    public string ChildFrameId;

    /// <summary>Pose in the header frame.</summary>
    public Pose Pose;

    /// <summary>Velocity in the child frame.</summary>
    public Ros2Twist Twist;

    /// <inheritdoc />
    public static string DdsTypeName => "nav_msgs::msg::dds_::Odometry_";

    /// <inheritdoc />
    public static int MaxSerializedSize => 1024;

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);
        Header.Write(ref writer);
        writer.WriteString(ChildFrameId);

        writer.WriteDouble(Pose.Position.X);
        writer.WriteDouble(Pose.Position.Y);
        writer.WriteDouble(Pose.Position.Z);
        writer.WriteDouble(Pose.Orientation.X);
        writer.WriteDouble(Pose.Orientation.Y);
        writer.WriteDouble(Pose.Orientation.Z);
        writer.WriteDouble(Pose.Orientation.W);
        WritePoseCovariance(ref writer);

        writer.WriteDouble(Twist.Linear.X);
        writer.WriteDouble(Twist.Linear.Y);
        writer.WriteDouble(Twist.Linear.Z);
        writer.WriteDouble(Twist.Angular.X);
        writer.WriteDouble(Twist.Angular.Y);
        writer.WriteDouble(Twist.Angular.Z);
        WritePoseCovariance(ref writer);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static Ros2Odometry Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);

        var odometry = new Ros2Odometry
        {
            Header = Ros2Header.Read(ref reader),
            ChildFrameId = reader.ReadString(),
        };

        var position = new Vector3(
            (float)reader.ReadDouble(),
            (float)reader.ReadDouble(),
            (float)reader.ReadDouble());

        var orientation = new Quaternion(
            (float)reader.ReadDouble(),
            (float)reader.ReadDouble(),
            (float)reader.ReadDouble(),
            (float)reader.ReadDouble());

        odometry.Pose = new Pose(position, orientation);
        SkipPoseCovariance(ref reader);

        odometry.Twist = new Ros2Twist
        {
            Linear = new Vector3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble()),
            Angular = new Vector3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble()),
        };

        SkipPoseCovariance(ref reader);
        return odometry;
    }

    /// <summary>Writes the 6×6 covariance matrix that pose and twist each carry.</summary>
    private static void WritePoseCovariance(ref CdrWriter writer)
    {
        // Diagonal entries express the drift Unitree odometry actually exhibits: a few centimetres in
        // translation and a few degrees in yaw over a short run. Reporting something honest here lets a
        // downstream filter weight this source sensibly instead of over-trusting it.
        Span<double> diagonal = [0.05, 0.05, 0.02, 0.02, 0.02, 0.1];

        for (int row = 0; row < 6; row++)
        {
            for (int column = 0; column < 6; column++)
            {
                writer.WriteDouble(row == column ? diagonal[row] : 0.0);
            }
        }
    }

    private static void SkipPoseCovariance(ref CdrReader reader)
    {
        for (int i = 0; i < 36; i++)
        {
            reader.ReadDouble();
        }
    }
}
