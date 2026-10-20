using System.Globalization;
using System.Text.Json;
using Gravicode.Science.GraviFrame;
using SiWare.Ai;
using SiWare.Analysis;
using SiWare.Sdk;
using SiWare.Visualization;

/// <summary>Chemometrics, plotting, and AI commands built on SiWare.Analysis, SiWare.Visualization, and SiWare.Ai.</summary>
internal static class AnalysisCommands
{
    public const string Help =
        "  dataset [--samples 60] [--points 257] [--output dir]   Simulated labelled dataset (JSON, CSV, overlay plot)\n" +
        "  analyze --input dataset.json [--components 3] [--output dir] [preprocessing]   PCA with plots and scores CSV\n" +
        "  calibrate --input dataset.json [--max-components 8] [--folds 5] [--output dir] [preprocessing]   PLS + cross-validation\n" +
        "  plot --input a.json[,b.json] [--output plot.png|svg|pdf]   Overlay spectra\n" +
        "  explain --input spectrum.json|dataset.json [--analysis pca|pls] [--language en|id] [--provider hf|openai|local] [--model id] [--base-url url]\n" +
        "  preprocessing: [--msc] [--sg-window 11] [--derivative 0|1|2] [--snv]\n" +
        "  explain keys: HF_TOKEN (hf router, gated local models), SIWARE_LLM_API_KEY (openai-compatible endpoint).";

    public static async Task<int> RunAsync(string command, Func<string, string?> option, Func<string, bool> flag, CancellationToken token)
    {
        switch (command)
        {
            case "dataset":
                {
                    var dataset = SimulatedSampleSet.Generate(Int(option("--samples"), 60), Int(option("--points"), 257));
                    var directory = OutputDirectory(option, "artifacts/dataset");
                    await File.WriteAllTextAsync(Path.Combine(directory, "dataset.json"), SpectralDatasetFile.ToJson(dataset), token);
                    dataset.ToDataFrame().WriteCsv(Path.Combine(directory, "dataset.csv"));
                    SpectrumCharts.Overlay(dataset.Spectra, "Simulated moisture series").Save(Path.Combine(directory, "overlay.png"));
                    Console.WriteLine($"SIMULATED DATA | {dataset.SampleCount} spectra × {dataset.FeatureCount} points, {dataset.ReferenceName} " +
                        $"{dataset.ReferenceValues!.Min():F1}–{dataset.ReferenceValues!.Max():F1}");
                    Console.WriteLine($"Saved dataset.json, dataset.csv, overlay.png to {directory}");
                    return 0;
                }
            case "analyze":
                {
                    var dataset = Preprocess(await LoadDataset(option, token), option, flag);
                    var pca = PcaResult.Fit(dataset, Int(option("--components"), 3));
                    var directory = OutputDirectory(option, "artifacts/analysis");
                    SpectrumCharts.Overlay(dataset.Spectra, "Preprocessed spectra").Save(Path.Combine(directory, "spectra.png"));
                    SpectrumCharts.PcaScores(pca, colorValues: dataset.ReferenceValues, colorLabel: dataset.ReferenceName).Save(Path.Combine(directory, "pca-scores.png"));
                    SpectrumCharts.PcaLoadings(pca).Save(Path.Combine(directory, "pca-loadings.png"));
                    var columns = new Dictionary<string, double[]>();
                    if (dataset.ReferenceValues != null) columns[dataset.ReferenceName] = [.. dataset.ReferenceValues];
                    for (var c = 0; c < pca.Components; c++) columns[$"PC{c + 1}"] = [.. pca.Scores.Select(row => row[c])];
                    DataFrame.FromColumns(columns).WriteCsv(Path.Combine(directory, "pca-scores.csv"));
                    Console.WriteLine(Provenance(dataset.IsSimulated) + $" | PCA of {dataset.SampleCount} spectra");
                    for (var c = 0; c < pca.Components; c++)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  PC{c + 1}: {pca.ExplainedVarianceRatio[c],7:P2} of variance"));
                    Console.WriteLine($"Saved spectra.png, pca-scores.png, pca-loadings.png, pca-scores.csv to {directory}");
                    return 0;
                }
            case "calibrate":
                {
                    var dataset = Preprocess(await LoadDataset(option, token), option, flag);
                    var calibration = CalibrationResult.Fit(dataset, Int(option("--max-components"), 8), Int(option("--folds"), 5));
                    var directory = OutputDirectory(option, "artifacts/calibration");
                    SpectrumCharts.PredictedVsMeasured(calibration).Save(Path.Combine(directory, "predicted-vs-measured.png"));
                    SpectrumCharts.ComponentSelection(calibration).Save(Path.Combine(directory, "rmsecv.png"));
                    await File.WriteAllTextAsync(Path.Combine(directory, "calibration.json"), JsonSerializer.Serialize(calibration, SpectrumFile.JsonOptions), token);
                    Console.WriteLine(Provenance(dataset.IsSimulated) + $" | PLS calibration of {calibration.Property}");
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  latent variables {calibration.Components} | RMSECV {calibration.RmseCv:G4} | R² (CV) {calibration.R2Cv:F4} | {calibration.Folds}-fold"));
                    Console.WriteLine($"Saved predicted-vs-measured.png, rmsecv.png, calibration.json to {directory}");
                    return 0;
                }
            case "plot":
                {
                    var inputs = (option("--input") ?? throw new ArgumentException("--input is required.")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var spectra = new List<Spectrum>();
                    foreach (var path in inputs) spectra.Add(SpectrumFile.FromJson(await File.ReadAllTextAsync(path, token)));
                    var output = Path.GetFullPath(option("--output") ?? "plot.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    SpectrumCharts.Overlay(spectra, "Spectra", [.. inputs.Select(Path.GetFileNameWithoutExtension).Select(n => n ?? "")]).Save(output);
                    Console.WriteLine($"Saved {output}");
                    return 0;
                }
            case "explain":
                {
                    var input = option("--input") ?? throw new ArgumentException("--input is required.");
                    var json = await File.ReadAllTextAsync(input, token);
                    string facts;
                    using (var document = JsonDocument.Parse(json))
                        facts = document.RootElement.TryGetProperty("spectra", out _)
                            ? DatasetFacts(Preprocess(SpectralDatasetFile.FromJson(json), option, flag), option("--analysis") ?? "pca")
                            : SpectrumFacts.Describe(SpectrumFile.FromJson(json));
                    var language = InterpretationPrompt.NormalizeLanguage(option("--language"));
                    Console.WriteLine("Facts sent to the model:\n" + facts);
                    var interpretation = option("--provider") switch
                    {
                        null or "hf" => await ExplainRemote(ChatCompletionsOptions.HuggingFaceRouter(option("--model")), facts, language, token),
                        "openai" => await ExplainRemote(OpenAiCompatible(option), facts, language, token),
                        "local" => await ExplainLocal(facts, language, option("--model"), token),
                        _ => throw new ArgumentException("--provider must be hf, openai, or local.")
                    };
                    Console.WriteLine($"── {interpretation.Provider} · {interpretation.Model} ──");
                    Console.WriteLine(interpretation.Text);
                    Console.WriteLine($"[{interpretation.Disclaimer}]");
                    return 0;
                }
            default:
                return -1;
        }
    }

    private static string DatasetFacts(SpectralDataset dataset, string analysis) => analysis switch
    {
        "pca" => SpectrumFacts.Describe(PcaResult.Fit(dataset, Math.Min(3, dataset.SampleCount))),
        "pls" => SpectrumFacts.Describe(CalibrationResult.Fit(dataset)),
        _ => throw new ArgumentException("--analysis must be pca or pls.")
    };

    private static async Task<Interpretation> ExplainRemote(ChatCompletionsOptions options, string facts, string language, CancellationToken token)
    {
        using var http = new HttpClient();
        return await new ChatCompletionsInterpreter(http, options).InterpretAsync(facts, language, token);
    }

    /// <summary>Any OpenAI-compatible endpoint; the key is read from SIWARE_LLM_API_KEY so it never appears in shell history.</summary>
    private static ChatCompletionsOptions OpenAiCompatible(Func<string, string?> option)
    {
        var baseUrl = option("--base-url") ?? throw new ArgumentException("--base-url is required for --provider openai (the URL before /chat/completions).");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("--base-url must be an absolute https URL.");
        var key = Environment.GetEnvironmentVariable("SIWARE_LLM_API_KEY") ?? throw new ArgumentException("Set SIWARE_LLM_API_KEY for --provider openai.");
        return new(uri, option("--model") ?? throw new ArgumentException("--model is required for --provider openai."), key, uri.Host);
    }

    private static async Task<Interpretation> ExplainLocal(string facts, string language, string? model, CancellationToken token)
    {
        Console.WriteLine($"Loading {model ?? LocalModelInterpreter.DefaultModel} locally (first run downloads it)…");
        using var local = await LocalModelInterpreter.LoadAsync(model ?? LocalModelInterpreter.DefaultModel, cancellationToken: token);
        return await local.InterpretAsync(facts, language, token);
    }

    private static async Task<SpectralDataset> LoadDataset(Func<string, string?> option, CancellationToken token) =>
        SpectralDatasetFile.FromJson(await File.ReadAllTextAsync(option("--input") ?? throw new ArgumentException("--input is required."), token));

    /// <summary>MSC, then Savitzky-Golay, then SNV — the usual NIR order.</summary>
    private static SpectralDataset Preprocess(SpectralDataset dataset, Func<string, string?> option, Func<string, bool> flag)
    {
        if (flag("--msc")) dataset = Preprocessing.MultiplicativeScatterCorrection(dataset);
        var derivative = Int(option("--derivative"), 0);
        if (option("--sg-window") != null || derivative > 0)
        {
            var window = Int(option("--sg-window"), 11);
            dataset = dataset.Select(s => Preprocessing.SavitzkyGolay(s, window, Math.Max(2, derivative), derivative));
        }
        if (flag("--snv")) dataset = dataset.Select(SpectralProcessing.StandardNormalVariate);
        return dataset;
    }

    private static string OutputDirectory(Func<string, string?> option, string fallback)
    {
        var directory = Path.GetFullPath(option("--output") ?? fallback);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Provenance(bool simulated) => simulated ? "SIMULATED DATA" : "MEASURED DATA";

    private static int Int(string? value, int fallback) =>
        value == null ? fallback
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : throw new ArgumentException($"'{value}' is not an integer.");
}
