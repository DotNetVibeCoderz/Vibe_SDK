# The Robot Wizard

A code editor for robot applications, with **Jack The Code Bender** in a panel beside it.

![The wizard with a project open](images/wizard-editor.png)

```bash
dotnet run --project apps/PollenRobotics.Net.Wizard

# Or open a project straight away.
dotnet run --project apps/PollenRobotics.Net.Wizard -- --project C:\path\to\MyRobotApp
```

## The layout

Files on the left, editor in the middle, Jack on the right, log along the bottom, status bar under
that. Menus for Project, Edit, View, Run and Help.

The editor, the build pipeline and the assistant are three separate things sharing **one log**. That
sharing is the point: a build error, robot telemetry and Jack's own output land in the same panel in
the order they happened, which is the only way to see that the failed build is the one Jack just
caused.

## New Project

Blank, or from one of twenty templates. Both are one list and one decision rather than two dialogs.

A template produces a complete project that already builds and runs - that is what makes it worth
choosing over blank. Blank still gives you connection, cancellation and shutdown wired up.

```
Robot          Templates
Reachy Mini    hello · emotions · face-follow · idle-life · teleop · record-replay
               look-at · jack-companion
MicroDuck      hello · gamepad · obstacle-avoid · trick-show · patrol · policy-runner
Reachy 2       hello · pick-place · mobile-patrol · head-gaze · bimanual · telemetry
```

Every Reachy Mini and MicroDuck template takes `--sim`, so a fresh project runs against the
simulator with no hardware.

### Keeping templates honest

Template code is written by hand against the SDK's public surface and **nothing recompiles it by
accident**. A renamed method leaves twenty templates that scaffold cleanly, look right in the
editor, and fail the moment a user presses Build.

```bash
dotnet run --project tools/PollenRobotics.Net.TemplateCheck
```

That scaffolds every template and compiles it. Run it after any change to the SDK's public API. The
first time it ran, four of twenty failed.

## Build, Run, Deploy

```
Build          dotnet build, with errors parsed and clickable
Run            builds first, then runs against the simulator or the robot
Stop           kills the whole process tree
Deploy         publishes self-contained for linux-arm64 and copies with scp
```

Build failures jump the editor to the first error. Reading a build failure means finding the line,
and the wizard already knows where it is.

Run shells out to `dotnet` rather than hosting MSBuild in-process. The wizard is itself a .NET
application, and loading a second MSBuild into the same process is a reliable way to end up with two
conflicting versions of the same assembly. Output is streamed rather than collected, so the log
fills as the build runs - a build that prints nothing for twenty seconds and then everything at once
reads as a hang.

Stop kills the entire process tree. `dotnet run` launches the program as a child, and killing only
the launcher leaves the program running and still holding the robot.

Deploy publishes for `linux-arm64` by default. The robots are 64-bit ARM Linux; publishing for the
development machine's architecture produces a binary that copies across fine and then refuses to
start, with an error that does not mention architecture.

## Jack The Code Bender

![Jack answering](images/wizard-jack.png)

*Jack looked the API up before answering. `CommandedHeadPose`, `GotoTargetAsync` and
`waitForCompletion` are real members of this SDK.*

### Providers

Semantic Kernel underneath, with **OpenAI, Anthropic, Gemini or Ollama** - and anything
OpenAI-compatible through the endpoint override.

Anthropic has no first-party Semantic Kernel connector, so it comes in through
`Microsoft.Extensions.AI`: `Anthropic.SDK` exposes an `IChatClient` and Semantic Kernel consumes it
as a chat completion service. That route supports streaming and tool calls, so all four behave the
same from the caller's side.

### Configuration

`appsettings.json` beside the executable, or the settings dialog, or environment variables
(`Ai__Provider`, `Ai__ApiKey`, `Ai__Endpoint`, `Ai__Model`).

```json
{
  "Ai": {
    "Provider": "OpenAI",
    "Model": "",
    "ApiKey": "",
    "Endpoint": "",
    "Temperature": 0.3,
    "MaxTokens": 4096,
    "EnableFunctionCalling": true,
    "TavilyApiKey": "",
    "SystemPrompt": "",
    "SystemPromptIsCustom": false
  }
}
```

**`SystemPromptIsCustom` is not decoration.** A setting whose default is meaningful must not be
written back verbatim on save, or the file freezes whatever the built-in persona said that day and
every later improvement to it silently never reaches anyone who has run the app once. With the flag
false or absent, the code's default wins.

### The functions Jack can call

The SDK reference functions are the ones that matter most:

| Function | What it does |
|---|---|
| `list_sdk_types` | Public types, filterable |
| `describe_sdk_type` | Constructors, properties, methods, with real signatures |
| `search_sdk` | Find a member by name when you know the verb but not the noun |
| `get_robot_joints` | A robot's joints and limits, in wire order |
| `get_safety_rules` | The bring-up and limit rules for a robot |
| `generate_project_file`, `generate_program_skeleton` | Scaffolding |
| `search_web`, `read_web_page`, `read_file_from_url` | The web (search needs a Tavily key) |
| `calculate`, `degrees_to_radians`, `get_current_date` | The things models are bad at |

The reference functions reflect over the **actually-loaded assemblies**, so what they report is what
will compile. That is the entire point: telling an assistant to "check the API before using it" does
not work on its own - it skips the check precisely when it feels confident, which is when it is most
likely to be quoting a signature from some other robotics SDK. A tool that answers with the real
signature does work, because the answer arrives in the conversation whether or not the model thought
it needed one.

Web search is only registered when a Tavily key is configured. Advertising a tool that fails on
every call trains the model to keep trying it.

### Sessions and attachments

Multiple sessions, create, delete, reset, persisted to disk so a conversation survives a restart.

Images and documents take different routes, deliberately. An image is uploaded and its URL becomes
image content the model can actually see; a document is uploaded and its link is appended to the
message text for the model to fetch with `read_file_from_url`. Sending a 40-page PDF as inline
content would blow the context window; sending an image as a link would mean the model never looks
at it.

A local file path is skipped rather than sent, because the model cannot open one and pretending
otherwise produces an answer about an image it never saw.

### Rendering

Markdown is rendered into Avalonia controls, not a WebView. The chat panel sits beside a code editor
in a desktop tool; putting a browser in it would mean a second theme to keep in step, a second font
stack, and a process boundary between the assistant and the editor it is meant to be driving.

Code blocks are monospaced, **horizontally scrollable rather than wrapped**, and carry Copy and
Insert. Wrapping a generated C# file at panel width makes every line after the first look like a
continuation, and a reader cannot tell a wrapped line from a real one. Insert drops the block at the
caret.

Tables, lists, quotes, headings and inline code all render, because a model told it may use Markdown
will use all of it, and an unrendered table is worse than no table.

### Starter prompts

An empty chat box is the hardest part of a tool like this. Twenty-seven example prompts ship, in
English and Indonesian, drawn from `PromptLibrary` and filtered to the robot the open project
targets. Four are shown at random on an empty thread - a fixed set trains people to ignore the panel
after the first day.

## Editing

AvaloniaEdit underneath: line numbers, find, replace, go to line, word wrap toggle, undo and redo.
Ctrl+J shows and hides Jack.

The chat panel can be hidden entirely. It is a tool, not a tenant.
