using DepthAI.Backends;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;

namespace DepthAI.Simulation;

/// <summary>
/// Menerjemahkan pipeline menjadi sekumpulan generator paket. Rencana disusun sekali
/// saat start supaya loop per-frame hanya melakukan pekerjaan pembangkitan data.
/// </summary>
internal sealed class SimulationPlan
{
    private readonly List<StreamGenerator> _generators = [];

    private SimulationPlan(SyntheticScene scene, int fps)
    {
        Scene = scene;
        Fps = fps;
    }

    public SyntheticScene Scene { get; }

    public int Fps { get; }

    public static SimulationPlan Build(Pipeline pipeline, int seed)
    {
        // Label diambil dari model pertama supaya deteksi sintetis memakai kosakata
        // yang sama dengan yang diharapkan aplikasi.
        var labels = pipeline.Nodes
            .OfType<NeuralNetworkNode>()
            .Select(static n => n.Model?.Metadata.Labels)
            .FirstOrDefault(static l => l is { Count: > 0 }) ?? [];

        var fps = pipeline.Nodes
            .Select(static node => node switch
            {
                ColorCameraNode color => color.Fps,
                MonoCameraNode mono => mono.Fps,
                _ => 0,
            })
            .Where(static f => f > 0)
            .DefaultIfEmpty(30)
            .Max();

        var plan = new SimulationPlan(new SyntheticScene(labels, seed), fps);

        foreach (var stream in pipeline.OutputStreams)
        {
            var output = pipeline.ResolveOutput(stream.Source);
            var generator = CreateGenerator(stream.Name, output, pipeline);

            if (generator is not null)
            {
                plan._generators.Add(generator);
            }
        }

        return plan;
    }

    public IEnumerable<DevicePacket> GeneratePackets(TimeSpan timestamp)
    {
        foreach (var generator in _generators)
        {
            var packet = generator.Generate(Scene, timestamp);
            if (packet is not null)
            {
                yield return packet;
            }
        }
    }

    private static StreamGenerator? CreateGenerator(string streamName, NodeOutput output, Pipeline pipeline)
    {
        switch (output.Node)
        {
            case ColorCameraNode camera:
            {
                var (width, height) = output.Name switch
                {
                    "preview" => (camera.PreviewWidth, camera.PreviewHeight),
                    _ => camera.GetSensorSize(),
                };

                return new ColorGenerator(streamName, width, height, camera.ColorOrder == ColorOrder.Bgr);
            }

            case MonoCameraNode mono:
            {
                var (width, height) = mono.GetSensorSize();
                return new MonoGenerator(streamName, width, height);
            }

            case StereoDepthNode stereo:
            {
                var (width, height) = ResolveStereoSize(stereo, pipeline);

                return output.Name switch
                {
                    "depth" => new DepthGenerator(streamName, width, height),
                    "disparity" => new MonoGenerator(streamName, width, height),
                    _ => new MonoGenerator(streamName, width, height),
                };
            }

            case NeuralNetworkNode nn when output.Name is "detections" or "out":
                return new TensorGenerator(streamName, nn.Model?.Metadata ?? ModelMetadata.Default);

            case NeuralNetworkNode nn when output.Name is "passthrough":
            {
                var metadata = nn.Model?.Metadata ?? ModelMetadata.Default;
                return new ColorGenerator(streamName, metadata.InputWidth, metadata.InputHeight, bgr: true);
            }

            case VideoEncoderNode:
                // Simulasi tidak benar-benar mengompresi; frame dipancarkan sebagai piksel
                // mentah supaya kode hilir tetap punya sesuatu untuk ditulis atau ditampilkan.
                return new ColorGenerator(streamName, 1920, 1080, bgr: true);

            case ImuNode:
                return new ImuGenerator(streamName);

            default:
                return null;
        }
    }

    /// <summary>Kedalaman mengikuti resolusi kamera mono yang memberinya masukan.</summary>
    private static (int Width, int Height) ResolveStereoSize(StereoDepthNode stereo, Pipeline pipeline)
    {
        if (stereo.OutputWidth > 0 && stereo.OutputHeight > 0)
        {
            return (stereo.OutputWidth, stereo.OutputHeight);
        }

        var leftLink = pipeline.Links.FirstOrDefault(l =>
            string.Equals(l.To, stereo.Left.Path, StringComparison.OrdinalIgnoreCase));

        if (leftLink is not null)
        {
            var sourceNode = leftLink.From[..leftLink.From.LastIndexOf('.')];
            if (pipeline.Nodes.FirstOrDefault(n =>
                string.Equals(n.Name, sourceNode, StringComparison.OrdinalIgnoreCase)) is MonoCameraNode mono)
            {
                return mono.GetSensorSize();
            }
        }

        return (640, 400);
    }

    private abstract class StreamGenerator(string streamName)
    {
        protected string StreamName { get; } = streamName;

        public abstract DevicePacket? Generate(SyntheticScene scene, TimeSpan timestamp);
    }

    private sealed class ColorGenerator(string streamName, int width, int height, bool bgr)
        : StreamGenerator(streamName)
    {
        public override DevicePacket Generate(SyntheticScene scene, TimeSpan timestamp)
        {
            var payload = new byte[width * height * 3];
            scene.RenderColor(payload, width, height, bgr);

            return new DevicePacket
            {
                StreamName = StreamName,
                Kind = PacketKind.Image,
                Payload = payload,
                Width = width,
                Height = height,
                Format = bgr ? PixelFormat.Bgr888 : PixelFormat.Rgb888,
                SequenceNumber = scene.FrameNumber,
                DeviceTimestamp = timestamp,
            };
        }
    }

    private sealed class MonoGenerator(string streamName, int width, int height) : StreamGenerator(streamName)
    {
        public override DevicePacket Generate(SyntheticScene scene, TimeSpan timestamp)
        {
            var color = new byte[width * height * 3];
            scene.RenderColor(color, width, height, bgr: true);

            // Luma BT.601 dari frame warna yang sama, supaya keluaran mono benar-benar
            // menampilkan adegan yang sama dengan keluaran warna.
            var payload = new byte[width * height];
            for (var i = 0; i < payload.Length; i++)
            {
                var offset = i * 3;
                payload[i] = (byte)((color[offset + 2] * 0.299f) + (color[offset + 1] * 0.587f) + (color[offset] * 0.114f));
            }

            return new DevicePacket
            {
                StreamName = StreamName,
                Kind = PacketKind.Image,
                Payload = payload,
                Width = width,
                Height = height,
                Format = PixelFormat.Gray8,
                SequenceNumber = scene.FrameNumber,
                DeviceTimestamp = timestamp,
            };
        }
    }

    private sealed class DepthGenerator(string streamName, int width, int height) : StreamGenerator(streamName)
    {
        public override DevicePacket Generate(SyntheticScene scene, TimeSpan timestamp)
        {
            var depth = new ushort[width * height];
            scene.RenderDepth(depth, width, height);

            var payload = new byte[depth.Length * sizeof(ushort)];
            Buffer.BlockCopy(depth, 0, payload, 0, payload.Length);

            return new DevicePacket
            {
                StreamName = StreamName,
                Kind = PacketKind.Depth,
                Payload = payload,
                Width = width,
                Height = height,
                SequenceNumber = scene.FrameNumber,
                DeviceTimestamp = timestamp,
            };
        }
    }

    /// <summary>
    /// Menghasilkan tensor dalam tata letak asli keluarga model, sehingga parser
    /// sungguhan yang mengurainya — bukan jalur pintas khusus simulasi.
    /// </summary>
    private sealed class TensorGenerator(string streamName, ModelMetadata metadata) : StreamGenerator(streamName)
    {
        private const int SsdSlots = 100;
        private const int YoloAnchors = 512;

        public override DevicePacket Generate(SyntheticScene scene, TimeSpan timestamp)
        {
            var tensor = metadata.Family switch
            {
                ModelFamily.Yolo => BuildYolo(scene),
                ModelFamily.Classification => BuildClassification(scene),
                ModelFamily.Segmentation => BuildSegmentation(scene),
                _ => BuildSsd(scene),
            };

            return new DevicePacket
            {
                StreamName = StreamName,
                Kind = PacketKind.NeuralTensors,
                Width = metadata.InputWidth,
                Height = metadata.InputHeight,
                Tensors = new Dictionary<string, Tensor> { [tensor.Name] = tensor },
                SequenceNumber = scene.FrameNumber,
                DeviceTimestamp = timestamp,
            };
        }

        private static Tensor BuildSsd(SyntheticScene scene)
        {
            var data = new float[SsdSlots * 7];
            var slot = 0;

            foreach (var item in scene.Objects)
            {
                if (slot >= SsdSlots)
                {
                    break;
                }

                var box = item.Box;
                var offset = slot * 7;
                data[offset] = 0;
                data[offset + 1] = item.LabelIndex;
                data[offset + 2] = scene.ConfidenceFor(item);
                data[offset + 3] = box.XMin;
                data[offset + 4] = box.YMin;
                data[offset + 5] = box.XMax;
                data[offset + 6] = box.YMax;
                slot++;
            }

            // image_id = -1 menandai akhir deteksi nyata, seperti keluaran SSD asli.
            if (slot < SsdSlots)
            {
                data[slot * 7] = -1;
            }

            return new Tensor("detection_out", data, [1, 1, SsdSlots, 7]);
        }

        private Tensor BuildYolo(SyntheticScene scene)
        {
            var classCount = Math.Max(1, metadata.Labels.Count);
            var attributes = 4 + classCount;
            var data = new float[attributes * YoloAnchors];
            var anchor = 0;

            foreach (var item in scene.Objects)
            {
                if (anchor >= YoloAnchors)
                {
                    break;
                }

                var box = item.Box;

                // Tata letak anchor-free menyimpan tiap atribut secara bersebelahan
                // sepanjang sumbu anchor.
                data[(0 * YoloAnchors) + anchor] = box.CenterX * metadata.InputWidth;
                data[(1 * YoloAnchors) + anchor] = box.CenterY * metadata.InputHeight;
                data[(2 * YoloAnchors) + anchor] = box.Width * metadata.InputWidth;
                data[(3 * YoloAnchors) + anchor] = box.Height * metadata.InputHeight;

                var labelIndex = Math.Min(item.LabelIndex, classCount - 1);
                data[((4 + labelIndex) * YoloAnchors) + anchor] = scene.ConfidenceFor(item);
                anchor++;
            }

            return new Tensor("output0", data, [1, attributes, YoloAnchors]);
        }

        private Tensor BuildClassification(SyntheticScene scene)
        {
            var classCount = Math.Max(2, metadata.Labels.Count);
            var data = new float[classCount];
            var winner = (int)(scene.FrameNumber / 60 % classCount);

            for (var i = 0; i < classCount; i++)
            {
                data[i] = i == winner ? 4.5f : scene.Jitter(1.0f);
            }

            return new Tensor("prob", data, [1, classCount]);
        }

        private Tensor BuildSegmentation(SyntheticScene scene)
        {
            const int Width = 128;
            const int Height = 96;

            var classCount = Math.Max(2, metadata.Labels.Count);
            var data = new float[classCount * Width * Height];

            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var pixel = (y * Width) + x;
                    var winner = 0;

                    for (var o = 0; o < scene.Objects.Count; o++)
                    {
                        var (px, py, pw, ph) = scene.Objects[o].Box.ToPixels(Width, Height);
                        if (x >= px && x < px + pw && y >= py && y < py + ph)
                        {
                            winner = Math.Min(scene.Objects[o].LabelIndex, classCount - 1);
                            break;
                        }
                    }

                    data[(winner * Width * Height) + pixel] = 5f;
                }
            }

            return new Tensor("segmentation", data, [1, classCount, Height, Width]);
        }
    }

    /// <summary>
    /// Memancarkan satu sampel IMU per frame. IMU sungguhan berjalan jauh lebih cepat
    /// dari laju kamera, tapi menyamakannya dengan frame membuat kode contoh yang
    /// menggabungkan pose dengan gambar tetap sederhana.
    /// </summary>
    private sealed class ImuGenerator(string streamName) : StreamGenerator(streamName)
    {
        public override DevicePacket Generate(SyntheticScene scene, TimeSpan timestamp)
        {
            var t = (float)timestamp.TotalSeconds;

            return new DevicePacket
            {
                StreamName = StreamName,
                Kind = PacketKind.Imu,
                SequenceNumber = scene.FrameNumber,
                DeviceTimestamp = timestamp,
                Imu = new ImuReading
                {
                    // Gravitasi pada sumbu Z plus goyangan halus pada dua sumbu lain.
                    Accelerometer = (MathF.Sin(t) * 0.4f, MathF.Cos(t * 0.7f) * 0.3f, 9.81f),
                    Gyroscope = (MathF.Sin(t * 1.3f) * 0.05f, MathF.Cos(t) * 0.04f, 0.01f),
                    Timestamp = timestamp,
                },
            };
        }
    }
}
