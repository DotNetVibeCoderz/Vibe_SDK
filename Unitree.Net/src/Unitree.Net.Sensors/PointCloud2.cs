using System.Numerics;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Sensors;

/// <summary>
/// Field data types used by <c>sensor_msgs/PointCloud2</c>.
/// </summary>
public enum PointFieldType : byte
{
    /// <summary>Signed 8-bit integer.</summary>
    Int8 = 1,

    /// <summary>Unsigned 8-bit integer.</summary>
    UInt8 = 2,

    /// <summary>Signed 16-bit integer.</summary>
    Int16 = 3,

    /// <summary>Unsigned 16-bit integer.</summary>
    UInt16 = 4,

    /// <summary>Signed 32-bit integer.</summary>
    Int32 = 5,

    /// <summary>Unsigned 32-bit integer.</summary>
    UInt32 = 6,

    /// <summary>32-bit float.</summary>
    Float32 = 7,

    /// <summary>64-bit float.</summary>
    Float64 = 8,
}

/// <summary>
/// One field within a point record.
/// </summary>
/// <param name="Name">Field name, e.g. <c>x</c> or <c>intensity</c>.</param>
/// <param name="Offset">Byte offset within the point record.</param>
/// <param name="DataType">Element type.</param>
/// <param name="Count">Number of elements.</param>
public readonly record struct PointField(string Name, uint Offset, PointFieldType DataType, uint Count);

/// <summary>
/// A LiDAR point cloud, decoded from <c>sensor_msgs::msg::dds_::PointCloud2_</c>.
/// </summary>
/// <remarks>
/// <para>
/// The raw <see cref="Data"/> block is kept as received rather than expanded into a point array. A Unitree
/// L1 frame carries on the order of 20 000 points at 10 Hz; materialising every frame into objects would
/// allocate tens of megabytes a second and put the GC squarely in the sensor path. Use
/// <see cref="EnumeratePoints"/>, which decodes lazily.
/// </para>
/// </remarks>
public sealed class PointCloud2
{
    /// <summary>Timestamp seconds component.</summary>
    public int StampSeconds { get; init; }

    /// <summary>Timestamp nanoseconds component.</summary>
    public uint StampNanoseconds { get; init; }

    /// <summary>Coordinate frame the points are expressed in.</summary>
    public string FrameId { get; init; } = string.Empty;

    /// <summary>Rows in the cloud; 1 for an unordered cloud.</summary>
    public uint Height { get; init; }

    /// <summary>Points per row.</summary>
    public uint Width { get; init; }

    /// <summary>Field layout of one point record.</summary>
    public IReadOnlyList<PointField> Fields { get; init; } = [];

    /// <summary>Whether multi-byte values are big-endian.</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Size of one point record in bytes.</summary>
    public uint PointStep { get; init; }

    /// <summary>Size of one row in bytes.</summary>
    public uint RowStep { get; init; }

    /// <summary>Raw point data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Whether every point is finite.</summary>
    public bool IsDense { get; init; }

    /// <summary>Total number of points.</summary>
    public long PointCount => (long)Height * Width;

    /// <summary>
    /// Decodes the XYZ coordinates of every point.
    /// </summary>
    /// <returns>A lazy sequence; nothing is allocated per point beyond the returned vector.</returns>
    /// <exception cref="UnitreeException">The cloud has no float32 x, y and z fields.</exception>
    public IEnumerable<Vector3> EnumeratePoints()
    {
        (uint xOffset, uint yOffset, uint zOffset) = ResolveXyzOffsets();

        if (IsBigEndian)
        {
            throw new UnitreeException(
                "Big-endian point clouds are not supported; Unitree LiDAR publishes little-endian data.");
        }

        return EnumerateCore(xOffset, yOffset, zOffset);
    }

    private IEnumerable<Vector3> EnumerateCore(uint xOffset, uint yOffset, uint zOffset)
    {
        int step = (int)PointStep;
        long count = PointCount;

        for (long i = 0; i < count; i++)
        {
            int recordStart = (int)(i * step);

            if (recordStart + step > Data.Length)
            {
                yield break;
            }

            ReadOnlySpan<byte> record = Data.Span.Slice(recordStart, step);

            yield return new Vector3(
                BitConverter.ToSingle(record[(int)xOffset..]),
                BitConverter.ToSingle(record[(int)yOffset..]),
                BitConverter.ToSingle(record[(int)zOffset..]));
        }
    }

    /// <summary>
    /// Finds the closest point within a horizontal angular sector.
    /// </summary>
    /// <param name="centreAngleRadians">Sector centre, measured from the robot's forward axis.</param>
    /// <param name="halfWidthRadians">Half-width of the sector.</param>
    /// <param name="minRangeMetres">Points nearer than this are ignored as self-returns.</param>
    /// <returns>Distance in metres, or <see langword="null"/> when the sector is empty.</returns>
    /// <remarks>
    /// The minimum-range filter is not optional in practice: a body-mounted LiDAR sees the robot's own
    /// chassis, and without the filter every sector reports an obstacle a few centimetres away.
    /// </remarks>
    public float? FindNearestInSector(
        float centreAngleRadians,
        float halfWidthRadians,
        float minRangeMetres = 0.15f)
    {
        float nearest = float.MaxValue;

        foreach (Vector3 point in EnumeratePoints())
        {
            float range = MathF.Sqrt((point.X * point.X) + (point.Y * point.Y));

            if (range < minRangeMetres || range >= nearest)
            {
                continue;
            }

            float angle = MathF.Atan2(point.Y, point.X);

            if (MathF.Abs(RobotMath.AngleDifference(centreAngleRadians, angle)) <= halfWidthRadians)
            {
                nearest = range;
            }
        }

        return nearest == float.MaxValue ? null : nearest;
    }

    private (uint X, uint Y, uint Z) ResolveXyzOffsets()
    {
        uint? x = null;
        uint? y = null;
        uint? z = null;

        foreach (PointField field in Fields)
        {
            if (field.DataType != PointFieldType.Float32)
            {
                continue;
            }

            switch (field.Name)
            {
                case "x":
                    x = field.Offset;
                    break;
                case "y":
                    y = field.Offset;
                    break;
                case "z":
                    z = field.Offset;
                    break;
                default:
                    break;
            }
        }

        if (x is null || y is null || z is null)
        {
            throw new UnitreeException(
                $"Point cloud lacks float32 x/y/z fields; found: {string.Join(", ", Fields.Select(f => f.Name))}.");
        }

        return (x.Value, y.Value, z.Value);
    }

    /// <summary>Decodes a point cloud from a CDR payload.</summary>
    public static PointCloud2 Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);

        int stampSeconds = reader.ReadInt32();
        uint stampNanoseconds = reader.ReadUInt32();
        string frameId = reader.ReadString();

        uint height = reader.ReadUInt32();
        uint width = reader.ReadUInt32();

        uint fieldCount = reader.ReadUInt32();
        var fields = new List<PointField>((int)Math.Min(fieldCount, 64));

        for (uint i = 0; i < fieldCount; i++)
        {
            string name = reader.ReadString();
            uint offset = reader.ReadUInt32();
            byte dataType = reader.ReadByte();
            uint count = reader.ReadUInt32();
            fields.Add(new PointField(name, offset, (PointFieldType)dataType, count));
        }

        bool isBigEndian = reader.ReadBool();
        uint pointStep = reader.ReadUInt32();
        uint rowStep = reader.ReadUInt32();
        byte[] data = reader.ReadByteSequence().ToArray();
        bool isDense = reader.ReadBool();

        return new PointCloud2
        {
            StampSeconds = stampSeconds,
            StampNanoseconds = stampNanoseconds,
            FrameId = frameId,
            Height = height,
            Width = width,
            Fields = fields,
            IsBigEndian = isBigEndian,
            PointStep = pointStep,
            RowStep = rowStep,
            Data = data,
            IsDense = isDense,
        };
    }
}
