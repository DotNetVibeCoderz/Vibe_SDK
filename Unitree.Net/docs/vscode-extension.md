# VS Code extension

`tools/vscode-unitree` puts the robot workflow inside the editor: create a project, connect to a robot
or the simulator, run, debug, deploy — with live status and logs in the sidebar.

```bash
cd tools/vscode-unitree
npm install
npm run compile
code --extensionDevelopmentPath=.
```

Then, with the Unitree.Net repository open as a folder:

1. **Unitree: Start Simulator**
2. **Unitree: Connect**
3. **Unitree: New Robot Project…**
4. **Unitree: Run**

No robot is needed for any of it.

## What you get

| Where | What |
|---|---|
| Activity bar → **Robot status** | Connection, model, transport, battery with estimated runtime, pack voltage and current, hottest motor, ground contact, speed, body height, pose, odometry, message counts |
| Activity bar → **Templates** | All 16 project templates, grouped by kind. Click one to scaffold from it |
| Status bar | The robot and its battery, plus **where Run will send commands** |
| Output → `Unitree` | A levelled, timestamped log: connection changes, tool output, deployment progress |
| Problems | Build errors, through a problem matcher, so they are clickable |

The status bar turns amber and reads **REAL ROBOT** when the run target is hardware, and Run asks for
confirmation before it starts. That is not decoration — it is the difference between a mistake costing
nothing and a mistake moving fifteen kilograms of machine.

## Commands

| Command | What it does |
|---|---|
| New Robot Project… | Template or blank, name, folder, then opens it |
| Connect / Disconnect | Starts and stops the live telemetry stream |
| Start / Stop Simulator | Runs the headless virtual robot |
| Build | `dotnet build` as a task, errors in the Problems panel |
| Run | Runs in a terminal with the run target in the environment |
| Debug | Launches the .NET debugger — needs the C# extension |
| Deploy to Robot… | Publishes for `linux-arm64` and copies it over SSH |
| Run Diagnostics | Transport, native library and interfaces. Needs no robot |
| Set Run Target | Simulator or real hardware |
| Show Logs | Reveals the output channel |
| Open Robot Wizard | Launches the desktop wizard |

## How it talks to the SDK

Through the `unitree` CLI, which gained a machine-readable surface for exactly this:

| Command | Output |
|---|---|
| `unitree templates` | The template catalogue as JSON |
| `unitree new --name … --output … [--template … \| --kind …]` | The created project as JSON |
| `unitree probe` | One telemetry snapshot as JSON |
| `unitree stream --interval 500` | Newline-delimited JSON, one object per sample |
| `unitree deploy --project … --host …` | Publish and copy; progress on stderr |

Nothing is reimplemented in TypeScript. A copy of the template catalogue or the telemetry decoding
would drift from the C# the first time either changed, and the drift would be silent.

`probe` and `stream` disable console logging entirely, because the logger writes to stdout and one
info line is enough to make the output unparsable. That is not hypothetical — it is what happened the
first time the extension called `probe`.

### Finding the CLI

Three routes, in order: a configured `unitree.cliPath`, an already-built
`apps/Unitree.Net.Cli/bin/{Release,Debug}/net10.0/unitree.dll`, and finally `dotnet run` from source.

The middle one matters. `dotnet run` restores and builds on every invocation, and two concurrent
invocations — the telemetry stream and a template listing — block on the same MSBuild lock. The
Templates view stayed empty for a minute and then timed out, which reads as a broken extension rather
than a slow one. Build the CLI once and it is instant:

```bash
dotnet build apps/Unitree.Net.Cli -c Release
```

## Settings

| Setting | Purpose |
|---|---|
| `unitree.sdkRoot` | Repository path. Found by walking up from the workspace if unset |
| `unitree.cliPath` | A published `unitree`, instead of running from source |
| `unitree.model` | Go2, Go2W, B2, B2W, G1, H1, H12, R1 |
| `unitree.transport` | `ManagedMulticast` reaches the simulator; hardware needs `CycloneNative` |
| `unitree.multicastAddress` / `Port` | Where the robot publishes |
| `unitree.networkInterface` | The most common reason nothing connects |
| `unitree.pollIntervalMs` | Status panel refresh rate |
| `unitree.runTarget` | Simulator or Robot |
| `unitree.deploy.*` | Host, port, user, private key, remote directory, systemd |

A private key is preferred over a password for deployment: it is the only option that does not put a
robot password in a settings file. Without one the password is asked for each time and never stored.

## Packaging

```bash
npx vsce package --no-dependencies
code --install-extension unitree-net-0.1.0.vsix
```

## Limitations

- Deployment has never been run against a real robot. See `PROGRESS.md`.
- Debugging needs the C# extension (`ms-dotnettools.csharp`); the command offers to install it.
- **Start Simulator** runs the headless virtual robot rather than the 3D simulator, so the command
  works on any platform. Use **Open Robot Wizard** or run the simulator directly for the 3D view.

---

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
