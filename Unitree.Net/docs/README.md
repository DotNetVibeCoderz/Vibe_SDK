# Documentation

## Start here

| Document | Read it when |
|---|---|
| [Getting started](getting-started.md) | First time — covers running with and without a robot |
| [DDS networking](dds-networking.md) | **Nothing connects.** Almost always one of three causes |
| [Safety](safety.md) | **Before** any low-level control. Non-optional reading |

## Tools

| Document | Covers |
|---|---|
| [Simulator](simulator.md) | 3D robot simulator — eight platforms, real telemetry, no hardware |
| [Robot Wizard](wizard.md) | Code editor, 16 project templates, and Jack The Code Bender |
| [VS Code extension](vscode-extension.md) | Create, run, debug and deploy from the editor, with live status and logs |

## Reference

| Document | Covers |
|---|---|
| [Architecture](architecture.md) | Layering, the transport seam, why the codec is hand-written, backpressure |
| [Low-level control](low-level-control.md) | Direct joint commands, trajectories, learned policies |
| [Navigation](navigation.md) | Waypoints, odometry drift, what this is not |
| [AI workflows](ai-workflow.md) | Semantic Kernel, the four providers, motion gating |
| [ROS 2 bridge](ros2-bridge.md) | Publishing telemetry, accepting `cmd_vel`, frame conventions |
| [Native shim](../native/README.md) | Building `unitree_net_native` for real hardware |

## Project status

- [PLAN.md](../PLAN.md) — roadmap by phase
- [PROGRESS.md](../PROGRESS.md) — what is verified, how it was verified, and what is not

`PROGRESS.md` deliberately separates "builds and passes tests" from "validated on hardware". Nothing in
this repository has been run against a real robot yet.

## Quick answers

**The robot doesn't connect.** Run `unitree diagnose`. It needs no robot and distinguishes a wrong
network interface, a missing native library, and filtered multicast.

**Low-level commands do nothing, no error.** The sport service still owns the motors. Use
`BeginLowLevelSessionAsync`, which releases it first.

**Velocity commands are ignored.** The robot must be in balanced standing — call `BalanceStandAsync`
after `StandUpAsync`.

**The robot stops on its own after half a second.** That is the robot's own command expiry, working as
intended. Use `StartVelocityStream`, which resends the command at 20 Hz — set `Command` once and it
keeps going.

**Can I develop without a robot?** Yes, entirely. Start `apps/Unitree.Net.Simulator` for the 3D
simulator, or `samples/Unitree.Net.Samples.VirtualRobot` for the headless one, and point anything at it.

**How do I start a new robot application?** `apps/Unitree.Net.Wizard` — pick a template, press Run.
Every template works against the simulator without edits. If you would rather stay in VS Code, install
the [extension](vscode-extension.md) and use **Unitree: New Robot Project…**.

---

Unitree.Net tooling dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
