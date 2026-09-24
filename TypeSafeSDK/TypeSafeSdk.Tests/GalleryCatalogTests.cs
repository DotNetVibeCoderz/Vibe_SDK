using TypeSafe.JevGallery;
using TypeSafeSdk;

namespace TypeSafeSdk.Tests;

/// <summary>Menjamin setiap specimen Jev Gallery benar-benar berjalan lewat SDK, bukan sekadar teks.</summary>
public sealed class GalleryCatalogTests
{
    public static TheoryData<string> Accessions() => [.. SpecimenCatalog.All.Select(s => s.Accession)];

    [Fact] public void Catalog_CoversEveryDomain()
    {
        Assert.Equal(Enum.GetValues<SpecimenDomain>().Length, SpecimenCatalog.Counts.Count);
        Assert.All(SpecimenCatalog.Counts.Values, count => Assert.True(count >= 3));
        Assert.Equal(SpecimenCatalog.All.Count, SpecimenCatalog.All.Select(s => s.Accession).Distinct().Count());
    }

    [Theory, MemberData(nameof(Accessions))]
    public async Task Specimen_RunsAndAnswersEveryQuestion(string accession)
    {
        var specimen = SpecimenCatalog.All.Single(s => s.Accession == accession);
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });

        var response = await client.SystemOneAsync(specimen.State(specimen.SampleInput), new Dictionary<string, object>(specimen.Questions));

        Assert.Equal(specimen.Questions.Count, response.Answers.Count);
        foreach (var name in specimen.Questions.Keys) Assert.True(response.Answers.ContainsKey(name), $"{accession} did not answer '{name}'.");
        Assert.All(response.ChoiceAnswers.Values, choice => Assert.InRange(choice.Confidence, 0, 1));
        Assert.All(response.Nouls.Values, noul => Assert.InRange(noul.Noul, 0, 1));
    }

    [Theory, MemberData(nameof(Accessions))]
    public void Specimen_CarriesSampleInputAndCode(string accession)
    {
        var specimen = SpecimenCatalog.All.Single(s => s.Accession == accession);
        Assert.False(string.IsNullOrWhiteSpace(specimen.SampleInput));
        Assert.False(string.IsNullOrWhiteSpace(specimen.Blurb));
        Assert.Contains("SystemOneAsync", specimen.Code);
    }

    /// <summary>Jawaban yang harus dihasilkan sample input tiap specimen di mode simulator.</summary>
    public static TheoryData<string, string, string> ExpectedChoices() => new()
    {
        { "G-01", "intent", "trade" },
        { "E-02", "subject", "algebra" },
        { "W-01", "category", "billing" },
        { "S-01", "method", "randomised-trial" },
        { "S-02", "behaviour", "feeding" },
        { "M-03", "state", "degrading" }
    };

    [Theory, MemberData(nameof(ExpectedChoices))]
    public async Task Specimen_DemoAnswerStaysCorrect(string accession, string question, string expected)
    {
        var specimen = SpecimenCatalog.All.Single(s => s.Accession == accession);
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var response = await client.SystemOneAsync(specimen.State(specimen.SampleInput), new Dictionary<string, object>(specimen.Questions));
        var answer = response.ChoiceAnswers[question];
        Assert.Equal(expected, answer.Choice);
        Assert.True(answer.Confidence > 0.5, $"{accession}/{question} answered {answer.Choice} with only {answer.Confidence:0.00} confidence.");
    }

    public static TheoryData<string, string> ExpectedTrueNouls() => new()
    {
        { "G-02", "toxic" }, { "E-03", "too_hard" }, { "W-01", "urgent" },
        { "W-02", "has_commitment" }, { "W-02", "owner_named" }, { "S-01", "has_control" }
    };

    [Theory, MemberData(nameof(ExpectedTrueNouls))]
    public async Task Specimen_NoulReadsTrueOnItsDemoInput(string accession, string question)
    {
        var specimen = SpecimenCatalog.All.Single(s => s.Accession == accession);
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var response = await client.SystemOneAsync(specimen.State(specimen.SampleInput), new Dictionary<string, object>(specimen.Questions));
        Assert.True(response.Nouls[question].Noul > 0.6, $"{accession}/{question} returned {response.Nouls[question].Noul:0.00}.");
    }

    [Fact]
    public async Task Noul_DoesNotReadTrueOnAnObviouslyNegativeInput()
    {
        var specimen = SpecimenCatalog.All.Single(s => s.Accession == "G-02");
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var response = await client.SystemOneAsync(specimen.State("nice play everyone, good game"), new Dictionary<string, object>(specimen.Questions));
        Assert.True(response.Nouls["toxic"].Noul <= 0.6, $"Simulator called a friendly message toxic at {response.Nouls["toxic"].Noul:0.00}.");
    }

    [Fact] public async Task ChoiceSpecimens_ReturnALabelFromTheirOwnCriteria()
    {
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        foreach (var specimen in SpecimenCatalog.All)
        {
            var response = await client.SystemOneAsync(specimen.State(specimen.SampleInput), new Dictionary<string, object>(specimen.Questions));
            foreach (var (name, question) in specimen.Questions)
            {
                if (question is not ChoiceSchema schema) continue;
                Assert.Contains(response.Choices[name].Choice, schema.Values);
            }
        }
    }
}
