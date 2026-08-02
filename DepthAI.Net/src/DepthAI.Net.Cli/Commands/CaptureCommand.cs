using System.ComponentModel;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepthAI.Cli.Commands;

/// <summary>Menangkap frame ke berkas gambar.</summary>
public sealed class CaptureCommand : AsyncCommand<CaptureCommand.Settings>
{
    public sealed class Settings : DeviceSettings
    {
        [CommandOption("-o|--output <DIR>")]
        [Description("Direktori keluaran.")]
        [DefaultValue("./capture")]
        public string Output { get; init; } = "./capture";

        [CommandOption("--streams <LIST>")]
        [Description("Stream yang ditangkap, dipisah koma: rgb, depth, depth-raw.")]
        [DefaultValue("rgb,depth")]
        public string Streams { get; init; } = "rgb,depth";

        [CommandOption("--frames <COUNT>")]
        [Description("Jumlah frame yang disimpan per stream.")]
        [DefaultValue(10)]
        public int Frames { get; init; } = 10;

        [CommandOption("--every <N>")]
        [Description("Simpan hanya setiap frame ke-N; menjarangkan rekaman panjang.")]
        [DefaultValue(1)]
        public int Every { get; init; } = 1;

        [CommandOption("--fps <FPS>")]
        [Description("Laju frame kamera.")]
        [DefaultValue(30)]
        public int Fps { get; init; } = 30;

        [CommandOption("--colormap <MAP>")]
        [Description("Peta warna kedalaman: Turbo, Jet, atau Grayscale.")]
        [DefaultValue(DepthColorMap.Turbo)]
        public DepthColorMap ColorMap { get; init; } = DepthColorMap.Turbo;

        public override Spectre.Console.ValidationResult Validate()
        {
            var baseResult = base.Validate();
            if (!baseResult.Successful)
            {
                return baseResult;
            }

            return Frames <= 0
                ? Spectre.Console.ValidationResult.Error("--frames harus lebih besar dari 0.")
                : Spectre.Console.ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var requested = settings.Streams
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();

        var wantsDepth = requested.Contains("depth") || requested.Contains("depth-raw");
        var wantsRgb = requested.Contains("rgb");

        if (!wantsDepth && !wantsRgb)
        {
            AnsiConsole.MarkupLine("[red]Tidak ada stream yang dikenali.[/] Pilihan: rgb, depth, depth-raw.");
            return 1;
        }

        Directory.CreateDirectory(settings.Output);

        var pipeline = wantsDepth
            ? PipelinePresets.StereoDepth(settings.Fps)
            : PipelinePresets.RgbPreview(settings.Fps);

        await using var device = await CliHelpers.OpenAsync(settings, cancellationToken);

        pipeline.Validate(device.Capabilities).ThrowIfInvalid();
        await device.StartAsync(pipeline, cancellationToken);

        var saved = 0;
        var target = settings.Frames * ((wantsRgb ? 1 : 0) + (wantsDepth ? 1 : 0));

        // Callback stream RGB dan depth berjalan di thread berbeda, jadi antreannya
        // harus aman untuk penulisan bersamaan — List<Task> akan kehilangan entri.
        var tasks = new System.Collections.Concurrent.ConcurrentQueue<Task>();

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var progress = ctx.AddTask("Menangkap frame", maxValue: target);

                var rgbCount = 0;
                var depthCount = 0;
                var seen = 0;

                using var rgbSubscription = wantsRgb
                    ? device.GetStream<ImageFrame>("video").Subscribe(frame =>
                    {
                        if (Interlocked.Increment(ref seen) % settings.Every != 0 || rgbCount >= settings.Frames)
                        {
                            return;
                        }

                        var index = Interlocked.Increment(ref rgbCount);
                        // Frame di-clone karena penyimpanan berlangsung asinkron,
                        // sedangkan frame milik stream dibuang begitu callback selesai.
                        var copy = frame.Clone();
                        tasks.Enqueue(SaveRgbAsync(copy, settings.Output, index, progress));
                    })
                    : null;

                using var depthSubscription = wantsDepth
                    ? device.GetStream<DepthFrame>("depth").Subscribe(frame =>
                    {
                        if (depthCount >= settings.Frames)
                        {
                            return;
                        }

                        var index = Interlocked.Increment(ref depthCount);
                        var copy = frame.Clone();
                        tasks.Enqueue(SaveDepthAsync(
                            copy, settings.Output, index, settings.ColorMap, requested.Contains("depth-raw"), progress));
                    })
                    : null;

                while (!cancellationToken.IsCancellationRequested
                    && (rgbCount < settings.Frames || !wantsRgb)
                    && (depthCount < settings.Frames || !wantsDepth))
                {
                    await Task.Delay(50, cancellationToken);
                }

                await Task.WhenAll(tasks.ToArray());
                saved = (int)progress.Value;
            });

        await device.StopAsync();

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Tersimpan {saved} berkas[/] ke {Path.GetFullPath(settings.Output)}");

        return 0;
    }

    private static async Task SaveRgbAsync(ImageFrame frame, string directory, int index, ProgressTask progress)
    {
        try
        {
            await frame.SaveAsync(Path.Combine(directory, $"rgb_{index:D4}.png"));
            progress.Increment(1);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static async Task SaveDepthAsync(
        DepthFrame frame,
        string directory,
        int index,
        DepthColorMap colorMap,
        bool includeRaw,
        ProgressTask progress)
    {
        try
        {
            await frame.SaveAsync(Path.Combine(directory, $"depth_{index:D4}.png"), colorMap);

            if (includeRaw)
            {
                // Versi berwarna hanya untuk dilihat; PNG 16-bit menyimpan milimeter
                // sebenarnya supaya bisa dipakai analisis.
                await frame.SaveRawDepthAsync(Path.Combine(directory, $"depth_raw_{index:D4}.png"));
            }

            progress.Increment(1);
        }
        finally
        {
            frame.Dispose();
        }
    }
}
