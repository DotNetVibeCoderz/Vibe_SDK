using System.Text.RegularExpressions;
using PollenRobotics.Net.Ai.Plugins;
using PollenRobotics.Net.Core;
using Shouldly;
using Xunit;

namespace PollenRobotics.Net.Tests;

/// <summary>
/// Checks on the project files Jack writes.
/// </summary>
/// <remarks>
/// Nothing compiles Jack's output, so these assert the two properties that made it fail to restore:
/// the package IDs have to exist, and their versions have to be the versions those packages
/// actually have.
/// </remarks>
public partial class CodeGenerationTests
{
    [GeneratedRegex("""<PackageReference Include="(?<id>[^"]+)" Version="(?<version>[^"]+)" />""")]
    private static partial Regex PackageReferenceLine { get; }

    private static (string Id, string Version)[] References(string projectFile) =>
        [.. PackageReferenceLine.Matches(projectFile)
            .Select(m => (m.Groups["id"].Value, m.Groups["version"].Value))];

    /// <summary>SDK packages are published under the publisher prefix, so a reference without it does not exist.</summary>
    [Theory]
    [InlineData("console")]
    [InlineData("desktop")]
    [InlineData("web")]
    [InlineData("embedded")]
    public void SdkReferencesCarryThePublisherPrefix(string kind)
    {
        var plugin = new CodeGenerationPlugin();

        string projectFile = plugin.GenerateProjectFile("Probe", kind, "ReachyMini");

        (string Id, string Version)[] sdk =
            [.. References(projectFile).Where(r => r.Id.Contains("PollenRobotics", StringComparison.Ordinal))];

        sdk.ShouldNotBeEmpty();
        sdk.ShouldAllBe(r => r.Id.StartsWith("Gravicode.", StringComparison.Ordinal));
        sdk.ShouldAllBe(r => r.Version == SdkInfo.Version);
    }

    /// <summary>
    /// A third-party package must carry its own version, not the SDK's.
    /// </summary>
    /// <remarks>
    /// Regression test. Every reference used to be written at the SDK version, so a desktop project
    /// asked for Avalonia 0.1.0 — a version that has never existed — and the very first restore
    /// failed. Only desktop and embedded pull in third-party packages, which is why this went
    /// unnoticed: the console template, the one everybody tries first, has none.
    /// </remarks>
    [Theory]
    [InlineData("desktop", "Avalonia")]
    [InlineData("embedded", "Microsoft.Extensions.Hosting")]
    public void ThirdPartyReferencesDoNotUseTheSdkVersion(string kind, string expectedPackage)
    {
        var plugin = new CodeGenerationPlugin();

        string projectFile = plugin.GenerateProjectFile("Probe", kind, "ReachyMini");

        (string Id, string Version)[] thirdParty =
            [.. References(projectFile).Where(r => !r.Id.Contains("PollenRobotics", StringComparison.Ordinal))];

        thirdParty.Select(r => r.Id).ShouldContain(expectedPackage);
        thirdParty.ShouldAllBe(r => r.Version != SdkInfo.Version);
    }

    /// <summary>The version in generated project files follows the build.</summary>
    /// <remarks>
    /// It used to be a literal here, in the wizard's scaffold, in the CLI banner, in the About
    /// dialog and in the simulated duck's firmware string. A release bumped the packages and left
    /// all five behind.
    /// </remarks>
    [Fact]
    public void TheSdkVersionComesFromTheAssembly()
    {
        SdkInfo.Version.ShouldNotBeNullOrWhiteSpace();
        SdkInfo.Version.ShouldNotContain("+");
        SdkInfo.Version.ShouldNotContain("-");
        SdkInfo.Version.ShouldNotBe("0.0.0");

        // What the wizard writes and what Jack writes have to agree, or a project scaffolded one
        // way and extended the other ends up with two versions of the same package.
        var plugin = new CodeGenerationPlugin();
        string projectFile = plugin.GenerateProjectFile("Probe", "console", "ReachyMini");

        References(projectFile)
            .Where(r => r.Id.Contains("PollenRobotics", StringComparison.Ordinal))
            .Select(r => r.Version)
            .Distinct()
            .ShouldBe([SdkInfo.Version]);
    }
}
