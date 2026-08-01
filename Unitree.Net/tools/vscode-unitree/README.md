# Unitree.Net Robot Tools for VS Code

Create, run, debug and deploy Unitree robot applications without leaving the editor, with live robot
status and logs in the sidebar.

## What it gives you

| Where | What |
|---|---|
| Activity bar | **Robot status** — connection, battery, motor temperature, ground contact, pose, odometry, message counts. **Templates** — the 16 project templates, grouped by kind. |
| Status bar | The robot, its battery, and **where Run will send commands**. Turns amber when the target is real hardware. |
| Output panel | A `Unitree` log with levels and timestamps: connection changes, tool output, deployment progress. |
| Problems panel | Build errors, via a problem matcher, so they are clickable. |

## Commands

All under the **Unitree:** prefix in the command palette.

| Command | What it does |
|---|---|
| New Robot Project… | Pick a template or a blank kind, name it, choose a folder, open it |
| Connect / Disconnect | Start and stop the live telemetry stream |
| Start / Stop Simulator | Runs the headless virtual robot so there is something to connect to |
| Build | `dotnet build` as a task, with errors in the Problems panel |
| Run | Runs in a terminal, with the run target passed through the environment |
| Debug | Launches the .NET debugger (needs the C# extension) |
| Deploy to Robot… | Publishes for `linux-arm64` and copies it over SSH |
| Run Diagnostics | Checks transport, native library and interfaces. Needs no robot |
| Show Logs | Reveals the output channel |
| Set Run Target | Switches between the simulator and real hardware |

## Getting started

1. Open the Unitree.Net repository as a folder, or set `unitree.sdkRoot`.
2. **Unitree: Start Simulator**, then **Unitree: Connect**.
3. **Unitree: New Robot Project…** and pick a template.
4. **Unitree: Run**.

No robot is needed for any of that.

If nothing connects, run **Unitree: Run Diagnostics** first. It needs no robot and distinguishes the
three usual causes: the wrong network interface, a missing native library, and filtered multicast.

## How it works

Everything goes through the `unitree` CLI rather than being reimplemented in TypeScript:

```
unitree templates          # the template catalogue, as JSON
unitree new --name … --template …
unitree probe              # one telemetry snapshot, as JSON
unitree stream --interval  # newline-delimited JSON, one object per sample
unitree deploy --project … --host …
unitree diagnose
```

That is deliberate. A TypeScript copy of the template catalogue or the telemetry decoding would drift
from the C# the first time either changed, and the drift would be silent.

By default the CLI runs from source with `dotnet run`, which is slower to start but always matches the
repository you are looking at. Point `unitree.cliPath` at a published executable if you would rather it
were fast.

## Settings

| Setting | Purpose |
|---|---|
| `unitree.sdkRoot` | Repository path. Found automatically if the workspace is inside it |
| `unitree.cliPath` | A published `unitree` executable, instead of running from source |
| `unitree.model` | Go2, Go2W, B2, B2W, G1, H1, H12, R1 |
| `unitree.transport` | `ManagedMulticast` reaches the simulator; real hardware needs `CycloneNative` |
| `unitree.multicastAddress`, `unitree.multicastPort` | Where the robot publishes |
| `unitree.networkInterface` | The single most common reason nothing connects |
| `unitree.runTarget` | Simulator or Robot |
| `unitree.deploy.*` | Host, port, user, private key, remote directory, systemd |

A private key is preferred over a password for deployment — it is the only option that does not put a
robot password in a settings file. Without one, the password is asked for each time and never stored.

## Safety

Setting the run target to **Robot** turns the status bar amber and makes Run ask for confirmation.
That is not decoration: these machines weigh tens of kilograms and move in a real space. Read
`docs/safety.md` before low-level control.

Nothing in this SDK has been run against real hardware, deployment included.

## Building it

```bash
cd tools/vscode-unitree
npm install
npm run compile
code --extensionDevelopmentPath=.
```

---

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
