# DynoCommandPC

A Windows (WPF / .NET 8) re-implementation of the MC75A Bluetooth dyno handheld —
a "PC test console" that connects directly to the dyno's EDM module over classic
Bluetooth (SPP / RFCOMM) and drives the same command + telemetry protocol.

## Features
- Direct Bluetooth connection to the EDM by MAC address (no pairing required).
- Live read-outs: SET / ACTUAL speed, roller revs/s, ramp rate, brake, hands-free,
  bed position, transmission, 4WD mode, device-online status.
- Modes: No-Load, Speed, Load, Ramp Up / Hold / Down, Brake, Hands-free, Set Bed.
- Speed safety governor (±10 km/h leash), brake interlock, 4WD balance control.
- Shootout lock-out (PC-controlled ramp rate) display.
- Diagnostics: packet log, "packet types seen" inspector, raw-RX hex capture,
  comms-loss tally, optional timestamped comms file logging.

## Requirements
- Windows 10 / 11 with a Bluetooth radio.
- .NET 8 SDK to build, or the .NET 8 Desktop Runtime to run a published build.
- `libs/DynoDotNet.dll` — **not in this repo.** It is Dyno Dynamics' proprietary
  DynoServer client library and is not ours to redistribute, so `libs/` is
  gitignored. Copy the DLL from a DigitalDyno install (or the CAN feeder's
  `libs/`) into `libs/` before building; the `ds` transport references it.

## Build & run
```powershell
dotnet build
dotnet run
```
Publish a framework-dependent build:
```powershell
dotnet publish -c Release
```

## Configuration
Open **⚙ Settings…** and set the **EDM address**. That one field picks the transport:

| Value | Link |
|-------|------|
| `0012F304962C` (12 hex digits) | Classic Bluetooth RFCOMM straight to that EDM MAC. Unauthenticated — the EDM does not support pairing, so there is no bonding step. |
| `127.0.0.1:51730` (`host:port`) | Transparent TCP carrying the same byte stream — for a simulator. |
| `ds` or `ds:<server-ip>` | Join the DynoServer network as the handheld (loopback, or that server's IP), the same way the CAN feeder attaches. Needs `libs/DynoDotNet.dll`. |

Settings persist to `settings.json` beside the executable (portable).

## Project layout
| Folder | Contents |
|--------|----------|
| `Protocol/` | Packet layout, stream framing, CRC-16/MODBUS, decode / encode |
| `Transport/` | Bluetooth RFCOMM/SPP (raw Winsock `AF_BTH`), TCP, and DynoServer transports |
| `ViewModel/` | MVVM view-model, value converters, relay commands |
| `Settings/` | Persisted application settings |
| `Logging/` | Comms-event file logger |
