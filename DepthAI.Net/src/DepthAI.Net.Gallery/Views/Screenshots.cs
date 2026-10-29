using Avalonia;
using Avalonia.Media.Imaging;
using DepthAI.Gallery.Showcase;

namespace DepthAI.Gallery.Views;

/// <summary>
/// Mode <c>--screenshot &lt;folder&gt;</c>: memotret jendela untuk dokumentasi. Setiap use case
/// dibuka seperti diklik pengguna, jadi bila kamera tersambung hasilnya datang dari kamera.
/// </summary>
/// <remarks>
/// <c>--shots traffic,ppe:code</c> membatasi use case yang dipotret; akhiran <c>:code</c>
/// memotret tab Kode. Mode live sengaja tidak dipotret: isinya adalah ruangan tempat kamera
/// berada, bukan sesuatu yang pantas masuk dokumentasi publik.
/// </remarks>
internal static class Screenshots
{
    /// <summary>Dirender 1,5× ukuran logis supaya teks tetap tajam di halaman dokumentasi.</summary>
    private const double Scale = 1.5;

    public static async Task CaptureAsync(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        var shots = Program.Options.ScreenshotUseCases.Count > 0
            ? Program.Options.ScreenshotUseCases
            : [.. Catalog.All.Select(u => u.Id)];

        foreach (var shot in shots)
        {
            var parts = shot.Split(':');
            var useCase = Catalog.All.FirstOrDefault(u => u.Id == parts[0]);
            if (useCase is null)
            {
                continue;
            }

            window.ShowCode(false);
            await window.SelectUseCaseAsync(useCase);
            window.ShowCode(parts.Length > 1 && parts[1] == "code");

            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (window.IsBusy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            // Anotasi memudar masuk selama 400 ms; tunggu sampai selesai dan tata letak stabil.
            await Task.Delay(900);

            var name = parts.Length > 1 ? $"gallery-{parts[0]}-{parts[1]}.jpg" : $"gallery-{parts[0]}.jpg";
            Save(window, Path.Combine(folder, name));
        }

        window.Close();
    }

    private static void Save(MainWindow window, string path)
    {
        var size = new PixelSize((int)(window.Bounds.Width * Scale), (int)(window.Bounds.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(window);

        // JPEG berkualitas tinggi: sepersepuluh ukuran PNG untuk tangkapan berisi foto.
        using var file = File.Create(path);
        bitmap.Save(file, new JpegBitmapEncoderOptions { Quality = 88 });
    }
}
