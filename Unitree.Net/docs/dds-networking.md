# DDS networking

Read this when the robot does not appear. Almost every "it won't connect" report resolves to one of the
three causes below, and they are distinguishable in about a minute.

Start here:

```bash
dotnet run --project apps/Unitree.Net.Cli -- diagnose
```

It needs no robot. It reports the resolved configuration, whether the native library loaded, and every
network interface with its multicast capability.

## Cause 1 — the wrong network interface

**By far the most common.** With more than one adapter up, DDS picks one on its own, and on a laptop
with both a robot cable and Wi-Fi that choice is effectively arbitrary. When it picks the corporate LAN,
discovery never completes — and nothing reports an error, because at every layer the transport started
successfully.

Set it explicitly:

```json
{ "Unitree": { "NetworkInterface": "eth0" } }
```

`diagnose` lists the valid names. The SDK logs a warning whenever it has to guess.

## Cause 2 — multicast is filtered

DDS discovery is multicast. Corporate networks, most VPNs, and many managed switches drop it by default.

Symptoms: the transport starts cleanly, no error appears anywhere, and `ConnectAsync` times out after
its configured window.

Options, in order of preference:

1. **Connect the robot directly** to the host with an Ethernet cable. This is what Unitree assume, and
   it removes the whole class of problem.
2. **Put the robot on a dedicated VLAN** with IGMP snooping configured to permit the group.
3. **Ask your network team to permit** the group and port on the robot's segment.

A quick check that multicast works at all on the segment, independent of this SDK:

```bash
# Host A
dotnet run --project samples/Unitree.Net.Samples.VirtualRobot -- --interface eth0
# Host B
dotnet run --project apps/Unitree.Net.Cli -- status --Unitree:NetworkInterface=eth0
```

If that works between two hosts but the robot does not appear, the problem is the robot link, not the
network.

## Cause 3 — the native library is missing

`ManagedMulticast` is **not RTPS**. It carries CDR payloads in Unitree.Net's own framing, so it can
reach another Unitree.Net process — a simulator, a bridge, a replay tool — but never robot firmware.

Real hardware needs `CycloneNative`, which needs `unitree_net_native`. See
[`native/README.md`](../native/README.md). `diagnose` reports whether the library loaded.

## Choosing a transport

| Situation | Transport |
|---|---|
| Real robot | `CycloneNative` |
| Developing against the virtual robot | `ManagedMulticast` |
| Two hosts sharing simulated telemetry | `ManagedMulticast` |
| Unit tests | `Loopback` |

## Configuration reference

```json
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "CycloneNative",
    "NetworkInterface": "eth0",
    "DomainId": 0,
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447,
    "MulticastTimeToLive": 1,
    "ConnectTimeout": "00:00:10",
    "RequestTimeout": "00:00:05",
    "TelemetryQueueCapacity": 256
  }
}
```

`MulticastAddress`, `MulticastPort` and `MulticastTimeToLive` apply only to `ManagedMulticast`. A TTL of
1 keeps traffic on the local segment; raise it only if you understand the routing consequences.

## QoS, and why "reliable" breaks things

The native shim applies different QoS by topic class, matching what the firmware advertises:

- **Streaming topics** (`rt/lowcmd`, `rt/lowstate`, `rt/sportmodestate`) — best-effort, keep-last-1,
  volatile.
- **Service topics** (`.../request`, `.../response`) — reliable, keep-last-16, volatile.

Requesting *reliable* on a streaming topic leaves the reader unmatched: the link looks connected and
never delivers a single sample. It is a genuinely confusing failure, and it is why the QoS is not
configurable per topic.

## Other things worth knowing

**One SDK instance per robot.** Unitree firmware does not arbitrate between controlling hosts. A second
connected process produces undefined behaviour rather than a clean error.

**C++ and Python binding tags do not line up.** Unitree's release tags differ between the C++ SDK and
its Python bindings; they must be paired manually. A matching tag does not imply a matching ABI.

**Point clouds exceed the datagram limit.** A LiDAR frame runs to several hundred kilobytes, well over
the 64 KB UDP limit, so `ManagedMulticast` cannot carry it. LiDAR requires the native transport, which
fragments.

## Diagnosing from telemetry counters

Every subscriber exposes counters that localise a problem quickly:

| Counter | Rising means |
|---|---|
| `ReceivedCount` stays 0 | Nothing arriving — interface or multicast |
| `MalformedCount` rising | Something else is publishing on this topic name |
| `DroppedCount` rising | Your consumer cannot keep up with the robot |
| `MotorState[i].Lost` rising | A failing motor cable, not a network problem |
