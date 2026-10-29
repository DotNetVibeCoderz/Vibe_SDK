using DepthAI.Gallery.Inference;

namespace DepthAI.Gallery.Showcase;

/// <summary>
/// Mode <c>--record</c>: menjalankan setiap use case pada gambar contohnya di kamera yang
/// tersambung, lalu menulis hasilnya ke <c>Samples/recorded.json</c> di sumber repo.
/// </summary>
internal static class Recorder
{
    /// <summary>
    /// Mode <c>--live-check [use-case…]</c>: menjalankan kamera live beberapa detik per use case
    /// dan mencetak laju serta hasilnya. Tidak menyimpan frame apa pun.
    /// </summary>
    public static async Task<int> LiveCheckAsync(IReadOnlyList<string> useCases)
    {
        using var http = GalleryPaths.CreateHttpClient();
        await using var engine = new GalleryEngine(new ModelZoo(http, ModelZoo.DefaultCacheDirectory, GalleryPaths.ModelConfigDirectory));
        if (!await engine.ConnectAsync())
        {
            Console.Error.WriteLine($"Kamera tidak bisa dipakai: {engine.ConnectionProblem}");
            return 1;
        }

        foreach (var useCase in Catalog.All.Where(u => useCases.Count == 0 || useCases.Contains(u.Id)))
        {
            var updates = 0;
            var last = string.Empty;
            await engine.StartLiveAsync(useCase, update =>
            {
                Interlocked.Increment(ref updates);
                last = $"{update.Frame.Width}x{update.Frame.Height} · {update.Result.Headline}";
                update.Frame.Dispose();
            });

            await Task.Delay(TimeSpan.FromSeconds(6));
            await engine.StopLiveAsync();
            Console.WriteLine($"{useCase.Id,-11} {updates / 6.0,5:0.0} hasil/detik  {last}");
        }

        return 0;
    }

    public static async Task<int> RunAsync(string? onlyUseCase)
    {
        var samples = GalleryPaths.SourceSamplesDirectory ?? GalleryPaths.SamplesDirectory;
        Console.WriteLine($"Merekam ke {samples}");

        using var http = GalleryPaths.CreateHttpClient();
        await using var engine = new GalleryEngine(new ModelZoo(http, ModelZoo.DefaultCacheDirectory, GalleryPaths.ModelConfigDirectory));
        if (!await engine.ConnectAsync())
        {
            Console.Error.WriteLine($"Kamera tidak bisa dipakai: {engine.ConnectionProblem}");
            return 1;
        }

        Console.WriteLine($"Kamera: {engine.DeviceName} {engine.DeviceId}");
        // Rekaman penuh mulai dari kosong supaya gambar yang dikeluarkan dari katalog ikut hilang.
        var recordings = onlyUseCase is null ? new Recordings() : Recordings.Load(samples);
        var progress = new Progress<string>(message => Console.WriteLine($"  … {message}"));

        foreach (var useCase in Catalog.All.Where(u => onlyUseCase is null || u.Id == onlyUseCase))
        {
            foreach (var sample in useCase.Samples)
            {
                using var image = ImageIo.Load(Path.Combine(samples, sample));
                var result = await engine.AnalyzeImageAsync(useCase, image, progress);
                recordings.Store(useCase.Id, sample, result);
                Console.WriteLine($"{useCase.Id,-11} {sample,-26} {result.Run!.RoundTripMilliseconds,7:0.0} ms  {result.Headline}");
                foreach (var box in result.Boxes.Take(12))
                {
                    Console.WriteLine($"    {box.Label,-16} {box.Score,5:P0} [{box.X0:0.000},{box.Y0:0.000} → {box.X1:0.000},{box.Y1:0.000}] {box.Caption}");
                }

                foreach (var score in result.Scores)
                {
                    Console.WriteLine($"    {score.Label,-24} {score.Value:P1}");
                }
            }
        }

        recordings.Save(samples);
        Console.WriteLine($"Tersimpan: {Path.Combine(samples, Recordings.FileName)}");
        return 0;
    }
}
