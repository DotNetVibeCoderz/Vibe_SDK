# Repository Guidelines

## Project Structure & Module Organization

This folder is the `SiWare.Net/` project inside the `Vibe_SDK` monorepo; run all commands from here. `SiWareNet.txt` defines the requirements; `.claude/skills/frontend-design/` contains a local UI design skill. .NET code lives under `src/`, Rust under `native/`, tests under `tests/`, samples under `samples/`, helper scripts under `scripts/`, and documentation and screenshots under `docs/` (`en/`, `id/`, `research/`). Maintain `PLAN.md` for the roadmap and `Progress.md` for development tracking.

## Build, Test, and Development Commands

- `dotnet build SiWare.Net.slnx` and `dotnet test SiWare.Net.slnx` — build (warnings are errors) and run xUnit tests.
- `dotnet run --project samples/SiWare.Workbench` or `samples/SiWare.Cli` — launch a sample.
- `cargo test --manifest-path native/siware-dsp/Cargo.toml` and `cargo build --release` — Rust tests and the library the .NET build copies.
- `dotnet pack src/SiWare.Sdk -c Release -o artifacts/packages` — local NuGet package.
- `node scripts/ui-smoke.mjs` — Playwright UI smoke test against a Workbench on port 5088.
- `cargo fmt --check` and `cargo clippy` — check Rust formatting and lint warnings from the crate directory.

## Coding Style & Naming Conventions

Until formatter configuration is added, use four-space indentation, PascalCase for C# types and public members, and camelCase for parameters and local variables. Follow standard Rust snake_case naming and `rustfmt` formatting. Use `dotnet format <solution>` after creating the solution. Keep sensor transport, protocol handling, and public SDK APIs separate; make native resource ownership explicit and avoid unnecessary allocations.

## Testing Guidelines

Tests use xUnit; no coverage threshold is configured. Prefer descriptive names such as `ReadSpectrum_InvalidPayload_Throws`. Test protocol parsing, error handling, and resource cleanup without hardware. Separate hardware integration tests and document required devices and configuration.

## Commit & Pull Request Guidelines

The monorepo uses concise, imperative commit subjects in Bahasa Indonesia (e.g. `Tambahkan ikon paket`). Pull requests should describe behavior changes, link relevant issues, list validation performed, and include screenshots for UI changes. Update roadmap and progress documents when milestones change.

## Documentation & Configuration

Provide documentation in English and Bahasa Indonesia, with screenshots where useful and the requested Gravicode Studios attribution. Keep credentials outside version control; never copy local API keys or tokens into source, examples, logs, or documentation.
