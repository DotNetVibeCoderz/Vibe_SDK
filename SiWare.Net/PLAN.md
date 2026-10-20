# SiWare.NET roadmap

## Objective / Tujuan
Build a .NET 10 SDK with Rust signal processing, usable samples, and bilingual documentation based on `SiWareNet.txt`.

## Delivery / Implementasi
1. Inspect installed Si-Spect artifacts with REA and record evidence and unknowns.
2. Implement validated spectrum models, simulator, driver abstraction, transport, processing, and data exchange.
3. Implement Rust DSP with a versioned C ABI and managed integration.
4. Build a CLI and an interactive acquisition workbench with calibration, plots, history, and export.
5. Test failure paths and numerical results; capture screenshots; build NuGet artifacts.
6. Document installation, architecture, API usage, integration, and hardware validation in English and Bahasa Indonesia.

## Hardware release gate / Gerbang rilis perangkat
Vendor protocol details and physical device validation must be established before claiming broad Si-Ware hardware compatibility. Static strings do not establish packet layouts or calling conventions. Never label simulated measurements as real sensor readings.

## Distribution / Distribusi
Produce local NuGet packages first. Public publication requires a reviewed package identity, version, licensing, and passing hardware compatibility checks.
