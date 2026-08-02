using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines.Nodes;

namespace DepthAI.Pipelines;

/// <summary>
/// Resep pipeline siap pakai. Dipakai bersama oleh CLI, template proyek, dan generator
/// kode wizard, supaya "cara benar" menyusun pipeline hanya ditulis di satu tempat.
/// </summary>
public static class PipelinePresets
{
    /// <summary>Nama preset yang dikenali <see cref="Create"/>.</summary>
    public static IReadOnlyDictionary<string, string> Available { get; } = new Dictionary<string, string>
    {
        ["rgb-preview"] = "Preview kamera warna saja — pipeline paling sederhana.",
        ["stereo-depth"] = "Stereo depth plus preview warna.",
        ["object-detection"] = "Deteksi objek 2D pada preview kamera warna.",
        ["spatial-detection"] = "Deteksi objek dengan koordinat 3D (butuh stereo).",
        ["record-rgbd"] = "Rekam RGB terkompresi bersama peta kedalaman.",
        ["imu-stream"] = "Aliran gerak dari IMU on-board.",
    };

    /// <summary>
    /// Membuat pipeline dari nama preset.
    /// </summary>
    /// <param name="preset">Salah satu kunci di <see cref="Available"/>.</param>
    /// <param name="model">
    /// Model untuk preset berbasis deteksi. Preset itu tetap bisa dibuat tanpa model
    /// (berguna untuk membuat kerangka JSON), tapi tidak akan lolos validasi sampai
    /// modelnya dipasang.
    /// </param>
    /// <param name="fps">Laju frame kamera.</param>
    public static Pipeline Create(string preset, NeuralModel? model = null, int fps = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);

        return preset.ToLowerInvariant() switch
        {
            "rgb-preview" => RgbPreview(fps),
            "stereo-depth" => StereoDepth(fps),
            "object-detection" => ObjectDetection(model, fps),
            "spatial-detection" => SpatialDetection(model, fps),
            "record-rgbd" => RecordRgbd(fps),
            "imu-stream" => ImuStream(fps),
            _ => throw new ArgumentException(
                $"Preset '{preset}' tidak dikenal. Pilihan: {string.Join(", ", Available.Keys)}.", nameof(preset)),
        };
    }

    /// <summary>Kamera warna dengan satu stream preview.</summary>
    public static Pipeline RgbPreview(int fps = 30)
        => Pipeline.CreateBuilder()
            .AddColorCamera("rgb", c =>
            {
                c.Fps = fps;
                c.WithPreview(640, 480);
            })
            .StreamOut("rgb.preview", "video")
            .BuildUnvalidated();

    /// <summary>Stereo depth diselaraskan ke kamera warna, plus preview warna.</summary>
    public static Pipeline StereoDepth(int fps = 30)
        => Pipeline.CreateBuilder()
            .AddColorCamera("rgb", c =>
            {
                c.Fps = fps;
                c.WithPreview(640, 400);
            })
            .AddStereoDepth("stereo", d =>
            {
                d.Preset = DepthPreset.HighDensity;
                d.LeftRightCheck = true;

                // Diselaraskan ke kamera warna supaya piksel depth dan piksel RGB
                // merujuk titik dunia yang sama — syarat overlay yang benar.
                d.AlignTo = CameraSocket.Rgb;
            }, fps: fps)
            .StreamOut("rgb.preview", "video")
            .StreamOut("stereo.depth", "depth")
            .BuildUnvalidated();

    /// <summary>Deteksi objek 2D pada preview kamera warna.</summary>
    public static Pipeline ObjectDetection(NeuralModel? model, int fps = 30)
    {
        var builder = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", c =>
            {
                c.Fps = fps;
                c.WithPreview(640, 480);
            });

        if (model is not null)
        {
            builder.AddObjectDetection(model, "rgb.preview", "detector");
            builder.StreamOut("detector.detections", "detections");
        }

        return builder
            .StreamOut("rgb.preview", "video")
            .BuildUnvalidated();
    }

    /// <summary>Deteksi objek yang membawa koordinat 3D per objek.</summary>
    public static Pipeline SpatialDetection(NeuralModel? model, int fps = 30)
    {
        var builder = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", c =>
            {
                c.Fps = fps;
                c.WithPreview(640, 400);
            });

        if (model is not null)
        {
            builder.AddSpatialObjectDetection(model, "rgb.preview", "detector");
            builder.StreamOut("detector.detections", "detections");
            builder.StreamOut("detector_stereo.depth", "depth");
        }
        else
        {
            builder.AddStereoDepth("stereo", d => d.AlignTo = CameraSocket.Rgb, fps: fps);
            builder.StreamOut("stereo.depth", "depth");
        }

        return builder
            .StreamOut("rgb.preview", "video")
            .BuildUnvalidated();
    }

    /// <summary>Video warna terkompresi berdampingan dengan kedalaman, untuk perekaman dataset.</summary>
    public static Pipeline RecordRgbd(int fps = 30)
    {
        var pipeline = Pipeline.Create();

        var camera = pipeline.AddColorCamera("rgb", c =>
        {
            c.Fps = fps;
            c.Resolution = ColorResolution.The1080P;
            c.WithPreview(640, 400);
        });

        var encoder = pipeline.AddVideoEncoder("encoder", e =>
        {
            e.Profile = VideoProfile.H265Main;
            e.Fps = fps;
            e.BitrateKbps = 8000;
        });

        camera.Video.LinkTo(encoder.Input);

        var left = pipeline.AddMonoCamera("left", c =>
        {
            c.Socket = CameraSocket.Left;
            c.Fps = fps;
        });

        var right = pipeline.AddMonoCamera("right", c =>
        {
            c.Socket = CameraSocket.Right;
            c.Fps = fps;
        });

        var stereo = pipeline.AddStereoDepth("stereo", d =>
        {
            d.Preset = DepthPreset.HighAccuracy;
            d.AlignTo = CameraSocket.Rgb;
        });

        left.Out.LinkTo(stereo.Left);
        right.Out.LinkTo(stereo.Right);

        pipeline.AddOutputStream(encoder.Bitstream, "video");
        pipeline.AddOutputStream(stereo.Depth, "depth");
        pipeline.AddOutputStream(camera.Preview, "preview");

        return pipeline;
    }

    /// <summary>Aliran IMU berdampingan dengan preview warna.</summary>
    public static Pipeline ImuStream(int fps = 30)
        => Pipeline.CreateBuilder()
            .AddColorCamera("rgb", c =>
            {
                c.Fps = fps;
                c.WithPreview(640, 480);
            })
            .Configure(p => p.AddImu("imu", i =>
            {
                i.Sensors = ImuSensors.Accelerometer | ImuSensors.Gyroscope | ImuSensors.RotationVector;
                i.RateHz = 200;
            }))
            .StreamOut("rgb.preview", "video")
            .StreamOut("imu.out", "imu")
            .BuildUnvalidated();
}
