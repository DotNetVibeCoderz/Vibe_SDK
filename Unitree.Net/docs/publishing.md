# Publishing and CI

The SDK ships as fifteen NuGet packages plus one umbrella package, built and published by GitHub
Actions from `DotNetVibeCoderz/Vibe_SDK`.

## The packages

`dotnet add package Unitree.Net` is the entry point. `Unitree.Net` carries no assembly of its own — it
is a metapackage whose only content is a dependency list — and pulls in every library a robot
application normally needs:

```
Unitree.Net
├── Unitree.Net.Core
├── Unitree.Net.Messages
├── Unitree.Net.Dds
├── Unitree.Net.Interop
├── Unitree.Net.Control
├── Unitree.Net.Sensors
├── Unitree.Net.Manipulation
├── Unitree.Net.Diagnostics
├── Unitree.Net.Firmware
├── Unitree.Net.Ros2
├── Unitree.Net.Ai
└── Unitree.Net.Extensions.DependencyInjection
```

`Unitree.Net.Ml`, `Unitree.Net.Simulation` and `Unitree.Net.Wizard.Core` are published but left out of
the umbrella:

- **`Unitree.Net.Ml`** brings ML.NET and TorchSharp, which are hundreds of megabytes of native
  runtime. Only gait analysis and learned-locomotion applications need it, and making everyone else
  download it would be unreasonable.
- **`Unitree.Net.Simulation`** is a development dependency — it belongs in the test project, not in
  what gets deployed to the robot.
- **`Unitree.Net.Wizard.Core`** exists to back the Robot Wizard and the VS Code extension. Robot
  applications have no use for project scaffolding.

The native Cyclone DDS shim is **not** in any package. It is built with CMake against a real
`unitree_sdk2` checkout — see [`native/README.md`](../native/README.md) — and without it the SDK can
reach the simulator but not robot firmware.

## Where the metadata comes from

Everything shared lives in `Directory.Build.props`: authorship, licence, tags, `PackageProjectUrl` and
`RepositoryUrl` (both pointing at this SDK's folder in the monorepo), symbol packages, and the README
that every package embeds. Each project supplies only its own `<Description>`.

Two settings are easy to undo by accident:

- **`PublishRepositoryUrl` is `false` on purpose.** Source Link overwrites `RepositoryUrl` from the git
  remote when it is true, and the remote describes the whole `Vibe_SDK` monorepo rather than this
  folder inside it. The explicit URLs are the correct ones.
- **`ContinuousIntegrationBuild` is set only under GitHub Actions.** It normalises source paths in the
  PDBs, which is what makes a package byte-reproducible — but it would break local debugging, so it
  stays off on a developer machine.

## Releasing

Push a prefixed tag. The prefix keeps the SDK's releases distinct from anything else in the monorepo:

```bash
git tag unitree-net-v0.2.0
git push origin unitree-net-v0.2.0
```

`.github/workflows/release.yml` then builds, tests, packs at the tag's version, pushes every
`.nupkg` and `.snupkg` to nuget.org, and attaches them to a GitHub release. The version comes from
the tag, and a tag that is not a valid NuGet version fails the job before anything is uploaded —
nuget.org has no unpublish.

To rehearse without publishing, run the workflow from the Actions tab with **dry_run** left on: it
packs and uploads the packages as a build artifact and skips the push.

The one prerequisite is the repository secret **`NUGET_API_KEY`** — a nuget.org API key scoped to the
`Unitree.Net*` glob, so a leaked key cannot push anything else. The workflow fails with a clear message
if it is missing rather than skipping the push silently.

## CI

`.github/workflows/ci.yml` runs on every push and pull request that touches the SDK:

| Job | Runner | What it protects |
|---|---|---|
| `windows` | `windows-latest` | The whole solution, including both WPF shells, plus the tests |
| `linux` | `ubuntu-latest` | Every cross-platform project — Ubuntu on x86_64 and ARM is the deployment target |
| `templates` | `ubuntu-latest` | Scaffolds and compiles all 16 wizard templates |
| `pack` | `ubuntu-latest` | Every package builds, and uploads them for inspection |
| `native` | `ubuntu-latest` | The Cyclone DDS shim compiles |

The Linux job cannot just build the solution: `Unitree.Net.Simulator` and `Unitree.Net.Wizard` target
`net10.0-windows10.0.19041.0` for WPF and WebView2, and do not restore anywhere else. It enumerates the
projects and skips those two instead.

The `templates` job is the one that catches the failure mode described in `CLAUDE.md`: the 16 templates
and `SdkPlugin`'s API reference are hand-written against the SDK's public surface, and nothing
recompiles them by accident. Every one of them failed the first time `TemplateCheck` ran.

Two jobs are deliberately not gates yet:

- **Formatting** runs with `continue-on-error`. `dotnet format --verify-no-changes` currently reports
  IDE1006 violations in `Unitree.Net.Wizard.Core`; the check reports them without failing the build.
  Remove `continue-on-error` once the tree is clean.
- **`native`** also runs with `continue-on-error`, because the shim needs a Cyclone DDS and
  `unitree_sdk2` that the runner does not necessarily have.

## If the SDK ever moves to its own repository

Both workflows resolve paths through a single `PROJECT_DIR` environment variable, set to
`Unitree.Net` because the SDK sits in a subfolder of `Vibe_SDK`. Set it to `.`, move the workflow files
to the new repository root, and drop the `Unitree.Net/**` path filters; nothing else changes.
