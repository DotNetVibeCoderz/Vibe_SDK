# SiWare.NET roadmap

## Objective / Tujuan
Build a .NET 10 SDK with Rust signal processing, usable samples, and bilingual documentation based on `SiWareNet.txt`.

## Delivery / Implementasi
1. Inspect installed Si-Spect artifacts with REA and record evidence and unknowns. ✅
2. Implement validated spectrum models, simulator, driver abstraction, transport, processing, and data exchange. ✅
3. Implement Rust DSP with a versioned C ABI and managed integration. ✅
4. Build a CLI and an interactive acquisition workbench with calibration, plots, history, and export. ✅
5. Test failure paths and numerical results; capture screenshots; build NuGet artifacts. ✅
6. Document installation, architecture, API usage, integration, and hardware validation in English and Bahasa Indonesia. ✅
7. **Chemometrics** with GravicodeScience (the NumPy/Pandas/Scikit/SciPy counterpart): preprocessing, PCA, regression/calibration models, cross-validation, metrics, tabular export. ✅
8. **Visualization** with Visual.Net (the matplotlib/plotly/seaborn counterpart): headless spectrum overlays and PCA score plots for the CLI, reports, and docs. ✅
9. **AI interpretation** with HF.Net: optional natural-language explanation of an analysis summary through a real Hugging Face model. The token is supplied only at runtime; outputs are labelled as AI-generated and advisory. ✅
10. Integrate 7–9 into the CLI and Workbench, add tests (offline by default, live LLM test opt-in via environment variable), screenshots, and bilingual docs. ✅
11. Resolve vendor-driver unknowns (scan-time unit, mode, dark acquisition, discovery transport) with REA + Ghidra static analysis. ✅ except `mode` (see `docs/research/driver-ghidra.md`).

Core `SiWare.Sdk` stays dependency-free; chemometrics, visualization, and AI ship as separate opt-in packages.

## Hardware release gate / Gerbang rilis perangkat
Vendor protocol details and physical device validation must be established before claiming broad Si-Ware hardware compatibility. Static strings do not establish packet layouts or calling conventions. Never label simulated measurements as real sensor readings.

## Distribution / Distribusi
Produce local NuGet packages first. ✅ Hardware gate passed on 2026-10-26 (Si-NIR Matrix DVK, Windows 10); 0.1.0 is published through `.github/workflows/siware-net-publish.yml`. Package identity `Gravicode.SiWare.*` and the MIT license are approved. After the physical-sensor checklist passes, add a GitHub Actions workflow in the `Vibe_SDK` monorepo that packs and publishes to nuget.org using the `NUGET_API_KEY` repository secret (modelled on `.github/workflows/depthai-net-publish.yml`).
