# GeekModeNoVantage

> [!CAUTION]
> **Personal, vibe-coded prototype.** This application was built for one person and has only been tested on a **Lenovo IdeaPad Pro 5 14IAH10, machine type 83JK, BIOS QLCN36WW**. It directly sends commands to Lenovo's power-management driver and changes undocumented SmartSense settings. Do not assume it supports another model, firmware version, or Lenovo software stack. Incorrect low-level power controls can cause instability, unexpected thermal behaviour, or data loss. Review the source, keep Lenovo's original drivers installed, and use it entirely at your own risk.

This is not an official Lenovo product and is not affiliated with or endorsed by Lenovo.

A small, local-only Windows tray utility created specifically for the Lenovo IdeaPad Pro 5 14IAH10 (83JK).

## Implemented

- Reads and switches Lenovo Intelligent Cooling, Battery Saving, Performance, and Geek modes directly through the installed `EnergyDrv` driver.
- Checks the charger before enabling Geek Mode.
- Reads Lenovo SmartSense state through its local `HumanPresenceDetectionEndpoint`
  RPC. On the tested IdeaPad, Lenovo's own RPC setters return Win32 error 87, so
  writes use the confirmed SmartSense registry contract and restart the service:
  - Zero Touch Login
  - Zero Touch Lock
  - Zero Touch Video Playback
  - Automatic or Near (0.42 m), Middle (0.6 m), and Far (1.2 m) distance sensitivity
  - Fast, Medium, and Slow automatic-lock delay
  - Camera-assisted lock when advertised by the installed SmartSense stack
  - Automatic leave-distance adjustment when advertised
- Reports the HPD interface version, capability mask, and sensor type (for example VL53L1).
- Shows whether the ToF/SmartSense stack is online.
- With the optional read-only Presence Bridge service installed, shows Lenovo's
  live `Present`, `Absent`, or `Unknown` lock decision in the tray icon.
- Shows a compact count of running Lenovo services.
- Uses no network access, analytics, advertising, account, or subscriptions.

## Still being decoded

SmartSense's live HPD feed is proprietary. Until it is decoded, the tray indicator says whether the ToF stack is online; it does not claim a person is detected.

The signed Lenovo EnergyDrv, ACPI, SmartSense, ToF/camera, and hotkey drivers remain installed. Lenovo Vantage is not involved in normal control once compatibility testing is complete.

SmartSense RPC is provided by the separately distributed AGPL-3.0
[`vantage-cli`](https://github.com/cyjin-yl/vantage-cli) backend. See
`THIRD_PARTY_NOTICES.md` and the bundled license for attribution and source.

The DYTC/EnergyDrv protocol implementation was validated against the GPL-licensed [Lenovo Legion Toolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit) implementation.

## Requirements

- Windows 11
- .NET 8 Desktop Runtime
- Lenovo IdeaPad Pro 5 14IAH10 (83JK); no other model is currently supported
- Lenovo `EnergyDrv`, ACPI, SmartSense and hotkey drivers

Keep Lenovo Vantage installed until every control has been verified on your own system. Removing Vantage does not mean removing Lenovo's signed hardware drivers.

## Build

```powershell
dotnet build .\LenovoSlimControl.csproj -c Release
```

The executable is produced under `bin\Release\net8.0-windows`.

Build and install the optional protected-event bridge from an elevated PowerShell:

```powershell
dotnet build .\PresenceBridge\PresenceBridge.csproj -c Release
.\Install-PresenceBridge.ps1
```

Remove it with `Uninstall-PresenceBridge.ps1` from an elevated PowerShell.

## Status

This is experimental software, not a finished general-purpose Vantage replacement. In particular, SmartSense's live human-presence feed is not yet decoded, and Geek Mode shares its firmware readback with ordinary Performance mode.
