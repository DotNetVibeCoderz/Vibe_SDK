# Publishing to NuGet

[Bahasa Indonesia](nuget.id.md) · **English**

The SDK ships as `Gravicode.TypeSafeSdk`. The package id is prefixed because this is an **unofficial** port: an unprefixed `TypeSafeSdk` would read as a vendor package, and it is not one.

## Install

```bash
dotnet add package Gravicode.TypeSafeSdk
```

```xml
<PackageReference Include="Gravicode.TypeSafeSdk" Version="1.2.0" />
```

The assembly and root namespace stay `TypeSafeSdk`, so `using TypeSafeSdk;` is all a consumer writes.

## Versioning

`TypeSafeConstants.SdkVersion` and the `<Version>` in `TypeSafeSdk.csproj` are the same number, and it is also the value sent in the `X-TypeSafe-SDK` header. Bump both together, or the header will lie about which build made the request.

Semantic versioning applies:

- **patch** — a fix with no surface change
- **minor** — new API, existing calls keep compiling
- **major** — anything a consumer must edit code for

## Pack

```bash
dotnet pack TypeSafeSdk/TypeSafeSdk.csproj -c Release -o artifacts
```

This produces `artifacts/Gravicode.TypeSafeSdk.<version>.nupkg` and a matching `.snupkg` symbol package. The package embeds the README, the icon (`TypeSafeSdk/icon.png`, 256×256: a generic `<T>` over a probability strip in the Jev Gallery palette), the MIT licence expression, the repository URL, and a deterministic source link, so a consumer can step into the SDK from their debugger.

Before pushing, confirm the package contains what you expect:

```bash
dotnet nuget verify artifacts/Gravicode.TypeSafeSdk.1.2.0.nupkg
unzip -l artifacts/Gravicode.TypeSafeSdk.1.2.0.nupkg
```

## Release from CI

Releases normally go through GitHub Actions, which reads the repository secret `NUGET_API_KEY`. Two workflows live at the monorepo root:

- `.github/workflows/typesafe-sdk-ci.yml` — on every push or PR touching `TypeSafeSDK/**`: builds the whole solution and runs every test on Windows (the board games are WPF), runs the SDK and App Generator tests on Ubuntu, then packs a `1.2.0-ci.<run>` prerelease as a build artifact. Nothing is pushed.
- `.github/workflows/typesafe-sdk-publish.yml` — on a `typesafe-sdk-v*` tag, or by hand from the Actions tab: tests, packs, and pushes to nuget.org.

```bash
git tag typesafe-sdk-v1.2.0
git push origin typesafe-sdk-v1.2.0
```

The tag is prefixed because Vibe_SDK holds several SDKs, and another SDK's release tag must not publish this package. The publish workflow refuses to run unless the tag, `<Version>`, and `TypeSafeConstants.SdkVersion` are the same number, so the header can never lie about the build. A manual run with `dry_run` checked packs and uploads the package as an artifact without pushing it — the way to rehearse a release.

## Push by hand

Only needed when CI is unavailable.

The API key lives outside the repository, in `PackageCredentials.txt`. Never paste it into a command that will be recorded in shell history or committed.

```powershell
$key = (Select-String -Path 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt' -Pattern 'ApiKey\s*=\s*(.+)').Matches.Groups[1].Value.Trim()
dotnet nuget push artifacts\Gravicode.TypeSafeSdk.1.2.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $key
```

A push is permanent. nuget.org lets you unlist a version so it stops appearing in search, but it can never be deleted, and that exact version number can never be reused. Check the version number twice before running the command.

## Release checklist

1. `dotnet test TypeSafeSDK.slnx` is green.
2. `TypeSafeConstants.SdkVersion` matches `<Version>`.
3. The version is not already on nuget.org.
4. `docs/` and `README.md` describe the version being shipped.
5. `Progress.md` records the release.
6. Push a `typesafe-sdk-v<version>` tag, or pack, inspect, and push by hand.

Created by **Gravicode Studios**, led by Kang Fadhil.
