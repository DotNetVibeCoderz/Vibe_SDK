using System.Reflection;

namespace PollenRobotics.Net.Core;

/// <summary>
/// Identity of this SDK build: the version, and the credit that goes on every application.
/// </summary>
/// <remarks>
/// The version is read from the assembly rather than written down, because the same number has to
/// appear in the CLI banner, the wizard's About dialog, the <c>PackageReference</c> lines that
/// generated projects and Jack's code answers emit, and the simulated firmware string. Every one of
/// those was a separate literal, and a release bumped <c>VersionPrefix</c> without touching any of
/// them - so a 0.1.1 build cheerfully told you it was 0.1.0 and scaffolded projects against a
/// version that no longer matched.
/// </remarks>
public static class SdkInfo
{
    // Declaration order matters: static field initializers run top to bottom, so FullVersion has to
    // be assigned before Version reads it. Declared the other way round, Version silently trimmed a
    // null and the compiler said so - CS8604 was the only warning between this and a build that
    // reported no version at all.

    /// <summary>The informational version exactly as the build stamped it.</summary>
    public static string FullVersion { get; } =
        typeof(SdkInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SdkInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>The SDK version, as three numbers with any build metadata removed.</summary>
    /// <remarks>
    /// <c>AssemblyInformationalVersion</c> carries a <c>+commit</c> suffix once Source Link is on,
    /// and a prerelease suffix such as <c>-preview.1</c> when one is set. The suffixes are stripped
    /// here because this string goes into <c>PackageReference</c> lines, where a <c>+</c> is not
    /// valid; use <see cref="FullVersion"/> when the whole thing is wanted.
    /// </remarks>
    public static string Version { get; } = Trim(FullVersion);

    /// <summary>Who made this.</summary>
    public const string Studio = "Gravicode Studios";

    /// <summary>Who leads it.</summary>
    public const string Lead = "Kang Fadhil";

    /// <summary>The one-line credit shown in application chrome.</summary>
    public const string Credit = $"{Studio}, led by {Lead}";

    private static string Trim(string informational)
    {
        int metadata = informational.IndexOf('+');
        string withoutMetadata = metadata < 0 ? informational : informational[..metadata];

        int prerelease = withoutMetadata.IndexOf('-');
        return prerelease < 0 ? withoutMetadata : withoutMetadata[..prerelease];
    }
}
