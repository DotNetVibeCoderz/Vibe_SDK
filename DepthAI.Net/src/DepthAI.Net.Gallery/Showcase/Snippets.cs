using DepthAI.Gallery.Inference;

namespace DepthAI.Gallery.Showcase;

/// <summary>
/// Menyusun kode contoh yang bisa disalin untuk tiap use case. Kerangkanya sama untuk semua
/// (muat model, susun pipeline, kirim gambar atau buka kamera); bagian dekodenya khas.
/// </summary>
public static class Snippets
{
    public static string Build(UseCase useCase, ModelZoo zoo, bool live)
    {
        var metadata = zoo.ReadMetadata(useCase.Primary);
        var (width, height) = (metadata.InputWidth, metadata.InputHeight);
        var archive = zoo.TryGetCached(useCase.Primary) is { } path ? Path.GetFileName(path) : $"{useCase.Primary.Slug}.rvc2.tar.xz";

        var (resultType, nodeCall) = useCase.Snippet switch
        {
            SnippetStyle.Detections => ("DetectionFrame", "AddDetectionNetwork(\"detector\", n => n.Model = model)"),
            SnippetStyle.Classification => ("ClassificationFrame", "AddNeuralNetwork(\"classifier\", n => n.Model = model)"),
            _ => ("NeuralTensorFrame", "AddNeuralNetwork(\"nn\", n => n.Model = model)"),
        };

        var header = $$"""
            // {{useCase.Title}} — model {{useCase.Primary}}
            using DepthAI;
            using DepthAI.Inference;
            using DepthAI.Pipelines;
            using DepthAI.Streaming;

            // Arsip NN dari Luxonis Model Zoo; config.json-nya disimpan di sebelahnya
            // sebagai berkas .tar.json supaya label dan ukuran masukan ikut terbaca.
            var model = await NeuralModel.LoadFromFileAsync("models/{{archive}}");

            var pipeline = Pipeline.Create();
            var nn = pipeline.{{nodeCall}};
            """;

        var body = Indent(useCase.SnippetBody.Trim(), live ? "    " : string.Empty);

        if (live)
        {
            return $$"""
                {{header}}
                var camera = pipeline.AddColorCamera("rgb", c =>
                {
                    c.PreviewWidth = {{width}};
                    c.PreviewHeight = {{height}};
                    c.Fps = {{useCase.LiveFps}};
                });
                camera.Preview.LinkTo(nn.Input);
                pipeline.AddOutputStream(nn.Out, "result");
                pipeline.AddOutputStream(nn.Passthrough, "frame");   // frame yang persis diinferensi
                {{StageLines(useCase)}}
                await using var device = await DepthAiDevice.OpenAsync();
                await device.StartAsync(pipeline);

                // Callback berjalan di thread perangkat: jangan menunggu di sini.
                using var langganan = device.GetStream<{{resultType}}>("result").Subscribe(result =>
                {
                {{body}}
                });

                Console.ReadLine();
                """;
        }

        return $$"""
            {{header}}
            var image = pipeline.AddHostInput("image");
            image.Out.LinkTo(nn.Input);
            pipeline.AddOutputStream(nn.Out, "result");
            {{StageLines(useCase)}}
            await using var device = await DepthAiDevice.OpenAsync();
            await device.StartAsync(pipeline);

            // Gambar BGR seukuran masukan model ({{width}}×{{height}}). Untuk foto berukuran lain,
            // perkecil dengan bingkai (letterbox) seperti ImageIo.ToModelInput di galeri.
            using var frame = ImageFrame.CopyFrom(bgr, {{width}}, {{height}}, PixelFormat.Bgr888);
            using var result = await device.InferAsync<{{resultType}}>("image", frame, "result");

            {{body}}
            """;
    }

    private static string StageLines(UseCase useCase)
    {
        if (useCase.Stages.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string> { string.Empty, "// Tahap kedua: potongan wajah dikirim dari host ke model lain di kamera yang sama." };
        foreach (var stage in useCase.Stages)
        {
            var name = stage.Key.Replace("-", string.Empty, StringComparison.Ordinal);
            lines.Add($"var {name} = pipeline.AddNeuralNetwork(\"{stage.Key}\", n => n.Model = await NeuralModel.LoadFromFileAsync(\"models/{stage.Model.Slug}.rvc2.tar.xz\"));");
            lines.Add($"pipeline.AddHostInput(\"{stage.Key}-in\").Out.LinkTo({name}.Input);");
            lines.Add($"pipeline.AddOutputStream({name}.Out, \"{stage.Key}\");");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Indent(string text, string prefix)
        => string.Join(Environment.NewLine, text.Split('\n').Select(l => prefix + l.TrimEnd('\r')));
}
