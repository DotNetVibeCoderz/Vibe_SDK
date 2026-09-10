# Roadmap

Where this goes next, in the order it should be done. [PROGRESS.md](PROGRESS.md) says what is true
today; this says what should be true later and why.

---

## 0.2 — Meet the hardware

Everything else is speculation until an SDK written from documentation has been held against a
robot. This release is one activity: plug each robot in and correct what is wrong.

- [ ] **Reachy Mini.** Confirm the WebSocket path and the command envelope. Both are marked
      *assumed* in PROGRESS.md, and both fail silently if wrong — the daemon drops an unknown
      variant without a word, so the symptom is a robot that ignores you.
- [ ] **MicroDuck.** Capture `robotctl monitor --json` and correct the joint table in
      `RobotCatalog.MicroDuck`; confirm the `robot.*` method signatures.
- [ ] **Reachy 2.** The protos are Pollen's own, so this should be the easy one. Confirm the port
      and part discovery, then check that a Cartesian goto behaves as the queue model predicts.
- [ ] Record what each robot actually reports at rest and turn it into fixtures, so a regression in
      the wire format is caught by a test rather than by a robot.
- [ ] Replace the *assumed* rows in PROGRESS.md with measurements.

## 0.3 — Perception

The SDK can move all three robots and see nothing. That is the largest gap.

- [ ] **Reachy Mini camera and audio.** The daemon serves both over GStreamer locally and WebRTC
      remotely. Start with the local path — it is the one a robot-side application uses, and it does
      not need a WebRTC stack.
- [ ] **MicroDuck time-of-flight.** Already in the transport interface; needs the daemon's real
      stream rather than the simulated wall.
- [ ] **Reachy 2 cameras** through `video.proto`.
- [ ] A vision pipeline behind `GetTrackedFaceAsync`, so head tracking is more than plumbing.

## 0.4 — Recorded moves, properly

- [ ] Daemon-side `playMove`: gzip+base64 upload over the data channel, motion and audio on one
      clock. The current implementation streams frames from the client, which is fine on a wired
      Lite and visibly rough over Wi-Fi.
- [ ] Audio attachment with the measured `audioLeadMs`, and a validator that rejects anything that
      is not 16 kHz mono 16-bit PCM before it reaches the robot — a format mismatch plays silently
      wrong and reports nothing.
- [ ] Load the Hugging Face emotions dataset so the built-in expressions are Pollen's rather than
      this project's approximations.

## 0.5 — Reachy 2 parity

- [ ] An `IReachy2Transport` so Reachy 2 gets a simulation transport like the other two, and its
      templates get a `--sim` switch. Today the simulated Reachy 2 is driven directly, which is the
      one place the "same code, either target" promise does not hold.
- [ ] Mobile base LIDAR through `mobile_base_lidar.proto`.
- [ ] `StreamReachyState` wired into the simulator's status panel.

## 0.6 — Ship it

- [ ] Move the tree to `C:\experiment\VibeCoding\Vibe_SDK` and commit it — packaging from a
      scratch directory produces packages with no commit behind them.
- [ ] Reserve the `Gravicode.*` ID prefix on nuget.org before the first push.
- [ ] Publish to NuGet from a tag, with the version validated against the tag before the push.
      Procedure: [docs/publishing.md](docs/publishing.md).
- [ ] A CI workflow that builds, tests, and runs `TemplateCheck` — the last one especially, because
      template drift is invisible until a user presses Build.
- [ ] Sign the packages.
- [ ] `dotnet new` templates, so `pollen new` has a first-party equivalent.

---

## Ideas worth considering, not yet committed

**A VS Code extension.** The wizard is a good editor for a robot project and a worse editor than VS
Code for everything else. An extension backed by the CLI would let people stay where they are. The
cost is a second UI to keep in step with the SDK.

**Behaviour trees.** The templates all express behaviour as straight-line async code, which is right
for a demo and wrong for anything that has to react while it acts. A small behaviour-tree layer over
the clients would fit the fall-recovery and obstacle-avoidance cases naturally.

**Policy training loop.** `OnnxPolicyRunner` runs a policy; nothing here trains one. TorchSharp is
already referenced. The interesting version of this is training against the simulator and exporting
something `robotctl policy load` accepts.

**Multi-robot.** Nothing in the SDK assumes one robot, but nothing tests more than one either. A
duck and a Reachy Mini in the same application, on one clock, would be a good forcing function.

**Recorded-move editor.** The wizard can generate code; it cannot author motion. A timeline editor
over `RecordedMove`, using the simulator as the preview, is the obvious missing tool.

---

## Things deliberately not planned

**A physics simulation.** The models here are kinematic and say so. Making them dynamic would invite
people to trust them for questions they still could not answer — whether a motion is stable, whether
a grasp will hold. MuJoCo already exists and Pollen already trains against it.

**Reimplementing the daemons.** This is a client SDK. The temptation to reimplement enough of
robotd to run a duck without robotd should be resisted; it doubles the surface that has to track
Pollen's releases.

**A second UI framework.** Avalonia for the shells, Blazor only for the 3D viewport where a browser
is genuinely the right tool. Adding a third would mean a third theme to keep in step.
