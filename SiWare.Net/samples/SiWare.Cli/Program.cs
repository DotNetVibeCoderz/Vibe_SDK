using System.Text.Json;
using SiWare.Sdk;

Console.OutputEncoding = System.Text.Encoding.UTF8; // R², ×, and Indonesian AI answers on Windows consoles
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var command = args.FirstOrDefault() ?? "help";
    string? Option(string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }
    bool Flag(string name) => args.Contains(name);
    if (command == "help")
    {
        Console.WriteLine("SiWare.NET | Gravicode Studios dipimpin oleh Kang Fadhil\n" +
            "  demo [--output directory]     Acquire dark/reference/sample and export absorbance\n" +
            "  scan [--output spectrum.json] [--vendor-config config.json]\n" +
            "  discover --vendor-config config.json\n" +
            SeriesCommand.Help + "\n" +
            "  process --input spectrum.json --operation snv|smooth [--output result.json]\n" +
            "  info                         Report .NET and Rust availability\n" +
            AnalysisCommands.Help + "\n" +
            "Default acquisition uses explicitly labelled simulation. Ctrl+C cancels.");
        return 0;
    }
    if (command == "info") { Console.WriteLine($".NET {Environment.Version}; Rust ABI v1 available: {NativeDsp.IsAvailable}"); return 0; }
    if (command == "process")
    {
        var input = SpectrumFile.FromJson(await File.ReadAllTextAsync(Option("--input") ?? throw new ArgumentException("--input is required."), cancellation.Token));
        var result = Option("--operation") switch
        {
            "snv" => SpectralProcessing.StandardNormalVariate(input),
            "smooth" => SpectralProcessing.Smooth(input),
            _ => throw new ArgumentException("--operation must be snv or smooth.")
        };
        await Save(result, Option("--output") ?? "processed.json", cancellation.Token); return 0;
    }
    var analysisResult = await AnalysisCommands.RunAsync(command, Option, Flag, cancellation.Token);
    if (analysisResult >= 0) return analysisResult;
    if (command is not ("demo" or "scan" or "discover" or "series")) throw new ArgumentException("Unknown command. Use help.");
    var vendor = Option("--vendor-config");
    if (command == "demo" && vendor != null) throw new ArgumentException("Demo is simulation-only. Use scan for hardware.");
    await using ISpectrometer device = vendor == null ? new SimulatedSpectrometer() : new VendorSpectrometer(
        JsonSerializer.Deserialize<VendorBridgeOptions>(await File.ReadAllTextAsync(vendor, cancellation.Token), SpectrumFile.JsonOptions)
        ?? throw new ArgumentException("Invalid vendor configuration."));
    await device.ConnectAsync(cancellation.Token);
    Console.WriteLine($"{device.Device.Name} | {(device.Device.IsSimulated ? "SIMULATED DATA" : "VENDOR DEVICE — hardware validation required")}");
    if (command == "discover") { Console.WriteLine(device.Device.Id); return 0; }
    if (command == "series") return await SeriesCommand.RunAsync(device, Option, cancellation.Token);
    if (command == "scan")
    {
        var spectrum = await device.AcquireAsync(MeasurementKind.Sample, new(), cancellation.Token);
        await Save(spectrum, Option("--output") ?? "spectrum.json", cancellation.Token); return 0;
    }
    var directory = Path.GetFullPath(Option("--output") ?? "artifacts/demo");
    Directory.CreateDirectory(directory);
    var options = new ScanOptions(Averages: 3);
    var dark = await device.AcquireAsync(MeasurementKind.Dark, options, cancellation.Token);
    var reference = await device.AcquireAsync(MeasurementKind.Reference, options, cancellation.Token);
    var sample = await device.AcquireAsync(MeasurementKind.Sample, options, cancellation.Token);
    var absorbance = SpectralProcessing.Absorbance(sample, dark, reference);
    foreach (var spectrum in new[] { dark, reference, sample, absorbance })
        await Save(spectrum, Path.Combine(directory, spectrum.Kind.ToString().ToLowerInvariant() + ".json"), cancellation.Token);
    await File.WriteAllTextAsync(Path.Combine(directory, "absorbance.csv"), SpectrumFile.ToCsv(absorbance), cancellation.Token);
    Console.WriteLine($"Exported {absorbance.Count} points to {directory}"); return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Operation cancelled or timed out."); return 2; }
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or JsonException
    or FormatException or ArithmeticException or HttpRequestException)
{ Console.Error.WriteLine(ex.Message); return 1; }

static async Task Save(Spectrum spectrum, string path, CancellationToken token)
{
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, SpectrumFile.ToJson(spectrum), token);
    Console.WriteLine($"Saved {full}");
}
