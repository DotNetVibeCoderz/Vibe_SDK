# Getting started

*SiWare.NET, created by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/getting-started.md)

## Requirements

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0.100 or later (`global.json` rolls forward to the latest feature band) | Everything |
| Rust toolchain | 1.85 or later (edition 2024) | Optional native DSP kernels |
| Windows x64 | n/a | Only the vendor bridge; the SDK, simulator, and apps are cross-platform |
| CPython 3.10 x64 | 3.10.x | Only the vendor bridge (the vendor module is built for `cp310-win_amd64`) |
| Node.js + Playwright | Node 20+ | Only UI smoke tests and screenshot scripts |

## Build and test

```sh
# 1. Optional: native kernels. Build in release; the .NET build copies the library automatically.
cd native/siware-dsp
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
cargo build --release
cd ../..

# 2. .NET solution (warnings are errors)
dotnet build SiWare.Net.slnx
dotnet test SiWare.Net.slnx
dotnet test tests/SiWare.Sdk.Tests --filter "FullyQualifiedName~ProcessingTests"
```

If you skip step 1, everything still works: `NativeDsp.IsAvailable` returns `false`, and the apps use the managed implementations. Rebuild the .NET projects after building the Rust library so that it is copied next to the assemblies.

## Run the Workbench

```sh
dotnet run --project samples/SiWare.Workbench
```

Open `http://localhost:5069`, then:

1. **Connect instrument.** The simulator is used unless `SIWARE_VENDOR_CONFIG` is set (see [Vendor integration](vendor-integration.md)).
2. Under **Calibration**, capture a **Dark signal** and a **Reference**.
3. **Capture sample**, then select it and press **Absorbance**. **SNV** and **Smooth** work on any selected spectrum.
4. Hover the chart, or focus it and use ←/→/Home/End to read values.
5. Export the selected spectrum as **CSV** or **JSON**. The session keeps the latest 100 spectra in memory.

The **EN / ID** button switches the language, and the choice is remembered in the browser. Acquisitions can be cancelled while running and time out after 3 minutes.

![Workbench](../screenshots/workbench-desktop.png)

## Use the CLI

```sh
dotnet run --project samples/SiWare.Cli -- help
dotnet run --project samples/SiWare.Cli -- info                       # .NET version and Rust ABI availability
dotnet run --project samples/SiWare.Cli -- demo --output artifacts/demo
dotnet run --project samples/SiWare.Cli -- scan --output spectrum.json
dotnet run --project samples/SiWare.Cli -- process --input spectrum.json --operation snv --output snv.json
dotnet run --project samples/SiWare.Cli -- discover --vendor-config vendor.json
```

`demo` is simulation-only. It writes `dark.json`, `reference.json`, `sample.json`, `absorbance.json`, and `absorbance.csv`. `scan` and `discover` use the vendor driver when `--vendor-config` is given. Exit codes: `0` success, `1` invalid input or device error, `2` cancelled (Ctrl+C) or timed out.

![CLI](../screenshots/cli.png)

## Build the NuGet package

```sh
cargo build --release --manifest-path native/siware-dsp/Cargo.toml
dotnet pack src/SiWare.Sdk -c Release -o artifacts/packages
```

This produces `Gravicode.SiWare.Sdk.<version>.nupkg`, which contains:

- `lib/net10.0/SiWare.Sdk.dll` and its XML documentation
- `runtimes/win-x64/native/siware_dsp.dll`, included only if the release library exists at pack time
- `contentFiles/any/any/SiWare/vendor_bridge.py`, copied to the consumer's output folder as `SiWare/vendor_bridge.py`
- The icon (`docs/assets/siware-net-icon.svg`, rendered with `node scripts/render-icon.mjs`), README, and MIT license

To consume it locally, add `artifacts/packages` as a package source in `nuget.config` and reference `Gravicode.SiWare.Sdk` with prerelease enabled. If you repack the same version, clear `~/.nuget/packages/gravicode.siware.sdk` first. Public publication is gated; see [Hardware validation](hardware-validation.md).

## UI smoke test and screenshots

```sh
dotnet run --project samples/SiWare.Workbench --urls http://127.0.0.1:5088
node scripts/ui-smoke.mjs        # in a second terminal; set SIWARE_TEST_URL for another address
node scripts/cli-screenshot.mjs  # regenerates docs/screenshots/cli.png from real CLI output
```

The smoke test drives the whole workflow in headless Chrome (connect, calibrate, acquire, process, inspect, export, cancel, language switch, mobile layout). It then rewrites the Workbench screenshots in `docs/screenshots/`. Install Playwright with `npm install` in `.tools/ui`, and set `SIWARE_BROWSER_CHANNEL` if Chrome is not installed.
