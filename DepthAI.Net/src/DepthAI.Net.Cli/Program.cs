using DepthAI.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("depthai-dotnet-cli");
    config.SetApplicationVersion(DepthAI.DepthAi.Version);
    config.UseStrictParsing();

    config.AddCommand<InfoCommand>("info")
        .WithDescription("Menampilkan versi SDK dan status runtime native.")
        .WithExample("info");

    config.AddBranch("devices", devices =>
    {
        devices.SetDescription("Menemukan dan memeriksa perangkat OAK.");

        devices.AddCommand<DeviceListCommand>("list")
            .WithDescription("Menampilkan semua perangkat yang terhubung.")
            .WithExample("devices", "list")
            .WithExample("devices", "list", "--json");

        devices.AddCommand<DeviceInfoCommand>("info")
            .WithDescription("Menampilkan detail dan telemetri satu perangkat.")
            .WithExample("devices", "info", "--device", "14442C10D1");

        devices.AddCommand<DeviceWatchCommand>("watch")
            .WithDescription("Memantau perangkat dicolok dan dicabut sampai Ctrl+C.")
            .WithExample("devices", "watch");
    });

    config.AddBranch("pipeline", pipeline =>
    {
        pipeline.SetDescription("Membuat, memeriksa, dan menjalankan pipeline.");

        pipeline.AddCommand<PipelineNewCommand>("new")
            .WithDescription("Membuat berkas pipeline JSON dari preset.")
            .WithExample("pipeline", "new", "object-detection", "-o", "my.pipeline.json");

        pipeline.AddCommand<PipelineValidateCommand>("validate")
            .WithDescription("Memeriksa berkas pipeline terhadap kemampuan perangkat.")
            .WithExample("pipeline", "validate", "my.pipeline.json");

        pipeline.AddCommand<PipelineDeployCommand>("deploy")
            .WithDescription("Menjalankan pipeline pada perangkat dan melaporkan throughput stream.")
            .WithExample("pipeline", "deploy", "my.pipeline.json", "--duration", "10");
    });

    config.AddBranch("model", model =>
    {
        model.SetDescription("Memeriksa dan mengunggah model neural.");

        model.AddCommand<ModelInfoCommand>("info")
            .WithDescription("Menampilkan metadata model.")
            .WithExample("model", "info", "yolov8n.blob");

        model.AddCommand<ModelUploadCommand>("upload")
            .WithDescription("Mengunggah model ke perangkat dan memverifikasi bisa dimuat.")
            .WithExample("model", "upload", "yolov8n.blob");
    });

    config.AddCommand<CaptureCommand>("capture")
        .WithDescription("Menangkap frame RGB dan depth ke berkas.")
        .WithExample("capture", "-o", "./out", "--frames", "30", "--streams", "rgb,depth");
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
    return 1;
}
