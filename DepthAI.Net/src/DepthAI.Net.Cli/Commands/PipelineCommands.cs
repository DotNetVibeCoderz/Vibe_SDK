using System.ComponentModel;
using System.Diagnostics;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepthAI.Cli.Commands;

/// <summary>Membuat berkas pipeline JSON dari preset bawaan.</summary>
public sealed class PipelineNewCommand : AsyncCommand<PipelineNewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PRESET]")]
        [Description("Nama preset. Jalankan tanpa argumen untuk melihat daftarnya.")]
        public string? Preset { get; init; }

        [CommandOption("-o|--output <FILE>")]
        [Description("Path berkas keluaran. Bawaan: <preset>.pipeline.json")]
        public string? Output { get; init; }

        [CommandOption("-m|--model <FILE>")]
        [Description("Model untuk preset berbasis deteksi.")]
        public string? Model { get; init; }

        [CommandOption("--fps <FPS>")]
        [Description("Laju frame kamera.")]
        [DefaultValue(30)]
        public int Fps { get; init; } = 30;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Preset))
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Preset");
            table.AddColumn("Deskripsi");

            foreach (var (name, description) in PipelinePresets.Available)
            {
                table.AddRow($"[cyan]{name}[/]", description);
            }

            AnsiConsole.Write(table);
            return 0;
        }

        NeuralModel? model = null;
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            model = await NeuralModel.LoadFromFileAsync(settings.Model);
        }

        var pipeline = PipelinePresets.Create(settings.Preset, model, settings.Fps);
        var output = settings.Output ?? $"{settings.Preset}.pipeline.json";

        await pipeline.SaveToFileAsync(output);

        AnsiConsole.MarkupLineInterpolated($"[green]Dibuat[/] {output}");
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]{pipeline.Nodes.Count} node, {pipeline.Links.Count} link, {pipeline.OutputStreams.Count} stream keluaran[/]");

        var validation = pipeline.Validate();
        foreach (var error in validation.Errors)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Perlu dilengkapi:[/] {error}");
        }

        return 0;
    }
}

/// <summary>Memeriksa berkas pipeline terhadap kemampuan perangkat.</summary>
public sealed class PipelineValidateCommand : AsyncCommand<PipelineValidateCommand.Settings>
{
    public sealed class Settings : DeviceSettings
    {
        [CommandArgument(0, "<FILE>")]
        [Description("Berkas pipeline JSON.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("-m|--model <FILE>")]
        [Description("Model yang dipasang ke node neural network sebelum validasi.")]
        public string? Model { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Berkas tidak ditemukan:[/] {settings.File}");
            return 1;
        }

        NeuralModel? model = null;
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            model = await NeuralModel.LoadFromFileAsync(settings.Model);
        }

        var pipeline = await Pipeline.LoadFromFileAsync(
            settings.File,
            new PipelineLoadOptions { ModelResolver = _ => model });

        await using var device = await CliHelpers.OpenAsync(settings, cancellationToken);

        var result = pipeline.Validate(device.Capabilities);

        var tree = new Tree($"[bold]{Path.GetFileName(settings.File)}[/]");
        var nodes = tree.AddNode($"Node ({pipeline.Nodes.Count})");
        foreach (var node in pipeline.Nodes)
        {
            nodes.AddNode($"[cyan]{node.NodeType}[/] {node.Name}");
        }

        var links = tree.AddNode($"Link ({pipeline.Links.Count})");
        foreach (var link in pipeline.Links)
        {
            links.AddNode($"{link.From} [grey]→[/] {link.To}");
        }

        var streams = tree.AddNode($"Stream keluaran ({pipeline.OutputStreams.Count})");
        foreach (var stream in pipeline.OutputStreams)
        {
            streams.AddNode($"[green]{stream.Name}[/] [grey]←[/] {stream.Source}");
        }

        AnsiConsole.Write(tree);

        foreach (var warning in result.Warnings)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Peringatan:[/] {warning}");
        }

        if (result.IsValid)
        {
            AnsiConsole.MarkupLine("[green]Pipeline valid.[/]");
            return 0;
        }

        foreach (var error in result.Errors)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {error}");
        }

        return 1;
    }
}

/// <summary>Menjalankan pipeline dan melaporkan throughput tiap stream.</summary>
public sealed class PipelineDeployCommand : AsyncCommand<PipelineDeployCommand.Settings>
{
    public sealed class Settings : DeviceSettings
    {
        [CommandArgument(0, "<FILE>")]
        [Description("Berkas pipeline JSON.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("-m|--model <FILE>")]
        [Description("Model yang dipasang ke node neural network.")]
        public string? Model { get; init; }

        [CommandOption("--duration <SECONDS>")]
        [Description("Berapa lama berjalan. 0 berarti sampai Ctrl+C.")]
        [DefaultValue(10)]
        public int DurationSeconds { get; init; } = 10;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Berkas tidak ditemukan:[/] {settings.File}");
            return 1;
        }

        NeuralModel? model = null;
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            model = await NeuralModel.LoadFromFileAsync(settings.Model);
        }

        var pipeline = await Pipeline.LoadFromFileAsync(
            settings.File,
            new PipelineLoadOptions { ModelResolver = _ => model });

        await using var device = await CliHelpers.OpenAsync(settings, cancellationToken);

        var counters = pipeline.OutputStreams.ToDictionary(
            s => s.Name,
            _ => new StreamCounter(),
            StringComparer.OrdinalIgnoreCase);

        await device.StartAsync(pipeline, cancellationToken);

        var subscriptions = new List<IDisposable>();
        foreach (var stream in pipeline.OutputStreams)
        {
            var counter = counters[stream.Name];
            subscriptions.Add(device.GetStream(stream.Name).Subscribe(frame => counter.Record(frame)));
        }

        var clock = Stopwatch.StartNew();
        var deadline = settings.DurationSeconds > 0
            ? TimeSpan.FromSeconds(settings.DurationSeconds)
            : Timeout.InfiniteTimeSpan;

        await AnsiConsole.Live(BuildTable(counters, clock.Elapsed))
            .StartAsync(async ctx =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested
                        && (deadline == Timeout.InfiniteTimeSpan || clock.Elapsed < deadline))
                    {
                        await Task.Delay(250, cancellationToken);
                        ctx.UpdateTarget(BuildTable(counters, clock.Elapsed));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C: berhenti rapi, bukan error.
                }
            });

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        await device.StopAsync();

        AnsiConsole.MarkupLineInterpolated($"[grey]Selesai setelah {clock.Elapsed.TotalSeconds:F1} detik.[/]");
        return 0;
    }

    private static Table BuildTable(Dictionary<string, StreamCounter> counters, TimeSpan elapsed)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.Title = new TableTitle($"Berjalan {elapsed.TotalSeconds:F1}s — Ctrl+C untuk berhenti");
        table.AddColumn("Stream");
        table.AddColumn("Frame");
        table.AddColumn("FPS");
        table.AddColumn("Detail terakhir");

        foreach (var (name, counter) in counters)
        {
            var seconds = Math.Max(0.001, elapsed.TotalSeconds);
            table.AddRow(
                $"[cyan]{name}[/]",
                counter.Count.ToString(),
                $"{counter.Count / seconds:F1}",
                counter.LastDescription ?? "[grey]menunggu…[/]");
        }

        return table;
    }

    /// <summary>Menghitung frame per stream dan mencatat deskripsi frame terakhir.</summary>
    private sealed class StreamCounter
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public string? LastDescription { get; private set; }

        public void Record(Frame frame)
        {
            Interlocked.Increment(ref _count);

            LastDescription = frame switch
            {
                ImageFrame image => $"{image.Width}x{image.Height} {image.Format}",
                DepthFrame depth => $"{depth.Width}x{depth.Height} depth",
                DetectionFrame detection => detection.Best is { } best
                    ? $"{detection.Count} objek, teratas: {best.Label} {best.Confidence:P0}"
                    : "0 objek",
                ClassificationFrame classification => classification.Top?.ToString() ?? "-",
                SegmentationFrame segmentation => $"mask {segmentation.Width}x{segmentation.Height}",
                _ => frame.GetType().Name,
            };
        }
    }
}
