using DepthAI.Wizard.Projects;

namespace DepthAI.Wizard.Tests;

public class TemplateCatalogTests
{
    [Fact]
    public void All_ExposesEveryProjectKind()
    {
        Assert.Contains(TemplateCatalog.All, t => t.Kind == ProjectKind.Console);
        Assert.Contains(TemplateCatalog.All, t => t.Kind == ProjectKind.Desktop);
        Assert.Contains(TemplateCatalog.All, t => t.Kind == ProjectKind.Web);
    }

    [Fact]
    public void All_HaveUniqueIds()
    {
        var ids = TemplateCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_ProduceExactlyOneProjectFile()
        => Assert.All(TemplateCatalog.All, template =>
            Assert.Single(template.Files.Where(f => f.RelativePath.EndsWith(".csproj", StringComparison.Ordinal))));

    [Fact]
    public void All_ReferenceTheSdkThroughThePlaceholder()
    {
        // Placeholder-lah yang membuat scaffolder bisa memilih antara PackageReference
        // dan ProjectReference; template yang melewatinya akan menghasilkan proyek
        // yang tidak bisa di-build dari dalam repo.
        Assert.All(TemplateCatalog.All, template =>
        {
            var project = template.Files.Single(f => f.RelativePath.EndsWith(".csproj", StringComparison.Ordinal));
            Assert.Contains("<!--SDK_REFERENCE-->", project.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void All_HaveBilingualMetadata()
        => Assert.All(TemplateCatalog.All, template =>
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Title));
            Assert.False(string.IsNullOrWhiteSpace(template.TitleEnglish));
            Assert.False(string.IsNullOrWhiteSpace(template.Description));
            Assert.False(string.IsNullOrWhiteSpace(template.DescriptionEnglish));
        });

    [Fact]
    public void Get_ThrowsWithAvailableIdsForUnknownTemplate()
    {
        var exception = Assert.Throws<KeyNotFoundException>(() => TemplateCatalog.Get("tidak-ada"));
        Assert.Contains("blank-console", exception.Message);
    }
}

public class ProjectScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "depthai-scaffold-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Vision")]
    [InlineData("my-vision-app")]
    [InlineData("App_2024")]
    public void ValidateName_AcceptsReasonableNames(string name)
        => ProjectScaffolder.ValidateName(name);

    [Theory]
    [InlineData("")]
    [InlineData("2fast")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void ValidateName_RejectsUnusableNames(string name)
        => Assert.ThrowsAny<ArgumentException>(() => ProjectScaffolder.ValidateName(name));

    [Theory]
    [InlineData("my-vision-app", "MyVisionApp")]
    [InlineData("Vision", "Vision")]
    [InlineData("depth_viewer", "DepthViewer")]
    public void ToNamespace_ProducesValidIdentifier(string name, string expected)
        => Assert.Equal(expected, ProjectScaffolder.ToNamespace(name));

    [Fact]
    public async Task CreateAsync_WritesFilesAndSubstitutesTokens()
    {
        var result = await ProjectScaffolder.CreateAsync(new ScaffoldOptions
        {
            ProjectName = "MyVision",
            ParentDirectory = _root,
            Template = TemplateCatalog.Get("blank-console"),
        });

        Assert.True(File.Exists(result.ProjectFile));

        var program = await File.ReadAllTextAsync(Path.Combine(result.ProjectDirectory, "Program.cs"));
        Assert.DoesNotContain("{{ProjectName}}", program, StringComparison.Ordinal);
        Assert.DoesNotContain("{{ProjectNamespace}}", program, StringComparison.Ordinal);

        var project = await File.ReadAllTextAsync(result.ProjectFile);
        Assert.Contains("""<PackageReference Include="DepthAI.Net" Version="0.1.0" />""", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--SDK_REFERENCE-->", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_UsesProjectReferenceWhenRunningInsideTheSdkRepo()
    {
        var repositoryRoot = ProjectScaffolder.FindSdkRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repositoryRoot);

        var result = await ProjectScaffolder.CreateAsync(new ScaffoldOptions
        {
            ProjectName = "LocalRef",
            ParentDirectory = _root,
            Template = TemplateCatalog.Get("blank-console"),
            SdkReference = SdkReferenceMode.Project,
            SdkRepositoryRoot = repositoryRoot,
        });

        var project = await File.ReadAllTextAsync(result.ProjectFile);

        Assert.Contains("<ProjectReference", project, StringComparison.Ordinal);
        Assert.Contains("DepthAI.Net.Core.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("""<PackageReference Include="DepthAI.Net" """, project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ReplacesImagingPackageReferencesInProjectMode()
    {
        var repositoryRoot = ProjectScaffolder.FindSdkRepositoryRoot(AppContext.BaseDirectory);

        var result = await ProjectScaffolder.CreateAsync(new ScaffoldOptions
        {
            ProjectName = "ImagingRef",
            ParentDirectory = _root,
            Template = TemplateCatalog.Get("rgbd-recorder"),
            SdkReference = SdkReferenceMode.Project,
            SdkRepositoryRoot = repositoryRoot,
        });

        var project = await File.ReadAllTextAsync(result.ProjectFile);

        Assert.Contains("DepthAI.Net.Imaging.ImageSharp.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("""Include="DepthAI.Net.Imaging.ImageSharp" Version""", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RefusesToOverwriteNonEmptyDirectory()
    {
        var target = Path.Combine(_root, "Taken");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "berkas.txt"), "penting");

        await Assert.ThrowsAsync<IOException>(() => ProjectScaffolder.CreateAsync(new ScaffoldOptions
        {
            ProjectName = "Taken",
            ParentDirectory = _root,
            Template = TemplateCatalog.Get("blank-console"),
        }));
    }

    [Fact]
    public async Task CreateAsync_ProducesFilesForEveryTemplate()
    {
        // Menjaga agar template baru tidak diam-diam menghasilkan proyek kosong.
        foreach (var template in TemplateCatalog.All)
        {
            var result = await ProjectScaffolder.CreateAsync(new ScaffoldOptions
            {
                ProjectName = "T" + template.Id.Replace("-", string.Empty, StringComparison.Ordinal),
                ParentDirectory = _root,
                Template = template,
            });

            Assert.Equal(template.Files.Count, result.CreatedFiles.Count);
            Assert.All(result.CreatedFiles,
                file => Assert.True(File.Exists(Path.Combine(result.ProjectDirectory, file))));
        }
    }
}
