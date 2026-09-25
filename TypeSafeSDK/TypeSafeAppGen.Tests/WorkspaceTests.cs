using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Tests;

public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appgen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public sealed class ProjectWorkspaceTests
{
    [Fact]
    public void Resolve_KeepsPathsInsideTheProject()
    {
        using var temp = new TempFolder();
        var workspace = new ProjectWorkspace(temp.Path);

        Assert.Equal(Path.Combine(workspace.Root, "src", "App.cs"), workspace.Resolve("src/App.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => workspace.Resolve("../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => workspace.Resolve(Path.GetTempPath()));
    }

    [Fact]
    public void Resolve_RejectsSiblingFolderWithSamePrefix()
    {
        using var temp = new TempFolder();
        var workspace = new ProjectWorkspace(temp.Path);

        // "C:\x\proj" tidak boleh mengizinkan "C:\x\proj-evil".
        Assert.Throws<UnauthorizedAccessException>(() => workspace.Resolve(workspace.Root + "-evil" + Path.DirectorySeparatorChar + "a.cs"));
    }

    [Fact]
    public void EnumerateFiles_SkipsBuildOutputAndGit()
    {
        using var temp = new TempFolder();
        temp.Write("Program.cs", "");
        temp.Write("bin/Debug/app.dll", "");
        temp.Write("obj/project.assets.json", "");
        temp.Write(".git/HEAD", "");
        temp.Write("Views/MainWindow.cs", "");

        var files = new ProjectWorkspace(temp.Path).EnumerateFiles().Select(Path.GetFileName).ToList();

        Assert.Equal(["Program.cs", "MainWindow.cs"], files);
    }

    [Fact]
    public void FindBuildTarget_PrefersSolutionThenShallowestProject()
    {
        using var temp = new TempFolder();
        temp.Write("src/Deep/Deep.csproj", "<Project />");
        temp.Write("App/App.csproj", "<Project />");
        var workspace = new ProjectWorkspace(temp.Path);
        Assert.EndsWith("App.csproj", workspace.FindBuildTarget());

        temp.Write("All.slnx", "<Solution />");
        Assert.EndsWith("All.slnx", workspace.FindBuildTarget());
    }

    [Fact]
    public void FindRunnableProject_SkipsClassLibraries()
    {
        using var temp = new TempFolder();
        temp.Write("Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        temp.Write("Web/Deeper/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        Assert.EndsWith("Web.csproj", new ProjectWorkspace(temp.Path).FindRunnableProject());
    }

    [Fact]
    public async Task WriteAsync_CreatesFoldersAndRaisesFileChanged()
    {
        using var temp = new TempFolder();
        var workspace = new ProjectWorkspace(temp.Path);
        string? changed = null;
        workspace.FileChanged += path => changed = path;

        var full = await workspace.WriteAsync("Views/Home.razor", "<h1>Hi</h1>");

        Assert.Equal("<h1>Hi</h1>", await File.ReadAllTextAsync(full));
        Assert.Equal(full, changed);
    }

    [Fact]
    public void Delete_RefusesProjectRoot()
    {
        using var temp = new TempFolder();
        var workspace = new ProjectWorkspace(temp.Path);
        Assert.Throws<UnauthorizedAccessException>(() => workspace.Delete("."));
    }
}

public sealed class BuildDiagnosticsTests
{
    [Fact]
    public void ParseLine_ReadsCompilerError()
    {
        var d = BuildDiagnostics.ParseLine(@"C:\app\Views\Main.cs(15,21): error CS1002: ; expected [C:\app\App.csproj]");

        Assert.NotNull(d);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal("; expected", d.Message);
        Assert.Equal(@"C:\app\Views\Main.cs", d.FilePath);
        Assert.Equal((15, 21), (d.Line, d.Column));
    }

    [Fact]
    public void ParseLine_ReadsWarningWithEndPosition()
    {
        var d = BuildDiagnostics.ParseLine("/home/u/app/Program.cs(3,1,3,9): warning CS8618: Non-nullable field 'x' must contain a non-null value.");

        Assert.NotNull(d);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("/home/u/app/Program.cs", d.FilePath);
    }

    [Fact]
    public void ParseLine_ReadsToolErrorWithoutFile()
    {
        var d = BuildDiagnostics.ParseLine("MSBUILD : error MSB1009: Project file does not exist.");

        Assert.NotNull(d);
        Assert.Null(d.FilePath);
        Assert.Equal("MSB1009", d.Code);
    }

    [Theory]
    [InlineData("Build succeeded.")]
    [InlineData("    0 Error(s)")]
    [InlineData("  Determining projects to restore...")]
    public void ParseLine_IgnoresOrdinaryOutput(string line) => Assert.Null(BuildDiagnostics.ParseLine(line));

    [Fact]
    public void Parse_RemovesTheSummaryDuplicates()
    {
        const string line = @"C:\a\P.cs(1,1): error CS0103: The name 'x' does not exist in the current context [C:\a\A.csproj]";
        var output = $"{line}\nBuild FAILED.\n\n{line}\n    1 Error(s)\n";

        Assert.Single(BuildDiagnostics.Parse(output));
    }
}

public sealed class ExpressionEvaluatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("10 % 4", 2)]
    [InlineData("sqrt(16) + abs(-3)", 7)]
    [InlineData("max(1, 7, 3) - min(4, 2)", 5)]
    [InlineData("round(2.345, 2)", 2.35)]
    [InlineData("log(1000)", 3)]
    [InlineData("log(8, 2)", 3)]
    [InlineData("1920 / 1080 * 720", 1280)]
    [InlineData("1.5e3 + 0.5", 1500.5)]
    public void Evaluate_ComputesExpressions(string expression, double expected) =>
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression), 9);

    [Fact]
    public void Evaluate_KnowsConstants() => Assert.Equal(Math.PI * 2, ExpressionEvaluator.Evaluate("2*pi"), 12);

    [Theory]
    [InlineData("")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("foo(1)")]
    [InlineData("2 $ 3")]
    public void Evaluate_RejectsInvalidInput(string expression) =>
        Assert.Throws<FormatException>(() => ExpressionEvaluator.Evaluate(expression));

    [Fact]
    public void Evaluate_ReportsDivisionByZero() =>
        Assert.Throws<DivideByZeroException>(() => ExpressionEvaluator.Evaluate("1 / (2 - 2)"));
}

public sealed class CodeFormatterTests
{
    [Fact]
    public void Format_CSharpIndentsAndSpaces()
    {
        var result = CodeFormatter.Format("class A{void M(){var x=1;}}", ".cs");

        Assert.True(result.Changed);
        Assert.Contains("var x = 1;", result.Text);
        Assert.Contains("class A", result.Text);
    }

    [Fact]
    public void Format_JsonIsIndented()
    {
        var result = CodeFormatter.Format("{\"a\":1,\"b\":[1,2]}", ".json");

        Assert.Contains("\n  \"a\": 1", result.Text);
    }

    [Fact]
    public void Format_BrokenJsonIsLeftUntouched()
    {
        var result = CodeFormatter.Format("{\"a\":", ".json");

        Assert.False(result.Changed);
        Assert.StartsWith("Not formatted", result.Summary);
    }

    [Fact]
    public void Format_XamlIsReindented()
    {
        var result = CodeFormatter.Format("<Window><Grid><Button Content=\"Hi\"/></Grid></Window>", ".axaml");

        Assert.Contains("\n  <Grid>", result.Text);
        Assert.Contains("\n    <Button", result.Text);
    }

    [Fact]
    public void Format_KeepsWindowsLineEndings()
    {
        var result = CodeFormatter.Format("line one   \r\nline two\t\r\n", ".txt");

        Assert.Equal("line one\r\nline two\r\n", result.Text);
    }
}
