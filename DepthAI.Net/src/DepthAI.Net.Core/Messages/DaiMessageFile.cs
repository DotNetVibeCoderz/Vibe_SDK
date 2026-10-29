using System.Runtime.InteropServices;
using DepthAI.Inference;
using DepthAI.Streaming;

namespace DepthAI.Messages;

/// <summary>
/// Menyimpan dan memuat frame sebagai berkas pesan <c>.dai</c>, format yang dipakai
/// <c>msg.save()</c>/<c>msg.load()</c> di depthai-core 3.10+.
/// </summary>
/// <remarks>
/// <para>
/// Berkasnya berupa amplop protobuf <c>ProtoSerializableMessage</c> (nama skema + pesan),
/// jadi berkas dari DepthAI.Net bisa dibuka Python/C++ depthai dan sebaliknya:
/// </para>
/// <code>
/// frame = dai.ImgFrame(); frame.load("capture.dai")   # Python
/// </code>
/// <para>
/// Yang didukung: <c>ImgFrame</c> (warna, grayscale, NV12, dan kedalaman RAW16) serta
/// <c>ImgDetections</c>. Data yang tidak punya tempat di skema upstream tidak ikut
/// tersimpan: kalibrasi <see cref="DepthFrame"/>, ukuran sumber
/// <see cref="DetectionFrame"/>, dan posisi spasial deteksi.
/// </para>
/// </remarks>
public static class DaiMessageFile
{
    /// <summary>Ekstensi yang ditambahkan bila path tidak punya ekstensi, sama seperti upstream.</summary>
    public const string Extension = ".dai";

    /// <summary>Nama skema protobuf <c>ImgFrame</c>.</summary>
    public const string ImgFrameSchema = "dai.proto.img_frame.ImgFrame";

    /// <summary>Nama skema protobuf <c>ImgDetections</c>.</summary>
    public const string ImgDetectionsSchema = "dai.proto.img_detections.ImgDetections";

    /// <summary>Menyimpan frame gambar ke berkas <c>.dai</c>.</summary>
    /// <exception cref="NotSupportedException">Format piksel tidak punya padanan di depthai (BGRA).</exception>
    public static void Save(this ImageFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var spec = ImgFrameTypes.FromPixelFormat(frame);
        WriteEnvelope(path, ImgFrameSchema, WriteImgFrame(frame, spec, frame.Pixels));
    }

    /// <summary>Menyimpan peta kedalaman sebagai <c>ImgFrame</c> RAW16 dalam milimeter.</summary>
    public static void Save(this DepthFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var spec = new FrameSpec(ImgFrameTypes.Raw16, frame.Width, frame.Height, frame.Width * 2, 2);
        WriteEnvelope(path, ImgFrameSchema, WriteImgFrame(frame, spec, MemoryMarshal.AsBytes(frame.Millimeters)));
    }

    /// <summary>Menyimpan hasil deteksi sebagai <c>ImgDetections</c>.</summary>
    public static void Save(this DetectionFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var message = new ProtoWriter();
        message.WriteInt64(1, frame.SequenceNumber);
        message.WriteMessage(2, w => WriteTimestamp(w, frame.DeviceTimestamp));
        message.WriteMessage(3, w => WriteTimestamp(w, frame.DeviceTimestamp));

        foreach (var detection in frame.Detections)
        {
            message.WriteMessage(4, w =>
            {
                var box = detection.Box;
                w.WriteVarint(1, (uint)Math.Max(0, detection.LabelIndex));
                w.WriteFloat(2, detection.Confidence);
                w.WriteFloat(3, box.XMin);
                w.WriteFloat(4, box.YMin);
                w.WriteFloat(5, box.XMax);
                w.WriteFloat(6, box.YMax);
                w.WriteString(7, detection.Label);

                // Upstream selalu menyertakan versi RotatedRect dari kotak yang sama.
                w.WriteMessage(8, rect =>
                {
                    rect.WriteMessage(1, p =>
                    {
                        p.WriteFloat(1, box.CenterX);
                        p.WriteFloat(2, box.CenterY);
                        p.WriteBool(3, true);
                    });
                    rect.WriteMessage(2, s =>
                    {
                        s.WriteFloat(1, box.Width);
                        s.WriteFloat(2, box.Height);
                        s.WriteBool(3, true);
                    });
                });
            });
        }

        message.WriteMessage(9, w => WriteUnixTimestamp(w, frame.HostTimestamp));
        WriteEnvelope(path, ImgDetectionsSchema, message.ToArray());
    }

    /// <summary>Membaca nama skema berkas tanpa mengurai isinya.</summary>
    public static string ReadSchemaName(string path) => ReadEnvelope(path).Schema;

    /// <summary>
    /// Memuat berkas <c>.dai</c> menjadi frame yang sesuai: <see cref="DepthFrame"/> untuk
    /// <c>ImgFrame</c> RAW16, <see cref="ImageFrame"/> untuk <c>ImgFrame</c> lain, dan
    /// <see cref="DetectionFrame"/> untuk <c>ImgDetections</c>.
    /// </summary>
    public static Frame Load(string path)
    {
        var envelope = ReadEnvelope(path);
        return envelope.Schema switch
        {
            ImgFrameSchema => ReadImgFrame(envelope, path) is var img && img.Spec.Type == ImgFrameTypes.Raw16
                ? ToDepthFrame(img)
                : ToImageFrame(img, path),
            ImgDetectionsSchema => ReadDetections(envelope.Message),
            _ => throw new NotSupportedException(
                $"Skema '{envelope.Schema}' pada {path} belum didukung DepthAI.Net. "
                + $"Yang didukung: {ImgFrameSchema}, {ImgDetectionsSchema}."),
        };
    }

    /// <summary>Memuat <c>ImgFrame</c> berwarna/grayscale.</summary>
    public static ImageFrame LoadImageFrame(string path)
        => ToImageFrame(ReadImgFrame(ReadEnvelope(path, ImgFrameSchema), path), path);

    /// <summary>Memuat <c>ImgFrame</c> RAW16 sebagai peta kedalaman milimeter.</summary>
    public static DepthFrame LoadDepthFrame(string path)
    {
        var img = ReadImgFrame(ReadEnvelope(path, ImgFrameSchema), path);
        if (img.Spec.Type != ImgFrameTypes.Raw16)
        {
            throw new InvalidDataException(
                $"{path} berisi ImgFrame {ImgFrameTypes.Name(img.Spec.Type)}, bukan kedalaman RAW16.");
        }

        return ToDepthFrame(img);
    }

    /// <summary>Memuat <c>ImgDetections</c>.</summary>
    public static DetectionFrame LoadDetections(string path)
        => ReadDetections(ReadEnvelope(path, ImgDetectionsSchema).Message);

    /// <summary>Path yang benar-benar dipakai: ditambah <see cref="Extension"/> bila tanpa ekstensi.</summary>
    public static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            throw new ArgumentException($"Path harus berisi nama berkas yang valid: {path}", nameof(path));
        }

        return Path.HasExtension(path) ? path : path + Extension;
    }

    private static byte[] WriteImgFrame(Frame frame, FrameSpec spec, ReadOnlySpan<byte> data)
    {
        var message = new ProtoWriter();
        message.WriteMessage(1, w => WriteTimestamp(w, frame.DeviceTimestamp));
        message.WriteMessage(2, w => WriteTimestamp(w, frame.DeviceTimestamp));
        message.WriteInt64(3, frame.SequenceNumber);
        message.WriteMessage(4, w => WriteSpec(w, spec));

        // Frame dari host tidak punya buffer sumber; upstream menandainya dengan tipe NONE.
        message.WriteMessage(5, w => w.WriteVarint(1, ImgFrameTypes.None));
        message.WriteBytes(11, data);
        message.WriteMessage(12, w => WriteUnixTimestamp(w, frame.HostTimestamp));
        return message.ToArray();
    }

    private static void WriteSpec(ProtoWriter writer, FrameSpec spec)
    {
        writer.WriteVarint(1, spec.Type);
        writer.WriteVarint(2, (uint)spec.Width);
        writer.WriteVarint(3, (uint)spec.Height);
        writer.WriteVarint(4, (uint)spec.Stride);
        writer.WriteVarint(5, (uint)spec.BytesPerPixel);
        writer.WriteVarint(6, (uint)spec.P1Offset);
        writer.WriteVarint(7, (uint)spec.P2Offset);
        writer.WriteVarint(8, (uint)spec.P3Offset);
    }

    private static void WriteTimestamp(ProtoWriter writer, TimeSpan value)
    {
        writer.WriteInt64(1, value.Ticks / TimeSpan.TicksPerSecond);
        writer.WriteInt64(2, value.Ticks % TimeSpan.TicksPerSecond * 100);
    }

    /// <summary><c>tsSystem</c> upstream adalah jam dinding (system_clock) sejak epoch Unix.</summary>
    private static void WriteUnixTimestamp(ProtoWriter writer, DateTimeOffset value)
        => WriteTimestamp(writer, value - DateTimeOffset.UnixEpoch);

    private static void WriteEnvelope(string path, string schema, byte[] message)
    {
        var envelope = new ProtoWriter();
        envelope.WriteString(1, schema);
        envelope.WriteBytes(3, message);
        File.WriteAllBytes(ResolvePath(path), envelope.ToArray());
    }

    private static Envelope ReadEnvelope(string path, string? expectedSchema = null)
    {
        var resolved = ResolvePath(path);
        var bytes = File.ReadAllBytes(resolved);
        var reader = new ProtoReader(bytes);
        var schema = string.Empty;
        var metadataOnly = false;
        byte[] message = [];

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == WireType.LengthDelimited:
                    schema = reader.ReadString();
                    break;
                case 2 when type == WireType.Varint:
                    metadataOnly = reader.ReadVarint() != 0;
                    break;
                case 3 when type == WireType.LengthDelimited:
                    message = reader.ReadBytes().ToArray();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        if (expectedSchema is not null && schema != expectedSchema)
        {
            // Pesan yang sama dengan upstream supaya mudah dicari.
            throw new InvalidDataException(
                $"Schema mismatch when reading file: {resolved}. Expected: {expectedSchema}, got: {schema}");
        }

        return new Envelope(schema, metadataOnly, message);
    }

    private static ImgFrameMessage ReadImgFrame(Envelope envelope, string path)
    {
        if (envelope.MetadataOnly)
        {
            throw new InvalidDataException(
                $"{path} disimpan dengan metadataOnly=True sehingga tidak berisi piksel.");
        }

        var reader = new ProtoReader(envelope.Message);
        var spec = default(FrameSpec);
        TimeSpan device = default;
        DateTimeOffset? system = null;
        long sequence = 0;
        byte[] data = [];

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 2 when type == WireType.LengthDelimited:
                    device = ReadTimestamp(reader.ReadBytes());
                    break;
                case 3 when type == WireType.Varint:
                    sequence = reader.ReadInt64();
                    break;
                case 4 when type == WireType.LengthDelimited:
                    spec = ReadSpec(reader.ReadBytes());
                    break;
                case 11 when type == WireType.LengthDelimited:
                    data = reader.ReadBytes().ToArray();
                    break;
                case 12 when type == WireType.LengthDelimited:
                    system = DateTimeOffset.UnixEpoch + ReadTimestamp(reader.ReadBytes());
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        if (spec.Width <= 0 || spec.Height <= 0)
        {
            throw new InvalidDataException($"{path}: ImgFrame tanpa ukuran yang valid ({spec.Width}x{spec.Height}).");
        }

        return new ImgFrameMessage(spec, data, sequence, device, system);
    }

    private static FrameSpec ReadSpec(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        var spec = default(FrameSpec);

        while (reader.TryReadTag(out var field, out var type))
        {
            if (type != WireType.Varint)
            {
                reader.Skip(type);
                continue;
            }

            var value = reader.ReadVarint();
            spec = field switch
            {
                1 => spec with { Type = value },
                2 => spec with { Width = (int)value },
                3 => spec with { Height = (int)value },
                4 => spec with { Stride = (int)value },
                5 => spec with { BytesPerPixel = (int)value },
                6 => spec with { P1Offset = (int)value },
                7 => spec with { P2Offset = (int)value },
                8 => spec with { P3Offset = (int)value },
                _ => spec,
            };
        }

        return spec;
    }

    private static TimeSpan ReadTimestamp(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        long seconds = 0, nanoseconds = 0;

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == WireType.Varint:
                    seconds = reader.ReadInt64();
                    break;
                case 2 when type == WireType.Varint:
                    nanoseconds = reader.ReadInt64();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        return TimeSpan.FromTicks((seconds * TimeSpan.TicksPerSecond) + (nanoseconds / 100));
    }

    private static ImageFrame ToImageFrame(ImgFrameMessage img, string path)
    {
        var spec = img.Spec;
        var (format, planar) = ImgFrameTypes.ToPixelFormat(spec.Type)
            ?? throw new NotSupportedException(
                $"{path}: ImgFrame {ImgFrameTypes.Name(spec.Type)} belum bisa dimuat sebagai ImageFrame.");

        ImageFrame frame;
        if (planar)
        {
            frame = ImageFrame.Wrap(Interleave(img), spec.Width, spec.Height, format);
        }
        else
        {
            var stride = spec.Stride > 0 ? spec.Stride : 0;
            var minimum = format is PixelFormat.Jpeg ? 1 : RequiredLength(spec, format);
            if (img.Data.Length < minimum)
            {
                throw new InvalidDataException(
                    $"{path}: data ImgFrame hanya {img.Data.Length} byte, padahal {spec.Width}x{spec.Height} butuh {minimum}.");
            }

            frame = ImageFrame.Wrap(img.Data, spec.Width, spec.Height, format, stride);
        }

        Stamp(frame, img);
        return frame;
    }

    private static int RequiredLength(FrameSpec spec, PixelFormat format)
    {
        var stride = spec.Stride > 0 ? spec.Stride : spec.Width * Math.Max(1, spec.BytesPerPixel);
        return format == PixelFormat.Nv12 ? stride * spec.Height * 3 / 2 : stride * spec.Height;
    }

    /// <summary>
    /// BGR888p/RGB888p (keluaran ImageManip untuk NN) disusun ulang menjadi interleaved,
    /// karena <see cref="ImageFrame"/> dan seluruh adapter imaging bekerja dengan interleaved.
    /// </summary>
    private static byte[] Interleave(ImgFrameMessage img)
    {
        var spec = img.Spec;
        var stride = spec.Stride > 0 ? spec.Stride : spec.Width;
        var planeSize = stride * spec.Height;
        int[] planes = spec.P2Offset > 0
            ? [spec.P1Offset, spec.P2Offset, spec.P3Offset]
            : [0, planeSize, 2 * planeSize];

        if (planes.Any(p => p + planeSize > img.Data.Length))
        {
            throw new InvalidDataException("Data ImgFrame planar lebih pendek dari tiga bidang warnanya.");
        }

        if (stride == spec.Width && planes[0] == 0 && planes[1] == planeSize && planes[2] == 2 * planeSize)
        {
            return Imaging.PixelConverter.PlanarToInterleaved(img.Data, spec.Width, spec.Height);
        }

        var result = new byte[spec.Width * spec.Height * 3];
        for (var y = 0; y < spec.Height; y++)
        {
            for (var x = 0; x < spec.Width; x++)
            {
                var source = (y * stride) + x;
                var target = ((y * spec.Width) + x) * 3;
                result[target] = img.Data[planes[0] + source];
                result[target + 1] = img.Data[planes[1] + source];
                result[target + 2] = img.Data[planes[2] + source];
            }
        }

        return result;
    }

    private static DepthFrame ToDepthFrame(ImgFrameMessage img)
    {
        var spec = img.Spec;
        var rowBytes = spec.Width * 2;
        var stride = spec.Stride > 0 ? spec.Stride : rowBytes;
        if (img.Data.Length < (stride * (spec.Height - 1)) + rowBytes)
        {
            throw new InvalidDataException(
                $"Data kedalaman hanya {img.Data.Length} byte, tidak cukup untuk {spec.Width}x{spec.Height} RAW16.");
        }

        var millimeters = new ushort[spec.Width * spec.Height];
        var target = MemoryMarshal.AsBytes(millimeters.AsSpan());
        for (var y = 0; y < spec.Height; y++)
        {
            img.Data.AsSpan(y * stride, rowBytes).CopyTo(target.Slice(y * rowBytes, rowBytes));
        }

        var frame = DepthFrame.Wrap(millimeters, spec.Width, spec.Height);
        Stamp(frame, img);
        return frame;
    }

    private static DetectionFrame ReadDetections(byte[] message)
    {
        var reader = new ProtoReader(message);
        var detections = new List<Detection>();
        TimeSpan device = default;
        DateTimeOffset? system = null;
        long sequence = 0;

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == WireType.Varint:
                    sequence = reader.ReadInt64();
                    break;
                case 3 when type == WireType.LengthDelimited:
                    device = ReadTimestamp(reader.ReadBytes());
                    break;
                case 4 when type == WireType.LengthDelimited:
                    detections.Add(ReadDetection(reader.ReadBytes()));
                    break;
                case 9 when type == WireType.LengthDelimited:
                    system = DateTimeOffset.UnixEpoch + ReadTimestamp(reader.ReadBytes());
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        var frame = new DetectionFrame { Detections = detections };
        frame.SequenceNumber = sequence;
        frame.DeviceTimestamp = device;
        if (system is { } host)
        {
            frame.HostTimestamp = host;
        }

        return frame;
    }

    private static Detection ReadDetection(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        int label = 0;
        float confidence = 0, xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        var name = string.Empty;
        BoundingBox? rotated = null;

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == WireType.Varint:
                    label = (int)reader.ReadVarint();
                    break;
                case 2 when type == WireType.Fixed32:
                    confidence = reader.ReadFloat();
                    break;
                case 3 when type == WireType.Fixed32:
                    xMin = reader.ReadFloat();
                    break;
                case 4 when type == WireType.Fixed32:
                    yMin = reader.ReadFloat();
                    break;
                case 5 when type == WireType.Fixed32:
                    xMax = reader.ReadFloat();
                    break;
                case 6 when type == WireType.Fixed32:
                    yMax = reader.ReadFloat();
                    break;
                case 7 when type == WireType.LengthDelimited:
                    name = reader.ReadString();
                    break;
                case 8 when type == WireType.LengthDelimited:
                    rotated = ReadRotatedRect(reader.ReadBytes());
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        // Detektor berbasis RotatedRect (beta parser upstream) bisa mengosongkan xmin..ymax.
        var box = xMin == 0 && yMin == 0 && xMax == 0 && yMax == 0 && rotated is { } r
            ? r
            : new BoundingBox(xMin, yMin, xMax, yMax);

        return new Detection { LabelIndex = label, Label = name, Confidence = confidence, Box = box };
    }

    /// <summary>Kotak sejajar sumbu yang melingkupi RotatedRect (abaikan sudut bila 0).</summary>
    private static BoundingBox ReadRotatedRect(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        float cx = 0, cy = 0, w = 0, h = 0, angle = 0;

        while (reader.TryReadTag(out var field, out var type))
        {
            switch (field)
            {
                case 1 when type == WireType.LengthDelimited:
                    (cx, cy) = ReadPair(reader.ReadBytes());
                    break;
                case 2 when type == WireType.LengthDelimited:
                    (w, h) = ReadPair(reader.ReadBytes());
                    break;
                case 3 when type == WireType.Fixed32:
                    angle = reader.ReadFloat();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        var radians = angle * MathF.PI / 180f;
        var (cos, sin) = (MathF.Abs(MathF.Cos(radians)), MathF.Abs(MathF.Sin(radians)));
        var halfW = ((w * cos) + (h * sin)) / 2f;
        var halfH = ((w * sin) + (h * cos)) / 2f;
        return new BoundingBox(cx - halfW, cy - halfH, cx + halfW, cy + halfH);

        static (float A, float B) ReadPair(ReadOnlySpan<byte> bytes)
        {
            var reader = new ProtoReader(bytes);
            float a = 0, b = 0;
            while (reader.TryReadTag(out var field, out var type))
            {
                switch (field)
                {
                    case 1 when type == WireType.Fixed32:
                        a = reader.ReadFloat();
                        break;
                    case 2 when type == WireType.Fixed32:
                        b = reader.ReadFloat();
                        break;
                    default:
                        reader.Skip(type);
                        break;
                }
            }

            return (a, b);
        }
    }

    private static void Stamp(Frame frame, ImgFrameMessage img)
    {
        frame.SequenceNumber = img.Sequence;
        frame.DeviceTimestamp = img.Device;
        if (img.System is { } host)
        {
            frame.HostTimestamp = host;
        }
    }

    private sealed record Envelope(string Schema, bool MetadataOnly, byte[] Message);

    private sealed record ImgFrameMessage(FrameSpec Spec, byte[] Data, long Sequence, TimeSpan Device, DateTimeOffset? System);
}

/// <summary>Isi <c>Specs</c> pada proto <c>ImgFrame</c>.</summary>
internal readonly record struct FrameSpec(
    ulong Type,
    int Width,
    int Height,
    int Stride,
    int BytesPerPixel,
    int P1Offset = 0,
    int P2Offset = 0,
    int P3Offset = 0);

/// <summary>Nilai enum <c>ImgFrame.Type</c> depthai yang relevan bagi SDK.</summary>
internal static class ImgFrameTypes
{
    public const ulong Yuv400p = 4;
    public const ulong Rgba8888 = 5;
    public const ulong Rgb888p = 7;
    public const ulong Bgr888p = 8;
    public const ulong Rgb888i = 9;
    public const ulong Bgr888i = 10;
    public const ulong Raw16 = 14;
    public const ulong Raw8 = 18;
    public const ulong Nv12 = 22;
    public const ulong Bitstream = 24;
    public const ulong Gray8 = 30;
    public const ulong None = 33;

    private static readonly string[] Names =
    [
        "YUV422i", "YUV444p", "YUV420p", "YUV422p", "YUV400p", "RGBA8888", "RGB161616", "RGB888p",
        "BGR888p", "RGB888i", "BGR888i", "LUT2", "LUT4", "LUT16", "RAW16", "RAW14", "RAW12", "RAW10",
        "RAW8", "PACK10", "PACK12", "YUV444i", "NV12", "NV21", "BITSTREAM", "HDR", "RGBF16F16F16p",
        "BGRF16F16F16p", "RGBF16F16F16i", "BGRF16F16F16i", "GRAY8", "GRAYF16", "RAW32", "NONE",
    ];

    public static string Name(ulong type) => type < (ulong)Names.Length ? Names[type] : $"type {type}";

    public static FrameSpec FromPixelFormat(ImageFrame frame)
    {
        var (w, h, stride) = (frame.Width, frame.Height, frame.Stride);
        return frame.Format switch
        {
            PixelFormat.Bgr888 => new FrameSpec(Bgr888i, w, h, stride, 3),
            PixelFormat.Rgb888 => new FrameSpec(Rgb888i, w, h, stride, 3),
            PixelFormat.Rgba8888 => new FrameSpec(Rgba8888, w, h, stride, 4),
            PixelFormat.Gray8 => new FrameSpec(Gray8, w, h, stride, 1),
            PixelFormat.Nv12 => new FrameSpec(Nv12, w, h, w, 1, 0, w * h),
            PixelFormat.Jpeg => new FrameSpec(Bitstream, w, h, 0, 0),
            _ => throw new NotSupportedException(
                $"Format {frame.Format} tidak punya padanan ImgFrame di depthai. Konversi ke Bgr888 atau Rgba8888 dulu."),
        };
    }

    public static (PixelFormat Format, bool Planar)? ToPixelFormat(ulong type) => type switch
    {
        Bgr888i => (PixelFormat.Bgr888, false),
        Rgb888i => (PixelFormat.Rgb888, false),
        Bgr888p => (PixelFormat.Bgr888, true),
        Rgb888p => (PixelFormat.Rgb888, true),
        Rgba8888 => (PixelFormat.Rgba8888, false),
        Gray8 or Raw8 or Yuv400p => (PixelFormat.Gray8, false),
        Nv12 => (PixelFormat.Nv12, false),
        Bitstream => (PixelFormat.Jpeg, false),
        _ => null,
    };
}
