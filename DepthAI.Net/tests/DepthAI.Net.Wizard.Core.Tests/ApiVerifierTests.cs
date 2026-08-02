using DepthAI.Wizard.Ai.Plugins;

namespace DepthAI.Wizard.Tests;

/// <summary>
/// Penjaga terhadap kode yang tidak bisa dikompilasi dari asisten.
/// </summary>
/// <remarks>
/// Dua kasus di bawah diambil apa adanya dari keluaran gpt-4o yang sesungguhnya.
/// Keduanya dulu lolos: versi pertama pemeriksa ini memakai regex, jadi hanya menangkap
/// bentuk "tipe DepthAI yang dikenal titik anggota" — sementara <c>using DepthAI.Oak</c>
/// dan <c>Device.CreateAsync</c> tidak berbentuk begitu.
/// </remarks>
public class CSharpApiVerifierTests
{
    [Fact]
    public void Verify_CatchesHallucinatedStaticMethod()
    {
        const string Code = """
            using DepthAI;
            using DepthAI.Pipelines;

            var pipeline = Pipeline.FromJsonFile("pipeline.json");
            """;

        var problem = Assert.Single(CSharpApiVerifier.Verify(Code));

        Assert.Equal("CS0117", problem.Id);
        Assert.Contains("FromJsonFile", problem.Message, StringComparison.Ordinal);
        Assert.Equal(4, problem.Line);
    }

    [Fact]
    public void Verify_CatchesEveryErrorInTheProjectThatFailedToBuild()
    {
        // Berkas ini persis yang dihasilkan asisten untuk proyek 'VisiSaya' dan gagal
        // pada dotnet build dengan empat galat. Semuanya harus tertangkap di sini.
        const string Code = """
            using System;
            using System.IO;
            using System.Reactive.Linq;
            using System.Threading.Tasks;
            using DepthAI;
            using DepthAI.Extensions;
            using DepthAI.Oak;
            using DepthAI.Inference;

            class Program
            {
                static async Task Main(string[] args)
                {
                    var device = await Device.CreateAsync("pipeline.json");
                }
            }
            """;

        var problems = CSharpApiVerifier.Verify(Code);

        Assert.Contains(problems, p => p.Message.Contains("'Reactive'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("'Extensions'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("'Oak'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("'Device'", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_CatchesMissingUsing()
    {
        const string Code = """
            using DepthAI;

            var pipeline = Pipeline.Create();
            """;

        var problem = Assert.Single(CSharpApiVerifier.Verify(Code));
        Assert.Contains("Pipeline", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_AcceptsCorrectCode()
    {
        const string Code = """
            using DepthAI;
            using DepthAI.Pipelines;
            using DepthAI.Streaming;

            await using var device = await DepthAiDevice.OpenAsync();
            var pipeline = await Pipeline.LoadFromFileAsync("pipeline.json");
            await device.StartAsync(pipeline);

            using var subscription = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
            {
                var distance = frame.GetDistanceMeters(frame.Width / 2, frame.Height / 2);
                Console.WriteLine(distance);
            });

            Console.ReadLine();
            await device.StopAsync();
            """;

        Assert.Empty(CSharpApiVerifier.Verify(Code));
    }

    [Fact]
    public void Verify_AssumesImplicitUsingsLikeTheProjectTemplates()
    {
        // Template mengaktifkan ImplicitUsings, jadi kode yang memakai Console dan Task
        // tanpa 'using System;' tetap sah dan tidak boleh ditolak.
        const string Code = """
            using DepthAI;

            await using var device = await DepthAiDevice.OpenAsync();
            Console.WriteLine(device.Info.Name);
            await Task.Delay(10);
            """;

        Assert.Empty(CSharpApiVerifier.Verify(Code));
    }

    [Fact]
    public void Verify_RecognisesExtensionMethods()
    {
        // Subscribe dan Throttle adalah metode ekstensi; memperlakukannya sebagai
        // tidak ada akan menolak kode yang benar.
        const string Code = """
            using DepthAI;
            using DepthAI.Streaming;

            await using var device = await DepthAiDevice.OpenAsync();
            using var s = device.GetStream<ImageFrame>("video")
                .Throttle(TimeSpan.FromMilliseconds(80))
                .Subscribe(frame => Console.WriteLine(frame.Width));
            """;

        Assert.Empty(CSharpApiVerifier.Verify(Code));
    }

    [Fact]
    public void Verify_SeesTypesDefinedInCompanionFiles()
    {
        const string Target = """
            var helper = new SceneHelper();
            Console.WriteLine(helper.Describe());
            """;

        const string Companion = """
            public sealed class SceneHelper
            {
                public string Describe() => "halo";
            }
            """;

        // Tanpa berkas pendamping, SceneHelper akan tampak seperti tipe yang tidak ada.
        Assert.NotEmpty(CSharpApiVerifier.Verify(Target));
        Assert.Empty(CSharpApiVerifier.Verify(Target, [Companion]));
    }

    [Fact]
    public void Verify_IgnoresProblemsThatBelongToCompanionFiles()
    {
        const string Target = "Console.WriteLine(\"ok\");";
        const string BrokenCompanion = "public class Rusak { void M() { Tidak.Ada(); } }";

        // Berkas pendamping ada untuk konteks, bukan untuk dinilai; galat di sana
        // tidak boleh membuat berkas yang sedang ditulis ikut ditolak.
        Assert.Empty(CSharpApiVerifier.Verify(Target, [BrokenCompanion]));
    }

    [Fact]
    public void Verify_ToleratesEmptyInput()
        => Assert.Empty(CSharpApiVerifier.Verify(string.Empty));

    [Fact]
    public void DescribeProblems_TellsTheModelWhatToDoNext()
    {
        var problems = CSharpApiVerifier.Verify("""
            using DepthAI.Pipelines;
            var p = Pipeline.FromJsonFile("x.json");
            """);

        var message = CSharpApiVerifier.DescribeProblems(problems);

        Assert.Contains("describe_sdk_api", message, StringComparison.Ordinal);
        Assert.Contains("ditolak", message, StringComparison.Ordinal);
        Assert.Contains("baris", message, StringComparison.Ordinal);
    }
}
