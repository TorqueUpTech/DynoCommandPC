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
Open **⚙ Settings…** and set the **EDM Bluetooth address** (12 hex digits, e.g.
`0012F304962C`). The app connects straight to that MAC over an unauthenticated
RFCOMM link — the EDM does not support pairing, so no bonding step is needed.
Settings persist to `settings.json` beside the executable (portable).

## Project layout
| Folder | Contents |
|--------|----------|
| `Protocol/` | Packet layout, stream framing, CRC-16/MODBUS, decode / encode |
| `Transport/` | Bluetooth RFCOMM/SPP transport (raw Winsock `AF_BTH`) |
| `ViewModel/` | MVVM view-model, value converters, relay commands |
| `Settings/` | Persisted application settings |
| `Logging/` | Comms-event file logger |
