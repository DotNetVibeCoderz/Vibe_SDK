using System.ComponentModel;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepthAI.Cli.Commands;

/// <summary>Menampilkan metadata model tanpa menyentuh perangkat.</summary>
public sealed class ModelInfoCommand : AsyncCommand<ModelInfoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<FILE>")]
        [Description("Berkas model (.blob, .superblob, .onnx).")]
        public string File { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Berkas tidak ditemukan:[/] {settings.File}");
            return 1;
        }

        var model = await NeuralModel.LoadFromFileAsync(settings.File);

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("k");
        table.AddColumn("v");

        table.AddRow("Nama", model.Name);
        table.AddRow("Format", model.Format.ToString());
        table.AddRow("Ukuran", CliHelpers.FormatBytes(model.SizeBytes));
        table.AddRow("Keluarga", model.Metadata.Family.ToString());
        table.AddRow("Ukuran input", $"{model.Metadata.InputWidth}x{model.Metadata.InputHeight}");
        table.AddRow("Ambang keyakinan", model.Metadata.ConfidenceThreshold.ToString("F2"));
        table.AddRow("Ambang IoU", model.Metadata.IouThreshold.ToString("F2"));
        table.AddRow("Jumlah kelas", model.Metadata.Labels.Count.ToString());

        if (model.Metadata.Labels.Count > 0)
        {
            var preview = string.Join(", ", model.Metadata.Labels.Take(12));
            if (model.Metadata.Labels.Count > 12)
            {
                preview += $", … (+{model.Metadata.Labels.Count - 12})";
            }

            table.AddRow("Kelas", preview);
        }

        AnsiConsole.Write(table);

        if (model.Metadata.Family == ModelFamily.Raw)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Keluarga model tidak diketahui.[/] Keluarannya akan dipaparkan sebagai tensor mentah. "
                + "Sertakan berkas .json pendamping bergaya Luxonis agar hasilnya terurai otomatis.");
        }

        return 0;
    }
}

/// <summary>Mengunggah model ke perangkat dan memverifikasi bisa dijalankan.</summary>
public sealed class ModelUploadCommand : AsyncCommand<ModelUploadCommand.Settings>
{
    public sealed class Settings : DeviceSettings
    {
        [CommandArgument(0, "<FILE>")]
        [Description("Berkas model yang diunggah.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("--verify")]
        [Description("Jalankan pipeline deteksi singkat untuk memastikan model benar-benar berjalan.")]
        [DefaultValue(true)]
        public bool Verify { get; init; } = true;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Berkas tidak ditemukan:[/] {settings.File}");
            return 1;
        }

        var model = await NeuralModel.LoadFromFileAsync(settings.File);
        AnsiConsole.MarkupLineInterpolated(
            $"Model [cyan]{model.Name}[/] — {model.Format}, {CliHelpers.FormatBytes(model.SizeBytes)}, keluarga {model.Metadata.Family}");

        await using var device = await CliHelpers.OpenAsync(settings, cancellationToken);

        if (!settings.Verify)
        {
            // Unggahan sebenarnya terjadi saat pipeline di-start; tanpa verifikasi
            // tidak ada yang bisa dikerjakan selain melaporkan bahwa model terbaca.
            AnsiConsole.MarkupLine("[green]Model berhasil dibaca.[/] Lewati verifikasi (--verify false).");
            return 0;
        }

        var pipeline = PipelinePresets.ObjectDetection(model);
        var validation = pipeline.Validate(device.Capabilities);

        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {error}");
            }

            return 1;
        }

        var frames = 0;

        await AnsiConsole.Status().StartAsync("Mengunggah dan memverifikasi…", async ctx =>
        {
            await device.StartAsync(pipeline, cancellationToken);

            using var subscription = device.GetStream<DetectionFrame>("detections")
                .Subscribe(_ => Interlocked.Increment(ref frames));

            // Beberapa detik cukup untuk membuktikan model termuat dan mengeluarkan hasil.
            for (var i = 0; i < 12 && frames == 0 && !cancellationToken.IsCancellationRequested; i++)
            {
                ctx.Status($"Menunggu hasil inferensi pertama… ({i + 1}/12)");
                await Task.Delay(250, cancellationToken);
            }

            await device.StopAsync();
        });

        if (frames == 0)
        {
            AnsiConsole.MarkupLine("[red]Model diunggah tapi tidak menghasilkan deteksi apa pun.[/]");
            AnsiConsole.MarkupLine("[grey]Periksa ukuran input dan keluarga model pada berkas .json pendamping.[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Model terverifikasi.[/] Menerima {frames} frame hasil inferensi.");
        return 0;
    }
}
