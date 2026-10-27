# Publishing to NuGet

Nothing has been pushed yet. This is the procedure for when it is.

## Where this lives

```
C:\experiment\VibeCoding\Vibe_SDK\PollenRobotics.Net
```

`Vibe_SDK` is one repository holding several SDKs, each in its own subfolder next to `DepthAI.Net`
and `Unitree.Net`. Pack from here, never from a scratch directory: packages built outside the
repository carry no commit and no tag to go back to when a bug report arrives against a version.

If the tree is ever copied in again, exclude the build outputs:

```powershell
robocopy <source> C:\experiment\VibeCoding\Vibe_SDK\PollenRobotics.Net /E /XD bin obj artifacts .vs node_modules
```

`obj` holds absolute paths to wherever it was built, and a stale `project.assets.json` produces
restore errors naming a directory that no longer exists.

Because the SDK is a subfolder rather than the repository root, the two URLs in
[Directory.Build.props](../Directory.Build.props) are deliberately different:

| Property | Value | Why |
|---|---|---|
| `PackageProjectUrl` | `.../Vibe_SDK/tree/main/PollenRobotics.Net` | Where a reader should land |
| `RepositoryUrl` | `.../Vibe_SDK` | Source Link resolves paths relative to the repository root; a tree URL sends every "go to definition" to a 404 |

## Package identity

Assembly names and namespaces are unprefixed. **Package IDs carry the publisher prefix.**

| Project | Package ID | Namespace |
|---|---|---|
| `PollenRobotics.Net.Core` | `Gravicode.PollenRobotics.Net.Core` | `PollenRobotics.Net.Core` |
| `PollenRobotics.Net.Kinematics` | `Gravicode.PollenRobotics.Net.Kinematics` | `PollenRobotics.Net.Kinematics` |
| `PollenRobotics.Net.Transport` | `Gravicode.PollenRobotics.Net.Transport` | `PollenRobotics.Net.Transport` |
| `PollenRobotics.Net.ReachyMini` | `Gravicode.PollenRobotics.Net.ReachyMini` | `PollenRobotics.Net.ReachyMini` |
| `PollenRobotics.Net.MicroDuck` | `Gravicode.PollenRobotics.Net.MicroDuck` | `PollenRobotics.Net.MicroDuck` |
| `PollenRobotics.Net.Reachy2` | `Gravicode.PollenRobotics.Net.Reachy2` | `PollenRobotics.Net.Reachy2` |
| `PollenRobotics.Net.Simulation` | `Gravicode.PollenRobotics.Net.Simulation` | `PollenRobotics.Net.Simulation` |
| `PollenRobotics.Net.Ml` | `Gravicode.PollenRobotics.Net.Ml` | `PollenRobotics.Net.Ml` |
| `PollenRobotics.Net.Ai` | `Gravicode.PollenRobotics.Net.Ai` | `PollenRobotics.Net.Ai` |
| `PollenRobotics.Net.Ui` | `Gravicode.PollenRobotics.Net.Ui` | `PollenRobotics.Net.Ui` |
| `PollenRobotics.Net.Wizard.Core` | `Gravicode.PollenRobotics.Net.Wizard.Core` | `PollenRobotics.Net.Wizard.Core` |

Keeping the namespaces unprefixed is deliberate: every `using` in the documentation, the twenty
templates, the samples and the gallery stays valid, and a rename would have been churn across 97
files to change nothing a caller can see.

The prefix lives in exactly two places, and they must agree:

- `PackageIdPrefix` in [Directory.Build.props](../Directory.Build.props) — applied to every packable
  project by [Directory.Build.targets](../Directory.Build.targets).
- `ProjectScaffold.PackageIdPrefix` in the wizard — what generated projects write into their
  `PackageReference` lines.

`tools/PollenRobotics.Net.TemplateCheck` strips the prefix when it rewrites a generated project to
compile against the working tree, so a mismatch between those two shows up as a template that fails
to restore rather than as something silent.

### Prefix reservation

`Gravicode.*` should be reserved on nuget.org before the first push
(**Manage packages → Package ID prefix reservation**). Without a reservation the prefix is only a
naming convention and anyone can publish under it.

## Version

`VersionPrefix` in `Directory.Build.props`, currently `0.3.0`. All twelve packages ship in lockstep —
they reference each other by exact version, so a partial push leaves a version of one package that
cannot restore.

Bump it in that one place only. `SdkInfo.Version` reads it back off the assembly at runtime, so
the CLI banner, the wizard's About dialog, the `PackageReference` lines in generated projects and in
Jack's answers, and the simulated duck's firmware string all follow automatically. They used to be
five separate literals, and 0.1.1 is the release that proved they drift.

0.x is the honest number. Nothing here has run against physical hardware, and the parts most likely
to change are the wire formats listed in [PROGRESS.md](../PROGRESS.md). Reaching 1.0 means one thing:
a real robot answered.

For a prerelease, append a suffix rather than editing the prefix:

```bash
dotnet pack -c Release -p:VersionSuffix=preview.1
```

## The icon

`assets/package-icon.png` is embedded in every package, and is generated:

```bash
python tools/package-icon.py
```

Embedded via `PackageIcon`, never `iconUrl` - the latter is deprecated, newer clients ignore it, and
NuGet's prefix-reservation guidance asks for the embedded form. Regenerate rather than editing the
PNG, for the same reason the robot models are generated.

## Pack

```bash
dotnet build PollenRobotics.Net.slnx -c Release
dotnet run --project tests/PollenRobotics.Net.Tests
dotnet run --project tools/PollenRobotics.Net.TemplateCheck
dotnet pack PollenRobotics.Net.slnx -c Release
```

Twelve `.nupkg` and twelve `.snupkg` land in `artifacts/packages`. Only `src/` packs; apps, samples,
tools and tests opt out through `IsPackable` in `Directory.Build.props`.

Run `TemplateCheck` before packing, not after. It is the only thing that compiles the twenty
templates, and a template that references a package ID that will not exist on nuget.org is a broken
first experience for whoever installs one.

### Check a package before pushing it

```powershell
dotnet nuget locals http-cache --clear
Expand-Archive artifacts\packages\Gravicode.PollenRobotics.Net.Core.0.3.0.nupkg -DestinationPath artifacts\inspect -Force
```

Look at the `.nuspec` inside: the `id` should carry the prefix, the dependency `id`s should carry it
too, and `README.md` should be at the package root.

## CI does this for you

Tagging is the whole release:

```bash
git tag pollenrobotics-v0.3.1 && git push origin pollenrobotics-v0.3.1
```

`.github/workflows/pollenrobotics-net-publish.yml` then builds, runs the tests, compiles all twenty
templates, packs and pushes with the repository's `NUGET_API_KEY` secret. The steps below are what
it runs, and what to do by hand when CI is not an option.

**The workflow refuses a tag that disagrees with the repository.** It reads `VersionPrefix` out of
`Directory.Build.props` and fails if the tag asks for a different version, because `-p:Version=`
would otherwise silently override it - the repo would say 0.3.0 while nuget.org received 0.4.0, and
`SdkInfo.Version` would bake the wrong number into the CLI banner and the About dialog. Bump
`VersionPrefix` and commit *before* tagging.

`workflow_dispatch` takes an explicit version and a `dry_run` box that packs without pushing.

## Push

The API key is in `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`, on the
`Nuget Api Key` line. Pass it through the environment; do not put it on a command line, where it
lands in shell history, and never into a tracked file.

```powershell
$env:NUGET_API_KEY = (Select-String -Path C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt -Pattern '^Nuget Api Key:\s*(.+)$').Matches.Groups[1].Value

dotnet nuget push "artifacts\packages\*.nupkg" `
  --source https://api.nuget.org/v3/index.json `
  --api-key $env:NUGET_API_KEY `
  --skip-duplicate
```

`--skip-duplicate` matters here: twelve packages push one at a time, and without it a failure
halfway through cannot be retried without erroring on everything already up.

Push `Gravicode.PollenRobotics.Net.Core` first. The other eleven depend on it, so if anything is
going to be rejected it will be rejected first, before eleven more uploads have gone out.

**You do not have to wait for it to index.** An earlier version of this page said nuget.org rejects
a package whose dependencies it cannot resolve, and that it was necessary to wait. It is not true:
0.3.0 pushed all eleven dependents while Core was still validating and every one was accepted.
nuget.org does not check dependency existence at upload. Waiting only matters before *installing*.

Symbol packages go up with the same command — `dotnet nuget push` picks up the matching `.snupkg`
automatically.

**A published version is permanent.** Unlisting hides a package; it does not remove it, and the
version number can never be reused. Push a prerelease first if there is any doubt.

## After the first push

- Tag the commit. The repository holds several SDKs, so the tag has to say which one:
  `git tag pollenrobotics-v0.3.0 && git push --tags`.
- Verify a clean install actually works, from outside the repository:

  ```bash
  dotnet new console -o /tmp/probe && cd /tmp/probe
  dotnet add package Gravicode.PollenRobotics.Net.ReachyMini
  dotnet add package Gravicode.PollenRobotics.Net.Simulation
  ```

  That is the exact pair a generated template asks for, and it is the first thing that would expose
  a wrong dependency ID.
- Add an install section to the README with the prefixed IDs.

---

## Ringkasan (Bahasa Indonesia)

1. **Pack dari dalam repo**, yaitu `C:\experiment\VibeCoding\Vibe_SDK\PollenRobotics.Net` —
   satu subfolder per SDK, bersebelahan dengan `DepthAI.Net` dan `Unitree.Net`. Jangan pack dari
   direktori scratch: paketnya tidak punya commit yang bisa dirujuk.
2. Nama paket sudah berprefix **`Gravicode.`** (misalnya `Gravicode.PollenRobotics.Net.ReachyMini`),
   sedangkan namespace tetap `PollenRobotics.Net.*` supaya seluruh dokumentasi, template, dan contoh
   kode tidak berubah.
3. Jalankan build, tes, dan `TemplateCheck` **sebelum** `dotnet pack`.
4. Ambil API key dari `PackageCredentials.txt` lewat environment variable, jangan ditulis ke file
   yang ikut ter-commit.
5. Push `...Net.Core` lebih dulu, tunggu terindeks, baru sepuluh paket sisanya.
6. Versi yang sudah terbit **tidak bisa dihapus atau dipakai ulang**. Kalau ragu, terbitkan
   prerelease dulu dengan `-p:VersionSuffix=preview.1`.
