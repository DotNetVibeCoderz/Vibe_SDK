# Unitree Robot Wizard

A code editor for building robot applications, with **Jack The Code Bender** — an assistant that reads
your project, looks things up, and writes code against the real SDK.

```bash
dotnet run --project apps/Unitree.Net.Wizard
```

## The window

```
┌──────────────────────────────────────────────────────────────────────────┐
│ {|} UNITREE ROBOT WIZARD  File Edit Build Help    ☀ Hide Jack  Build Run │
├────────────┬────────────────────────────────────┬────────────────────────┤
│ EXPLORER   │ Program.cs ● │ appsettings.json    │ J Jack The Code Bender │
│  Program.cs│ ┌────────────────────────────────┐ │ [chat] [+] [⟲]         │
│  appsett…  │ │  Monaco                        │ │                        │
│  README.md │ │                                │ │  thread                │
│            │ └────────────────────────────────┘ │                        │
│            ├────────────────────────────────────┤ │                      │
│            │ OUTPUT            2 errors  Clear  │ │ [Attach] [Send]      │
├────────────┴────────────────────────────────────┴────────────────────────┤
│ ● Ready  PatrolBot · Console  Program.cs  Ln 42, Col 8  Run ▸ Simulator  │
└──────────────────────────────────────────────────────────────────────────┘
```

The status bar always shows **where Run will send commands**. That is deliberate: it is the difference
between a mistake costing nothing and a mistake moving fifteen kilograms of robot.

## Menus

| Menu | Contents |
|---|---|
| **File** | New project, Open, Close, Save, Save all, recent projects |
| **Edit** | Undo/redo, cut/copy/paste, select all, find, replace, go to line, format, line-number toggle, command palette |
| **Build** | Build, Run, Stop, run target (simulator or robot), Deploy, Test robot connection |
| **Help** | Settings, where the settings file is, About |

The edit commands delegate to Monaco rather than reimplementing anything — the clipboard, the find
widget and the go-to-line box are the editor's own.

## New project

Two routes: **Blank** — pick a kind and get a minimal application that connects and prints a snapshot
— or **From template**, a searchable gallery of 16 worked examples.

| Kind | What it produces |
|---|---|
| Console | Runs on your machine, talks to the robot over the network |
| Desktop | A windowed operator console |
| Web | ASP.NET Core — a dashboard or an HTTP API |
| Embedded | Published self-contained for the robot's ARM64 module, deployed over SSH |

### Templates

| Id | Kind | What it does |
|---|---|---|
| `telemetry-monitor` | Console | Connects and prints battery, orientation, temperature, foot contact |
| `patrol-route` | Console | Walks a closed waypoint loop, checking battery each lap |
| `low-level-control` | Console | 500 Hz impedance loop holding a standing pose |
| `battery-guardian` | Console | Walks the robot home before the pack runs out |
| `gait-logger` | Console | Records joint states to CSV at full rate |
| `teleop-keyboard` | Console | Arrow-key driving through a watchdog-backed velocity stream |
| `ai-assistant` | Console | Natural-language supervision, motion off by default |
| `anomaly-watch` | Console | ML.NET gait statistics and departure detection |
| `arm-pick-place` | Console | Coordinated dual-arm trajectories on a humanoid |
| `desktop-control-panel` | Desktop | Operator console with posture buttons and a live readout |
| `web-telemetry-api` | Web | Minimal API with gated motion endpoints |
| `web-dashboard` | Web | Blazor Server page charting live telemetry |
| `embedded-inspection` | Embedded | Patrols stations, records readings, writes a report |
| `embedded-follow-me` | Embedded | Holds a standoff distance, stops when contact is lost |
| `embedded-fleet-reporter` | Embedded | Posts telemetry upstream, survives the link dropping |
| `ros2-bridge-node` | Embedded | Republishes telemetry for Nav2 and RViz |

**Every one of them compiles, and every one runs against the simulator without edits.** That is
checked, not assumed:

```bash
dotnet run --project tools/Unitree.Net.TemplateCheck
```

That tool scaffolds all sixteen and builds them. Run it whenever the SDK's public surface changes — it
is what caught the first version of this catalogue, where every template referenced members that did
not exist.

## Jack The Code Bender

A chat panel on the right, backed by Semantic Kernel. Show or hide it from the menu bar.

**Sessions.** Create, switch, delete, reset. Each is stored as its own JSON file under
`%APPDATA%\Unitree.Net.Wizard\sessions`, so a corrupt write costs one conversation rather than all of
them. A session takes its name from the first thing you type.

**Attachments.** Images are sent as image content, so a vision model actually looks at them. Documents
have their text extracted and included in the message. Anything over 12 MB is refused, and text is
truncated at 200 kB — a pasted log otherwise consumes the whole context window and pushes out the
conversation that gives it meaning.

**Rendering.** Full Markdown: tables scroll inside their own box, code blocks carry a language label
and a copy button, images and video render inline. Everything goes through DOMPurify — not because
Jack is hostile, but because his input includes pages he fetched and files he read.

### What he can do

| Function | Purpose |
|---|---|
| `describe_sdk` | The real API for an area — connection, locomotion, telemetry, low-level, navigation, arms, AI, ROS 2, config |
| `list_templates`, `get_template_code`, `scaffold_from_template` | Work from a known-good example |
| `get_project_info`, `read_project_file`, `write_project_file`, `search_project` | Read and edit the open project |
| `search_web` | Tavily search, when a key is configured |
| `fetch_page`, `read_file_from_url` | Read documentation or a source file |
| `get_current_time`, `date_difference` | A clock, which he does not otherwise have |
| `calculate`, `convert_units` | Arithmetic and the units robot work uses |

`describe_sdk` is the one that matters. This SDK is not in any model's training data, so without it
Jack invents plausible-looking members — `robot.Walk(1.0)`, `robot.Battery` — and the operator only
finds out at build time. The reference is curated by hand against the real public surface, so **it has
to be kept in step when the SDK changes**.

File access is confined to the open project. Every path is resolved and checked against the project
root, so a model that has been talked into writing to `../../../etc` simply cannot.

### Prompts

An empty session shows worked examples rather than a blinking cursor — 32 of them across nine
categories, written as complete requests because that is also what teaches the right way to ask. One
per category is shown by default; the rest are one click away.

## Settings

Stored in `app.config` beside the executable, in a file you can open, diff and copy between machines.

| Tab | Contains |
|---|---|
| Assistant | Provider (OpenAI, Anthropic, Gemini, Ollama), model, key, endpoint, temperature, max tokens, system prompt |
| Tools | Tavily key, workspace folder |
| Deployment | Robot host, port, user, password or private key, remote directory, systemd option |

Ollama is the default: no key, and nothing leaves the machine.

If you would rather not have a key on disk, leave it blank and set `UNITREE_WIZARD_APIKEY` or
`UNITREE_WIZARD_TAVILYKEY` in the environment. An `app.config` gets copied and committed more often
than anyone intends.

The **system prompt** is editable, and the default is worth reading before you change it — it carries
the three failures that produce no error at all on a real robot.

## Build, Run, Deploy

**Build** and **Run** shell out to the .NET CLI and stream output into the panel, classified by
MSBuild's own `: error` / `: warning` shape. Stop kills the whole process tree, because `dotnet run`
launches your application as a grandchild and killing only the direct child leaves it running.

The run target is passed as `UNITREE_RUN_TARGET` rather than by rewriting `appsettings.json`. Editing a
file you can see, behind your back, is the kind of thing that makes a tool feel untrustworthy.

**Deploy** publishes an embedded project self-contained for `linux-arm64`, copies it over SFTP, restores
the executable bit that SFTP drops, and optionally installs a systemd unit with `Restart=always`.

> Deployment has never been run against a real robot. See `PROGRESS.md`.

## Platform note

The shell is WPF and therefore Windows-only, and needs the WebView2 runtime that ships with Windows 11.
Everything underneath — templates, scaffolding, build orchestration, Jack, the SSH deploy — lives in
`src/Unitree.Net.Wizard.Core`, which is plain `net10.0` and has no UI dependency.

Monaco is vendored under `wwwroot/lib/monaco`, about 11 MB. An editor that needs a CDN to open a file
is not an editor.

---

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
