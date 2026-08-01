# Unitree.Net

**SDK .NET 10 untuk robot Unitree** — wrapper modern di atas Unitree SDK 2, dengan kontrol low-level,
lokomosi high-level, integrasi ROS 2, ML, dan LLM.

**A .NET 10 SDK for Unitree robots** — a modern wrapper over Unitree SDK 2, with low-level control,
high-level locomotion, ROS 2 integration, ML, and LLM support.

[Bahasa Indonesia](#bahasa-indonesia) · [English](#english)

---

## Bahasa Indonesia

### Apa ini

Unitree.Net memberi aplikasi .NET akses penuh ke robot Unitree — kuadruped (Go2, B2) dan humanoid
(G1, H1, R1) — mulai dari perintah torsi per-motor pada 500 Hz sampai navigasi waypoint dan
antarmuka bahasa alami.

Format kabel (wire format) diimplementasikan ulang di C# dan **diverifikasi byte-per-byte** terhadap
tata letak struct C++ Unitree, sehingga CRC yang dihitung SDK ini persis sama dengan yang dihitung
firmware. Ini penting: firmware membuang perintah dengan CRC salah **tanpa pesan error apa pun**.

### Mulai cepat

Tidak punya robot? Jalankan simulator dulu — ada tampilan 3D-nya, dan telemetrinya sungguhan:

```bash
dotnet run --project apps/Unitree.Net.Simulator          # simulator 3D (Windows)
dotnet run --project samples/Unitree.Net.Samples.VirtualRobot   # versi headless
```

Lalu di terminal lain:

```bash
dotnet run --project apps/Unitree.Net.Cli -- status     # snapshot telemetri
dotnet run --project apps/Unitree.Net.Cli -- monitor    # dashboard langsung
dotnet run --project apps/Unitree.Net.Dashboard         # dashboard web
```

Mau langsung bikin aplikasi robot? Buka **Unitree Robot Wizard** — editor kode dengan 16 template
yang semuanya jalan di simulator tanpa perlu diubah:

```bash
dotnet run --project apps/Unitree.Net.Wizard
```

Perintah pertama yang harus dijalankan kalau robot "tidak terhubung":

```bash
dotnet run --project apps/Unitree.Net.Cli -- diagnose
```

Perintah ini tidak memerlukan robot dan langsung menunjukkan penyebab paling umum: interface jaringan
salah, native library belum dibangun, atau multicast diblokir.

### Contoh kode

```csharp
var services = new ServiceCollection();
services.AddUnitreeRobot(configuration);

await using var provider = services.BuildServiceProvider();
var robot = provider.GetRequiredService<UnitreeRobot>();

await robot.ConnectAsync();
await robot.Sport.StandUpAsync();
await robot.Sport.BalanceStandAsync();

// Stream mengirim ulang perintah 20 kali per detik — robot meng-expire perintah kecepatan sekitar
// setengah detik setelah menerimanya. Kalau proses ini mati, request berhenti dan robot ikut berhenti.
using VelocityStream stream = robot.Sport.StartVelocityStream();
stream.Command = new VelocityCommand(Forward: 0.4f, Lateral: 0f, YawRate: 0.2f);

await Task.Delay(TimeSpan.FromSeconds(3));
stream.Stop();
```

### Fitur

| Komponen | Isi |
|---|---|
| **Core** | Model robot, peta sendi, matematika pose, batas keselamatan, loop real-time |
| **Messages** | Codec CDR tanpa alokasi, tipe `unitree_go` / `unitree_api`, CRC-32 Unitree |
| **Dds** | Abstraksi transport; transport UDP multicast murni-managed dan loopback |
| **Interop** | Binding P/Invoke ke shim Cyclone DDS (kompatibel dengan firmware robot) |
| **Control** | Kontrol sendi low-level, klien sport, navigasi waypoint, motion switcher |
| **Manipulation** | Perencanaan trajektori quintic, kontrol lengan, koordinasi dual-arm |
| **Sensors** | IMU, LiDAR (PointCloud2), kontak kaki, monitor baterai, telemetry hub |
| **Ros2** | Jembatan ke `sensor_msgs/Imu`, `nav_msgs/Odometry`, `geometry_msgs/Twist` |
| **Diagnostics** | Metrik OpenTelemetry, health check ASP.NET Core |
| **Firmware** | Paket terverifikasi, staging, gating kesehatan, rollback otomatis |
| **Ml** | Analisis gait ML.NET, deteksi anomali, inferensi kebijakan TorchSharp |
| **Ai** | Semantic Kernel dengan pilihan OpenAI / Anthropic / Gemini / Ollama |
| **Simulation** | Kinematika delapan platform, dipakai simulator maupun test |
| **Wizard.Core** | Template proyek, scaffolding, build, deploy SSH ke robot |

### Perkakas

| Aplikasi | Isi |
|---|---|
| **[Simulator](docs/simulator.md)** | Simulator robot 3D. Pilih platform (Go2, Go2-W, B2, B2-W, G1, H1, H1-2, R1), tekan Start, dan telemetri sungguhan mulai dipublikasikan. Ada panel status, panel log, dan drive pad. |
| **[Robot Wizard](docs/wizard.md)** | Editor kode dengan 16 template aplikasi robot, build/run/deploy, dan **Jack The Code Bender** — asisten yang membaca proyek Anda dan menulis kode dengan API SDK yang sebenarnya. |
| **[Extension VS Code](docs/vscode-extension.md)** | Buat proyek, connect ke robot atau simulator, run, debug, dan deploy langsung dari editor. Ada panel status robot dan panel logs. |
| **CLI** | `diagnose`, `status`, `monitor`, postur, `move`, chat AI, plus `templates`/`new`/`probe`/`stream`/`deploy` untuk otomasi |
| **Dashboard** | Dashboard web Blazor dengan grafik telemetri langsung |

Semua template di wizard sudah diverifikasi bisa dikompilasi:

```bash
dotnet run --project tools/Unitree.Net.TemplateCheck
```

### Keselamatan

Robot ini berbobot puluhan kilogram dan bergerak di ruang nyata. Beberapa hal yang dibangun ke dalam SDK:

- **Envelope keselamatan** membatasi kecepatan, torsi, gain, dan laju perubahan setpoint per tick.
- **Stream kecepatan** menjaga perintah tetap mengalir; kalau proses mati robot berhenti sendiri.
- **Deteksi jatuh** dan **batas suhu motor** memicu emergency stop yang terkunci.
- **Fungsi gerak untuk LLM dinonaktifkan secara default** — harus diaktifkan secara sadar.

Baca [`docs/safety.md`](docs/safety.md) sebelum menjalankan kontrol low-level.

### Dokumentasi

Dokumentasi lengkap ada di [`docs/`](docs/), termasuk [simulator](docs/simulator.md) dan
[Robot Wizard](docs/wizard.md).

`PROGRESS.md` sengaja memisahkan "build dan test lulus" dari "tervalidasi di hardware". **Belum ada
yang dijalankan di robot sungguhan.**

### Kredit

Perkakas Unitree.Net — **Simulator**, **Robot Wizard** dengan Jack The Code Bender, dan **extension
VS Code** — dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.

---

## English

### What this is

Unitree.Net gives .NET applications full access to Unitree robots — quadrupeds (Go2, B2) and humanoids
(G1, H1, R1) — from per-motor torque commands at 500 Hz up to waypoint navigation and a natural-language
interface.

The wire format is reimplemented in C# and **verified byte-for-byte** against Unitree's C++ struct
layout, so the CRC this SDK computes is exactly the one the firmware computes. That matters: the
firmware discards a command with a bad CRC **without reporting anything at all**.

### Quick start

No robot? Start the simulator first — it has a 3D viewport, and the telemetry it publishes is real:

```bash
dotnet run --project apps/Unitree.Net.Simulator                  # 3D simulator (Windows)
dotnet run --project samples/Unitree.Net.Samples.VirtualRobot    # headless equivalent
```

Then, in another terminal:

```bash
dotnet run --project apps/Unitree.Net.Cli -- status     # one-shot telemetry
dotnet run --project apps/Unitree.Net.Cli -- monitor    # live dashboard
dotnet run --project apps/Unitree.Net.Dashboard         # web dashboard
```

To start writing a robot application, open the **Unitree Robot Wizard** — a code editor with 16
templates, every one of which runs against the simulator without edits:

```bash
dotnet run --project apps/Unitree.Net.Wizard
```

The first thing to run whenever a robot "won't connect":

```bash
dotnet run --project apps/Unitree.Net.Cli -- diagnose
```

It needs no robot and immediately distinguishes the three usual causes: the wrong network interface, a
missing native library, or filtered multicast.

### Example

```csharp
var services = new ServiceCollection();
services.AddUnitreeRobot(configuration);

await using var provider = services.BuildServiceProvider();
var robot = provider.GetRequiredService<UnitreeRobot>();

await robot.ConnectAsync();
await robot.Sport.StandUpAsync();
await robot.Sport.BalanceStandAsync();

// The stream resends the command at 20 Hz, which is what keeps the robot going — it expires a
// velocity about half a second after receiving one. If this process dies the resends stop and so
// does the robot. Pass commandTimeout when the command's source is remote and can vanish.
using VelocityStream stream = robot.Sport.StartVelocityStream();
stream.Command = new VelocityCommand(Forward: 0.4f, Lateral: 0f, YawRate: 0.2f);

await Task.Delay(TimeSpan.FromSeconds(3));
stream.Stop();
```

### Packages

| Package | Contents |
|---|---|
| **Unitree.Net.Core** | Robot models, joint maps, pose maths, safety limits, real-time loop |
| **Unitree.Net.Messages** | Zero-allocation CDR codec, `unitree_go` / `unitree_api` types, Unitree CRC-32 |
| **Unitree.Net.Dds** | Transport abstraction; pure-managed multicast and loopback transports |
| **Unitree.Net.Interop** | P/Invoke bindings to the Cyclone DDS shim (firmware-compatible) |
| **Unitree.Net.Control** | Low-level joint control, sport client, waypoint navigation, motion switcher |
| **Unitree.Net.Manipulation** | Quintic trajectory planning, arm control, dual-arm coordination |
| **Unitree.Net.Sensors** | IMU, LiDAR (PointCloud2), foot contact, battery monitoring, telemetry hub |
| **Unitree.Net.Ros2** | Bridge to `sensor_msgs/Imu`, `nav_msgs/Odometry`, `geometry_msgs/Twist` |
| **Unitree.Net.Diagnostics** | OpenTelemetry-compatible metrics, ASP.NET Core health checks |
| **Unitree.Net.Firmware** | Verified packages, staging, health gating, automatic rollback |
| **Unitree.Net.Ml** | ML.NET gait analysis, anomaly detection, TorchSharp policy inference |
| **Unitree.Net.Ai** | Semantic Kernel with OpenAI / Anthropic / Gemini / Ollama providers |
| **Unitree.Net.Simulation** | Rig-driven kinematics for all eight platforms, shared by the simulator and the tests |
| **Unitree.Net.Wizard.Core** | Project templates, scaffolding, build orchestration, SSH deployment |

### Tools

| Application | What it does |
|---|---|
| **[Simulator](docs/simulator.md)** | A 3D robot simulator. Pick a platform (Go2, Go2-W, B2, B2-W, G1, H1, H1-2, R1), press Start, and it publishes real telemetry on the SDK's own transport — the CLI and dashboard connect to it exactly as they would to hardware. Status panel, drive pad, system log, and a ribbon showing each topic's measured rate. |
| **[Robot Wizard](docs/wizard.md)** | A code editor with 16 project templates, build/run/deploy, and **Jack The Code Bender** — an assistant that reads your project, looks things up, and writes code against the real SDK API rather than a remembered one. |
| **[VS Code extension](docs/vscode-extension.md)** | Create a project, connect to a robot or the simulator, run, debug and deploy without leaving the editor. Live robot status and logs in the sidebar. |
| **CLI** | `diagnose`, `status`, `monitor`, postures, `move`, AI chat, plus `templates` / `new` / `probe` / `stream` / `deploy` for automation |
| **Dashboard** | Blazor web dashboard with live telemetry charts |

Every wizard template is verified to compile:

```bash
dotnet run --project tools/Unitree.Net.TemplateCheck
```

The simulator and the wizard are WPF shells and therefore Windows-only. The engines behind them —
`Unitree.Net.Simulation` and `Unitree.Net.Wizard.Core` — are plain `net10.0` and run anywhere.

### Transports

Three transports sit behind one interface, so the same application runs against hardware, a host-only
link, or an in-process loopback with only a configuration change:

| Transport | Talks to firmware | Needs native library | Use for |
|---|---|---|---|
| `CycloneNative` | **Yes** | Yes | Real robots |
| `ManagedMulticast` | No | No | Simulators, host-to-host, integration tests |
| `Loopback` | No | No | Unit tests |

`ManagedMulticast` carries CDR payloads inside Unitree.Net's own framing — it is **not** RTPS and cannot
reach robot firmware. See [`native/README.md`](native/README.md) to build the native shim.

### Safety

These robots weigh tens of kilograms and move in a real space. Built into the SDK:

- A **safety envelope** bounds velocity, torque, gains and per-tick setpoint slew.
- The **velocity stream** keeps commands flowing; if the process dies the robot stops within half a
  second, and an explicit `commandTimeout` covers a remote source that vanishes.
- **Fall detection** and **motor temperature limits** latch an emergency stop.
- **LLM motion functions are off by default** and require two separate opt-ins to enable.

Read [`docs/safety.md`](docs/safety.md) before running low-level control.

### Building

```bash
dotnet build Unitree.Net.slnx
dotnet test  Unitree.Net.slnx
```

Requires the .NET 10 SDK. The native shim is optional and built separately with CMake.

### Documentation

Full documentation lives in [`docs/`](docs/):

- [Getting started](docs/getting-started.md)
- [Architecture](docs/architecture.md)
- [DDS networking](docs/dds-networking.md) — read this when nothing connects
- [Low-level control](docs/low-level-control.md)
- [Safety](docs/safety.md)
- [Navigation](docs/navigation.md)
- [AI workflows](docs/ai-workflow.md)
- [ROS 2 bridge](docs/ros2-bridge.md)
- [Simulator](docs/simulator.md)
- [Robot Wizard](docs/wizard.md)
- [VS Code extension](docs/vscode-extension.md)

Project planning is in [PLAN.md](PLAN.md); current status in [PROGRESS.md](PROGRESS.md).

`PROGRESS.md` deliberately separates "builds and passes tests" from "validated on hardware". **Nothing
in this repository has been run against a real robot.**

### Credits

The Unitree.Net tooling — the **Simulator**, the **Robot Wizard** with Jack The Code Bender, and the
**VS Code extension** — dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.

### Licence

MIT. Not affiliated with or endorsed by Unitree Robotics.
