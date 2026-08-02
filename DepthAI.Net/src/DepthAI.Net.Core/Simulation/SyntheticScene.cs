using DepthAI.Inference;

namespace DepthAI.Simulation;

/// <summary>Satu objek bergerak dalam adegan sintetis.</summary>
internal sealed class SceneObject
{
    public required string Label { get; init; }

    public required int LabelIndex { get; init; }

    /// <summary>Posisi tengah ternormalisasi.</summary>
    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; init; }

    public float Height { get; init; }

    public float VelocityX { get; set; }

    public float VelocityY { get; set; }

    /// <summary>Jarak dari kamera dalam meter; berosilasi supaya kedalaman ikut berubah.</summary>
    public float DistanceMeters { get; set; }

    public float DistanceVelocity { get; set; }

    public required (byte R, byte G, byte B) Color { get; init; }

    public BoundingBox Box => new BoundingBox(
        X - (Width / 2f), Y - (Height / 2f), X + (Width / 2f), Y + (Height / 2f)).Clamp();
}

/// <summary>
/// Adegan deterministik yang bergerak dan menjadi sumber semua data simulasi. Warna,
/// kedalaman, dan deteksi diturunkan dari adegan yang sama, sehingga peta kedalaman
/// benar-benar sejajar dengan objek yang terlihat di frame warna — sifat yang
/// dibutuhkan supaya sample deteksi spasial bermakna.
/// </summary>
internal sealed class SyntheticScene
{
    private static readonly string[] DefaultLabels =
        ["person", "bottle", "chair", "laptop", "cup", "keyboard", "monitor", "book"];

    private readonly List<SceneObject> _objects = [];
    private readonly Random _random;

    public SyntheticScene(IReadOnlyList<string> labels, int seed = 1337)
    {
        _random = new Random(seed);
        var palette = new (byte R, byte G, byte B)[]
        {
            (232, 93, 117), (86, 182, 194), (247, 184, 75), (129, 199, 132),
        };

        var effectiveLabels = labels.Count > 0 ? labels : DefaultLabels;

        for (var i = 0; i < 3; i++)
        {
            var index = i % effectiveLabels.Count;
            _objects.Add(new SceneObject
            {
                Label = effectiveLabels[index],
                LabelIndex = index,
                X = 0.25f + (0.25f * i),
                Y = 0.35f + (0.15f * (i % 2)),
                Width = 0.16f + (0.05f * i),
                Height = 0.28f + (0.06f * i),
                VelocityX = 0.004f * (i % 2 == 0 ? 1 : -1),
                VelocityY = 0.002f * (i % 3 == 0 ? 1 : -1),
                DistanceMeters = 1.2f + (0.8f * i),
                DistanceVelocity = 0.01f * (i % 2 == 0 ? 1 : -1),
                Color = palette[i % palette.Length],
            });
        }
    }

    public IReadOnlyList<SceneObject> Objects => _objects;

    public long FrameNumber { get; private set; }

    /// <summary>Memajukan adegan satu frame, memantulkan objek pada tepi bingkai.</summary>
    public void Advance()
    {
        FrameNumber++;

        foreach (var item in _objects)
        {
            item.X += item.VelocityX;
            item.Y += item.VelocityY;
            item.DistanceMeters += item.DistanceVelocity;

            if (item.X - (item.Width / 2) <= 0 || item.X + (item.Width / 2) >= 1)
            {
                item.VelocityX = -item.VelocityX;
                item.X = Math.Clamp(item.X, item.Width / 2, 1 - (item.Width / 2));
            }

            if (item.Y - (item.Height / 2) <= 0 || item.Y + (item.Height / 2) >= 1)
            {
                item.VelocityY = -item.VelocityY;
                item.Y = Math.Clamp(item.Y, item.Height / 2, 1 - (item.Height / 2));
            }

            if (item.DistanceMeters is < 0.6f or > 4.5f)
            {
                item.DistanceVelocity = -item.DistanceVelocity;
                item.DistanceMeters = Math.Clamp(item.DistanceMeters, 0.6f, 4.5f);
            }
        }
    }

    /// <summary>
    /// Merender frame warna interleaved. Latar berupa gradien vertikal supaya orientasi
    /// gambar langsung terlihat saat viewer keliru menukar baris.
    /// </summary>
    public void RenderColor(Span<byte> destination, int width, int height, bool bgr)
    {
        for (var y = 0; y < height; y++)
        {
            var shade = (byte)(30 + (60 * y / Math.Max(1, height)));

            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                Write(destination, offset, shade, (byte)(shade + 10), (byte)(shade + 26), bgr);
            }
        }

        foreach (var item in _objects)
        {
            var (px, py, pw, ph) = item.Box.ToPixels(width, height);

            for (var y = Math.Max(0, py); y < Math.Min(height, py + ph); y++)
            {
                for (var x = Math.Max(0, px); x < Math.Min(width, px + pw); x++)
                {
                    var offset = ((y * width) + x) * 3;
                    Write(destination, offset, item.Color.R, item.Color.G, item.Color.B, bgr);
                }
            }
        }

        // Garis sapuan bergerak: penanda visual bahwa frame benar-benar diperbarui.
        var sweepX = (int)(FrameNumber * 3 % width);
        for (var y = 0; y < height; y++)
        {
            var offset = ((y * width) + sweepX) * 3;
            Write(destination, offset, 255, 255, 255, bgr);
        }
    }

    /// <summary>Merender peta kedalaman milimeter yang konsisten dengan frame warna.</summary>
    public void RenderDepth(Span<ushort> destination, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            // Latar miring seperti lantai yang menjauh ke arah atas frame.
            var background = (ushort)(2500 + (2000 * (height - y) / Math.Max(1, height)));

            for (var x = 0; x < width; x++)
            {
                destination[(y * width) + x] = background;
            }
        }

        foreach (var item in _objects)
        {
            var (px, py, pw, ph) = item.Box.ToPixels(width, height);
            var depthMm = (ushort)Math.Clamp(item.DistanceMeters * 1000f, 0, ushort.MaxValue);

            for (var y = Math.Max(0, py); y < Math.Min(height, py + ph); y++)
            {
                for (var x = Math.Max(0, px); x < Math.Min(width, px + pw); x++)
                {
                    destination[(y * width) + x] = depthMm;
                }
            }
        }

        // Sebagian piksel dikosongkan supaya konsumen benar-benar menangani
        // kasus "tidak ada pengukuran", persis seperti pada hardware sungguhan.
        for (var i = 0; i < destination.Length; i += 997)
        {
            destination[i] = 0;
        }
    }

    /// <summary>Skor keyakinan yang bergoyang halus, sehingga UI terlihat hidup.</summary>
    public float ConfidenceFor(SceneObject item)
    {
        var phase = (FrameNumber + (item.LabelIndex * 17)) % 120 / 120f;
        return 0.72f + (0.2f * MathF.Sin(phase * MathF.Tau));
    }

    public float Jitter(float magnitude) => (float)((_random.NextDouble() - 0.5) * 2 * magnitude);

    private static void Write(Span<byte> destination, int offset, byte r, byte g, byte b, bool bgr)
    {
        if (offset + 2 >= destination.Length)
        {
            return;
        }

        if (bgr)
        {
            destination[offset] = b;
            destination[offset + 1] = g;
            destination[offset + 2] = r;
        }
        else
        {
            destination[offset] = r;
            destination[offset + 1] = g;
            destination[offset + 2] = b;
        }
    }
}
