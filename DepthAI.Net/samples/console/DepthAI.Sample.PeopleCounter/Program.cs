using DepthAI;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

// Model placeholder: metadata saja, tanpa bobot. Cukup untuk mengembangkan
// aplikasi melawan backend simulasi. Ganti dengan berkas .blob sungguhan
// sebelum menjalankan pada hardware:
//   var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob");
var model = NeuralModel.CreatePlaceholder(
    ModelFamily.MobileNetSsd,
    labels: ["person", "bottle", "chair"],
    inputWidth: 300,
    inputHeight: 300,
    confidenceThreshold: 0.55f);

// Garis hitung di tengah frame, dalam koordinat ternormalisasi.
const float LineX = 0.5f;

var tracker = new CrossingTracker(LineX);

await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
    .AddObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);

await device.StartAsync(pipeline);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    var people = frame.Detections.Where(d => d.Label == "person").ToList();
    if (tracker.Update(people))
    {
        Console.WriteLine($"masuk: {tracker.In,4}   keluar: {tracker.Out,4}   di dalam: {tracker.Occupancy,4}");
    }
});

Console.WriteLine("Menghitung lintasan. Tekan Enter untuk berhenti.");
Console.ReadLine();

await device.StopAsync();
Console.WriteLine($"Total — masuk {tracker.In}, keluar {tracker.Out}.");

/// <summary>
/// Pelacak lintasan sederhana berbasis kedekatan posisi antar frame.
/// </summary>
/// <remarks>
/// Deteksi tidak membawa identitas, jadi objek dicocokkan antar frame lewat
/// jarak titik pusat. Cukup untuk adegan lalu lintas ringan; untuk kerumunan
/// padat pakailah pelacak ber-ID seperti SORT atau ByteTrack.
/// </remarks>
internal sealed class CrossingTracker(float lineX)
{
    private const float MatchRadius = 0.12f;

    private readonly List<(float X, float Y, int Age)> _tracks = [];

    public int In { get; private set; }

    public int Out { get; private set; }

    public int Occupancy => Math.Max(0, In - Out);

    /// <summary>Memperbarui pelacak; true bila ada lintasan baru terhitung.</summary>
    public bool Update(IReadOnlyList<Detection> people)
    {
        var counted = false;
        var unmatched = new List<(float X, float Y, int Age)>(_tracks);
        var next = new List<(float X, float Y, int Age)>();

        foreach (var person in people)
        {
            var x = person.Box.CenterX;
            var y = person.Box.CenterY;

            var bestIndex = -1;
            var bestDistance = MatchRadius;

            for (var i = 0; i < unmatched.Count; i++)
            {
                var distance = MathF.Sqrt(
                    MathF.Pow(unmatched[i].X - x, 2) + MathF.Pow(unmatched[i].Y - y, 2));

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                var previous = unmatched[bestIndex];
                unmatched.RemoveAt(bestIndex);

                // Lintasan dihitung saat titik pusat berpindah sisi garis.
                if (previous.X < lineX && x >= lineX)
                {
                    In++;
                    counted = true;
                }
                else if (previous.X >= lineX && x < lineX)
                {
                    Out++;
                    counted = true;
                }
            }

            next.Add((x, y, 0));
        }

        _tracks.Clear();
        _tracks.AddRange(next);

        // Jejak yang tidak tercocokkan dipertahankan sebentar supaya deteksi
        // yang berkedip satu-dua frame tidak dihitung sebagai orang baru.
        foreach (var stale in unmatched.Where(t => t.Age < 3))
        {
            _tracks.Add((stale.X, stale.Y, stale.Age + 1));
        }

        return counted;
    }
}