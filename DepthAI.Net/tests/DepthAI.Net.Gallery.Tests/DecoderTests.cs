using DepthAI.Gallery.Inference;
using DepthAI.Gallery.Showcase;
using DepthAI.Inference;

namespace DepthAI.Gallery.Tests;

/// <summary>
/// Dekoder galeri diuji dengan tensor buatan yang tata letaknya sama dengan keluaran kamera.
/// Kecocokan dengan hardware sendiri sudah diperiksa di OAK-1 dan tercatat di recorded.json.
/// </summary>
public class DecoderTests
{
    private static Tensor Make(string name, int[] shape, Func<int, float>? fill = null)
    {
        var data = new float[shape.Aggregate(1, (a, b) => a * b)];
        if (fill is not null)
        {
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = fill(i);
            }
        }

        return new Tensor(name, data, shape);
    }

    [Fact]
    public void YuNetPriors_MatchTheModelOutputCount()
    {
        // 17610 adalah jumlah baris tensor loc yang dilaporkan YuNet 640×480 di OAK-1.
        Assert.Equal(17610, YuNetDecoder.Priors(640, 480).Count);
    }

    [Fact]
    public void PalmAnchors_MatchTheModelOutputCount()
        => Assert.Equal(2016, PalmDecoder.Anchors(192).Count);

    [Fact]
    public void YuNet_DecodesAConfidentPriorAtItsCenter()
    {
        var priors = YuNetDecoder.Priors(640, 480);
        var target = priors.FindIndex(p => p.W * 640 >= 63 && p.W * 640 <= 65 && p.X > 0.45f && p.Y > 0.45f);
        var tensors = new Dictionary<string, Tensor>
        {
            ["loc"] = Make("loc", [17610, 14]),
            ["conf"] = Make("conf", [17610, 2], i => i == (target * 2) + 1 ? 0.95f : 0f),
            ["iou"] = Make("iou", [17610, 1], i => i == target ? 0.9f : 0f),
        };

        var face = Assert.Single(YuNetDecoder.Decode(tensors, 640, 480));

        // Offset nol berarti kotak tepat sebesar prior dan berpusat di pusatnya.
        Assert.Equal(priors[target].X * 640, (face.Box.X0 + face.Box.X1) / 2, 0.01f);
        Assert.Equal(64, face.Box.X1 - face.Box.X0, 0.01f);
        Assert.Equal(MathF.Sqrt(0.95f * 0.9f), face.Box.Score, 0.001f);
    }

    [Fact]
    public void YoloGrid_DecodesTlbrDistancesInStrideUnits()
    {
        // Satu sel (kolom 3, baris 2) di grid 8×4 stride 8, dua kelas.
        const int w = 8, h = 4, plane = w * h, cell = (2 * w) + 3;
        var values = new Dictionary<int, float> { [0] = 1, [1] = 1, [2] = 2, [3] = 1, [4] = 0.9f, [6] = 0.8f };
        var layer = Make("output1_yolov8", [1, 7, h, w], i => i % plane == cell && values.TryGetValue(i / plane, out var v) ? v : 0);

        var candidate = Assert.Single(YoloGrid.Decode([layer], [8], 0.5f, 0.5f));

        Assert.Equal(cell, candidate.Cell);
        Assert.Equal(1, candidate.Box.Label);
        Assert.Equal(new ModelBox(20, 12, 44, 28, 0.8f, 1), candidate.Box);
    }

    [Fact]
    public void TextDetection_SeparatesLinesAndDropsSpecks()
    {
        const int w = 60, h = 20;
        bool Inside(int x, int y) => (y is >= 3 and <= 5 && x is >= 5 and <= 40) || (y is >= 12 and <= 14 && x is >= 5 and <= 30);
        var map = Make("output", [1, 1, h, w], i => Inside(i % w, i / w) ? 0.9f : (i == (18 * w) + 55 ? 0.9f : 0f));

        var lines = TextDetectionDecoder.Decode(map);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.X1 - l.X0 > 20));
    }

    [Fact]
    public void Letterbox_RoundTripsAndMapsPaddingOutsideTheImage()
    {
        var letterbox = new Letterbox(1280, 720, 640, 640);

        var (mx, my) = letterbox.ToModel(0.25f, 0.75f);
        var (ix, iy) = letterbox.ToImage(mx, my);
        Assert.Equal(0.25f, ix, 0.0001f);
        Assert.Equal(0.75f, iy, 0.0001f);

        // Pita bingkai di atas gambar 16:9 dalam masukan persegi berada di luar 0–1.
        Assert.True(letterbox.ToImage(0.5f, 0.05f).Y < 0);
    }

    [Fact]
    public void ResultMask_SurvivesSerialization()
    {
        var mask = new ResultMask { Width = 3, Height = 2, Kind = MaskKind.Classes, Values = [1, 2, 3, 255, 0, 7] };

        var copy = new ResultMask { Width = 3, Height = 2, Kind = MaskKind.Classes, Data = mask.Data };

        Assert.Equal(mask.Values, copy.Values);
    }

    [Fact]
    public void ReadPose_RecognisesASquatFromKneeAngles()
    {
        var keypoints = new (float X, float Y, float Score)[17];
        for (var i = 0; i < 17; i++)
        {
            keypoints[i] = (100, 50, 0.9f);
        }

        // Pinggul, lutut, mata kaki kiri dan kanan membentuk sudut 90°; tangan di bawah hidung.
        keypoints[0] = (100, 20, 0.9f);
        keypoints[9] = keypoints[10] = (100, 120, 0.9f);
        keypoints[11] = (60, 100, 0.9f);
        keypoints[13] = (100, 100, 0.9f);
        keypoints[15] = (100, 140, 0.9f);
        keypoints[12] = (160, 100, 0.9f);
        keypoints[14] = (120, 100, 0.9f);
        keypoints[16] = (120, 140, 0.9f);

        var (move, caption) = Catalog.ReadPose(keypoints);

        Assert.Equal("squat", move);
        Assert.Contains("90°", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Softmax_LeavesProbabilitiesAlone()
    {
        Assert.Equal([0.2f, 0.8f], ScoreMath.AsProbabilities([0.2f, 0.8f]));
        Assert.Equal(1f, ScoreMath.AsProbabilities([2f, -1f, 0.5f]).Sum(), 0.0001f);
    }
}
