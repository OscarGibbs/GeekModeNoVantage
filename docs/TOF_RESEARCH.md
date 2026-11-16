# Lenovo ToF / HPD research notes

Target: Lenovo IdeaPad Pro 5 14IAH10 (83JK), BIOS QLCN36WW, Lenovo Intelligent
Sensing driver 1.0.8.5.

## Confirmed stack

- PnP device: `ACPI\IDEA2002\5&14b36efe&0`, Lenovo Intelligent Sensing.
- ACPI path: `ACPI(_SB_)#ACPI(PC00)#ACPI(LPCB)#ACPI(EC0_)#ACPI(LISD)`.
- UMDF driver: `LnvSST.dll` from `lnvsst.inf` 1.0.8.5.
- Service: `SmartSense.exe` as LocalSystem.
- Per-user components: `UserSSCtrl.exe` and `SmartSenseController.exe`.
- HPD RPC `SensorType=1`, which Lenovo's client names `VL53L1`.

## HPD RPC findings

The local `HumanPresenceDetectionEndpoint` is useful for reading configuration,
capabilities, interface version and sensor type. It is not a live presence stream.

Lenovo's managed client publicly names its callback `SettingUpdated`. A passive
60-second subscription produced zero callbacks during normal sensing. The callback
payload is a `HumanPresenceDetectionSetting` configuration snapshot.

On this IdeaPad, setter calls made through both `vantage-cli` and Lenovo's own
`Lenovo.Vantage.SmartSenseRpcClient.dll` return Win32 error 87
(`ERROR_INVALID_PARAMETER`). The same protocol was reportedly writable on a
different ThinkBook. Production writes therefore retain the confirmed registry +
SmartSense restart path for this model; RPC remains a read/diagnostic channel.

## Live presence decision channel

`LnvSST.dll` creates two manual-reset global events:

```text
Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_PreLockEvent
Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_CancelLockEvent
```

Non-consuming `NtQueryEvent` polling observed this transition during a physical
leave/return test:

```text
initial: PRE_LOCK=True,  CANCEL_LOCK=False
return:  PRE_LOCK=False, CANCEL_LOCK=True
```

This is sufficient to report Lenovo's current absent/present decision. It is not
a raw ToF measurement. The event DACL rejects an ordinary desktop process even
when it requests query-only access; an elevated probe succeeds. Lenovo derives a
private package capability SID and deliberately protects these objects.

## Distance data

The HPD registry value `AdjustDistance` remained at `529` while the protected
presence events changed state. It therefore behaves as calibration/threshold
state, not a continuously published distance sample.

The VL53L1 hardware is capable of distance, signal rate, ambient rate and range
status internally, but no public/RPC/registry channel carrying those measurements
has been identified. Recovering them requires further analysis of the UMDF/ACPI
path and may conflict with Lenovo's active sensor ownership.

## Experimental probe

`experiments/HpdProbe` contains:

- managed API metadata inspection;
- a `SettingUpdated` callback watcher;
- a non-consuming protected-event state watcher;
- optional `AdjustDistance` correlation logging.

The protected-event watcher currently requires elevation. It must not reset or
wait destructively on Lenovo's events; it uses `NtQueryEvent` only.

## Read-only production bridge

`PresenceBridge` implements the privileged boundary with explicit installation.
It runs as LocalSystem, requests only `EVENT_QUERY_STATE`, polls through
`NtQueryEvent`, and publishes a small enum (`Present`, `Absent`, `Unknown`) plus a
one-second heartbeat under `HKLM\SOFTWARE\GeekModeNoVantage\Presence`.

There is no network listener, named-pipe command surface or Lenovo write path.
The normal tray process treats a heartbeat older than five seconds as offline.
Installation and removal are handled by `Install-PresenceBridge.ps1` and
`Uninstall-PresenceBridge.ps1`; neither is run implicitly by the application.
