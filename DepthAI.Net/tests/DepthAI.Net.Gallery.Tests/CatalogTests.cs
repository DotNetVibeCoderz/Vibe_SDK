using DepthAI.Gallery.Inference;
using DepthAI.Gallery.Showcase;

namespace DepthAI.Gallery.Tests;

/// <summary>
/// Menjaga katalog, gambar contoh, config model, dan rekaman tetap sinkron. Galeri tanpa
/// kamera hanya menampilkan rekaman, jadi entri yang hilang berarti layar kosong.
/// </summary>
public class CatalogTests
{
    private static string Samples => Path.Combine(AppContext.BaseDirectory, "Samples");

    private static string Configs => Path.Combine(AppContext.BaseDirectory, "ModelConfigs");

    public static TheoryData<string> UseCaseIds => [.. Catalog.All.Select(u => u.Id)];

    [Theory]
    [MemberData(nameof(UseCaseIds))]
    public void EverySampleExistsAndHasARecordingFromRealHardware(string id)
    {
        var useCase = Catalog.All.Single(u => u.Id == id);
        var recordings = Recordings.Load(Samples);

        foreach (var sample in useCase.Samples)
        {
            Assert.True(File.Exists(Path.Combine(Samples, sample)), $"{sample} tidak ada");
            var result = recordings.Find(id, sample);
            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.Headline));
            Assert.StartsWith("14442C10", result.Run!.DeviceId, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(UseCaseIds))]
    public void EveryModelHasABundledConfig(string id)
    {
        var useCase = Catalog.All.Single(u => u.Id == id);
        var zoo = new ModelZoo(new HttpClient(), Path.GetTempPath(), Configs);

        foreach (var model in useCase.Stages.Select(s => s.Model).Prepend(useCase.Primary))
        {
            var metadata = zoo.ReadMetadata(model);
            Assert.True(metadata.InputWidth > 0 && metadata.InputHeight > 0, $"{model} tanpa ukuran masukan");
        }
    }

    [Theory]
    [MemberData(nameof(UseCaseIds))]
    public void SnippetsUseHostInputForImagesAndTheCameraForLive(string id)
    {
        var useCase = Catalog.All.Single(u => u.Id == id);
        var zoo = new ModelZoo(new HttpClient(), Path.Combine(Path.GetTempPath(), "gallery-tests-empty"), Configs);

        var image = Snippets.Build(useCase, zoo, live: false);
        var live = Snippets.Build(useCase, zoo, live: true);

        Assert.Contains("AddHostInput(\"image\")", image, StringComparison.Ordinal);
        Assert.Contains("InferAsync<", image, StringComparison.Ordinal);
        Assert.Contains("AddColorCamera(", live, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostInput(\"image\")", live, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryUseCaseBelongsToAListedSector()
        => Assert.All(Catalog.All, u => Assert.All(u.Sectors, s => Assert.Contains(s, Catalog.Sectors)));
}
