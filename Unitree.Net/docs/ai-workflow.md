# AI workflows

`AiWorkflowEngine` puts a natural-language interface in front of a robot, built on Semantic Kernel with
a choice of four providers.

It is a **supervisory** interface. Language-model latency is measured in seconds; nothing here belongs
anywhere near a balance controller.

## Providers

```json
{
  "Unitree:Ai": {
    "Provider": "Ollama",
    "ModelId": "",
    "ApiKey": "",
    "Endpoint": "",
    "Temperature": 0.2,
    "MaxTokens": 1024,
    "ExposeMotionFunctions": false,
    "AllowAutomaticFunctionCalling": false
  }
}
```

| Provider | Default model | Needs a key | Notes |
|---|---|---|---|
| `OpenAI` | `gpt-4o-mini` | Yes | `Endpoint` also covers Azure OpenAI and OpenAI-compatible gateways |
| `Anthropic` | `claude-sonnet-4-5` | Yes | Reaches the kernel through `IChatClient` |
| `Gemini` | `gemini-2.0-flash` | Yes | |
| `Ollama` | `llama3.2` | No | Defaults to `http://localhost:11434` — fully local |

Ollama is the default because it runs on the robot host with no API key and no data leaving the machine,
which suits a field deployment.

API keys belong in user secrets or environment variables:

```bash
export UNITREE_Unitree__Ai__ApiKey="sk-…"
```

Three of the four providers have first-party Semantic Kernel connectors. Anthropic does not have a
stable one, so its client is adapted through `IChatClient` — the `Microsoft.Extensions.AI` abstraction
Semantic Kernel also speaks. Both routes produce an `IChatCompletionService`, so nothing downstream can
tell which was used.

## Two separate opt-ins

Motion is gated twice, deliberately.

| Setting | Off (default) | On |
|---|---|---|
| `ExposeMotionFunctions` | The model cannot see motion functions at all | They are registered |
| `AllowAutomaticFunctionCalling` | The model may *propose* a call; Semantic Kernel will not execute it | Calls execute |

With both off, the model reads telemetry and explains what it sees — a genuinely useful diagnostic
assistant with no physical risk:

```
you> why is the robot refusing to walk?
robot> The robot is connected and upright, but the battery is at 12%, which is below the
       configured 15% minimum. Motion commands are being refused for that reason. Motor
       temperatures are normal at 43 °C and all four feet are loaded.
```

## Functions

**Always available** (`RobotTelemetryPlugin`, read-only):

| Function | Returns |
|---|---|
| `get_robot_status` | Connection, battery, orientation, temperature, foot contact |
| `get_battery_status` | Charge, voltage, current, cycles, cell imbalance, estimated runtime |
| `get_position` | Odometry position and heading, with a drift caveat |
| `check_ready_to_move` | Whether motion is currently safe, with the specific reason if not |

**Gated** (`RobotMotionPlugin`): `stand_up`, `stand_down`, `move_forward`, `turn`, `stop`,
`recover_stand`, `greet`.

Every motion function re-checks readiness before acting, and refuses with an explanation rather than
throwing. A model may call functions in any order it likes — including asking the robot to walk without
ever checking whether it is upright — so that check cannot live only in the prompt.

Motion functions are also bounded independently of the prompt: 10 m per `move_forward`, 360° per `turn`.

## Usage

```csharp
builder.Services.AddUnitreeAi(builder.Configuration);

var engine = provider.GetRequiredService<AiWorkflowEngine>();

string reply = await engine.AskAsync("How is the battery holding up?");

await foreach (string chunk in engine.AskStreamingAsync("Walk forward two metres"))
{
    Console.Write(chunk);
}
```

Or interactively:

```bash
dotnet run --project apps/Unitree.Net.Cli -- ai
```

Turns are serialised; concurrent calls would interleave writes into the shared history. History is
trimmed to `MaxHistoryTurns`, always preserving index 0 — that holds the system prompt, and losing it
would silently remove every safety instruction the model was given.

## Adding your own functions

```csharp
public sealed class InspectionPlugin(LidarClient lidar)
{
    [KernelFunction("check_clearance")]
    [Description("Reports the distance to the nearest obstacle directly ahead, in metres.")]
    public string CheckClearance() =>
        lidar.GetForwardClearance() is { } m
            ? $"Nearest obstacle ahead: {m:0.00} m."
            : "No LiDAR returns in the forward sector.";
}

engine.Kernel.Plugins.AddFromObject(new InspectionPlugin(lidar), "Inspection");
```

Two things make the difference between a function a model uses well and one it misuses:

- **Describe it for the model, not for a developer.** The `[Description]` is the entire specification
  the model sees.
- **Return prose, not status codes.** `"Refused: battery at 12%, below the 15% minimum"` gives the model
  something to explain; `false` does not.

## What this is not for

- **Control loops.** Seconds of latency.
- **Safety decisions.** The safety envelope is deterministic code and stays that way.
- **Unsupervised operation.** With motion enabled, keep a human watching and a stop within reach.
