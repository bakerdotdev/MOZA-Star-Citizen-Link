# D-BOX Telemetry Capture — Goal & Research State

> **STATUS (2026-06-11): PARKED — pending real D-BOX hardware.** Software-only spoofing cracked the enumeration gate but cannot pass the system-generation classifier / MonitorServicePassthrough (these need real-device data; the deciding logic is in a NativeAOT binary). **Start with [`dbox-handoff.md`](dbox-handoff.md)** for the current state and the exact resume plan. Everything below is deeper background.

This document captures the goal, the current understanding of the D-BOX/Star Citizen integration, and the open avenues for capturing Star Citizen telemetry by way of D-BOX. It is intended to let us resume this thread later without re-deriving everything.

It supersedes the (now-obsolete) approach described in `dbox-proxy.md`. See "What We've Ruled Out" below for why.

## Goal

Produce a distributable Windows `.exe` that drives a MOZA wheel/peripheral with rich force-feedback effects tied to Star Citizen events. To make those effects feel correct, we need a continuous, high-fidelity telemetry stream — engine state, accel/heave, impacts, weapons, gear, atmospheric flight, etc.

The game log alone is too sparse and lagging for a quality feel. SC 4.8 added native D-BOX telemetry output, which is the richest known source. The strategic bet: **find a way to read what SC sends to D-BOX, even on systems without D-BOX hardware**, and translate it into MOZA force-feedback effects.

## Architecture (verified 2026-05-27 via live module/socket inspection)

```
StarCitizen.exe
   │  (D-BOX-coded telemetry: only emitted when D-BOX
   │   reports an active Haptic System)
   ▼
MotionEngine.exe       ← C:\Program Files\D-BOX\Motion Engine\
   │   loads HEMotionControl.dll, RemoteControl.dll, SocketCommComponent,
   │     MotionIPC, InterprocMessaging, QAutoDiscoveryComponent, Authorization,
   │     libcurl, libssl, etc.
   │   loads SETUPAPI but NOT WinUsb and NOT dbxService64.dll
   │   listens (game-facing): TCP 12740, 12745, 61555 ; UDP 61556
   │   queries: ──► TCP 40001 (MonitorService XML) for hardware state
   │              ──► TCP 5354 (Bonjour mDNS) for service discovery
   ▼
MonitorService.exe     ← C:\Program Files\D-BOX\Monitor\
   │   Single self-contained 2.5MB .NET binary (no DLL dependencies).
   │   THE ONLY process that loads WINUSB.DLL + SETUPAPI.DLL + CFGMGR32.DLL.
   │   THIS IS WHERE USB ENUMERATION ACTUALLY HAPPENS.
   │   Listens on TCP 40001 with documented XML commands
   │     (GetVersion, GetLayout, GetStatus, GetSoftwareParameter, etc.)
   ▼
Haptic Bridge (USB)
   │   VID_15C9 ; PID_0102 / PID_0106 / PID_010C ; "Haptic Bridge KCU-1X"
   ▼
Physical actuators (ACM G3 FLEX, etc.)

(Sidecars — not on the SC→FFB telemetry path)
HaptiSyncApi.WebApi.exe ← TCP 42010 — REST front-end (NativeAOT .NET, opaque)
HaptiSyncCenter.App     ← Loads dbxService64.dll for actuator-test buttons only
                           (proven 2026-05-27: only process that maps dbxService64)
HaptiSyncBgApp.exe      ← Tray agent
dbxMotionPanel.exe      ← Per-game tray panel for SC (no D-BOX DLLs, no sockets)
dbxService64.dll        ← Backend for HaptiSyncCenter actuator-test UI only.
                           NOT loaded by MotionEngine. NOT on the enum path.
```

**Critical correction (2026-05-27):** Our earlier model put `dbxService64.dll` on the device-enumeration path. That was wrong. Live process inspection shows `dbxService64.dll` is loaded ONLY by `HaptiSyncCenter.App` — solely to drive the "Test Actuator" buttons in the UI. The genuine USB enumerator is **`MonitorService.exe`**, communicating via XML on TCP 40001. Everything above MonitorService (MotionEngine, HaptiSyncApi, etc.) is an XML *client* of the monitor.

Key fact: **the gate that prevents SC from sending telemetry is the empty USB enumeration in `MonitorService.exe`.** If MonitorService reports "no Haptic Bridge present," MotionEngine reports zero haptic systems, the HaptiSync UI's "Launch" button stays greyed out, and SC never opens a session.

Observed on 2026-05-26 with SC running in Arena Commander:
- `StarCitizen.exe` made **zero** connections to any D-BOX port. All sockets went to public RSI servers.
- The per-game stub `C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll` was **never loaded** by any process — including SC.
- `Get-Process | Where Modules.FileName -like '*dbxLive64*'` returned empty.
- `GET /api/v1/haptic-systems` would return error code `300003` ("No Haptic Systems detected on your PC") — same root cause as the greyed Launch button.

## The Gating Problem

The Coded Gaming subsystem activates *per session*, not on game install. The session-start flow appears to be:

1. HaptiSync UI's **Launch** button → tells Motion Engine to load the SC haptic code into the active runtime and mark a session as ready.
2. Motion Engine begins advertising itself as a ready receiver on its game-facing ports.
3. SC launches (via the RSI Launcher) and queries D-BOX for a ready receiver.
4. SC begins emitting telemetry to Motion Engine.
5. Motion Engine processes the stream through the haptic code and writes commands to the Haptic Bridge.

The Launch button is greyed because step 1's precondition ("at least one Haptic System present") is false. Everything downstream is therefore dark.

## What We've Ruled Out

| Approach | Result |
|---|---|
| Proxy `C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll` (the `dbox-proxy.md` approach) | **Dead.** SC never loads this DLL. Trace log written under `%LOCALAPPDATA%\MozaStarCitizen\dbx-trace\` showed only the standalone test load from PowerShell, never `StarCitizen.exe`. The earlier suspicion that this proxy caused CIG error 19000 was disproven on retest — login worked fine with the proxy installed. |
| Sniff loopback between SC and Motion Engine | **Dead while the gate is closed.** SC sends nothing to Motion Engine ports without a session. Once the gate is open, this would be viable, but it requires solving the gate first. |
| HTTP API spoof via HaptiSync WebApi (TCP 42010) | **Dead.** The OpenAPI surface is strictly read-only for hardware enumeration: `GET /api/v1/haptic-systems` and `GET /api/v1/haptic-systems/{index}` only. No `POST` to create a virtual system. Enumeration happens deeper. |
| Game-log-only telemetry | Insufficient signal for a quality FFB experience (user assessment). May be used as a complementary source but cannot be the primary stream. |

## Hardware Identification (the spoof target)

Confirmed via string analysis of `C:\ProgramData\D-BOX\MotionService\dbxService64.dll`:

- **USB VID**: `15C9` (D-BOX Technologies, Inc.)
- **USB PIDs (any of)**: `0102`, `0106`, `010C` — three Bridge model variants
- **Product name**: `Haptic Bridge KCU-1X`
- **Interface class**: vendor-defined WinUSB (NOT HID). Bulk pipes via:
  - `SetupDiGetClassDevsA`, `SetupDiEnumDeviceInfo`, `SetupDiEnumDeviceInterfaces`, `SetupDiGetDeviceInterfaceDetailA`
  - `WinUsb_Initialize`, `WinUsb_ReadPipe`, `WinUsb_WritePipe`, `WinUsb_QueryPipe`, `WinUsb_SetPipePolicy`, `WinUsb_ResetPipe`, `WinUsb_AbortPipe`, `WinUsb_FlushPipe`, `WinUsb_QueryInterfaceSettings`, `WinUsb_GetOverlappedResult`, `WinUsb_Free`
- **All hardware enumeration lives in `dbxService64.dll`.** `MotionEngine.exe` itself does not import these APIs — it calls into the service DLL for everything bridge-related.

Other useful strings observed:
- `Haptic Bridge KCU-1X` (in `dbxService64.dll`)
- `Haptic Actuator ACM G3 FLEX (Main)` (in `MotionEngine.exe`)
- `CommUnitId="%s"` — fingerprint format reported back through the WebApi

## Chosen Path (revised 2026-05-27): Spoof MonitorService XML on TCP 40001

Replacing the prior "proxy `dbxService64.dll`" plan. Live inspection proved `dbxService64.dll` is not on the enumeration path, so swapping it does nothing for the gate.

The new approach:

1. Stop the real `MonitorService.exe` (`DboxMotionPlayerMonitor` service — though service↔binary mapping needs to be confirmed; the binary may also be the Monitor service host).
2. Bind our own TCP listener on `127.0.0.1:40001`.
3. Speak the D-BOX XML protocol (request/response message types). Documented commands include `GetVersion`, `GetLayout`, `GetStatus`, `GetSoftwareParameter`; the full set needs to be enumerated from MonitorService itself.
4. Respond to `GetLayout` / device-presence calls with a synthetic Haptic Bridge layout.
5. Watch MotionEngine's reaction. Expected: it advertises a ready haptic system on its mDNS service (`DboxMotionEngine`) and HaptiSyncApi's `GET /api/v1/haptic-systems` starts returning that system.
6. With the gate open, SC should connect to MotionEngine on TCP 12740/12745 / UDP 61556 and emit D-BOX-coded telemetry. Capture it via existing pktmon scripts.

**Why this is better than the abandoned DLL-proxy approach:**

- Pure user-space; no replacing files under `C:\Program Files\D-BOX\` or `C:\ProgramData\D-BOX\`.
- Distributable as a single `.exe` for end users (the original goal).
- Resilient to D-BOX binary updates — only the XML protocol needs to stay stable.
- Easy install/uninstall (start service / stop service).
- Doesn't touch any D-BOX installation state.

**Open questions for this approach (next probes):**

1. **Is `MonitorService.exe` .NET (decompile-friendly) or NativeAOT-style native?** The `.exe.config` + `.exe.log.config` files strongly suggest classic .NET Framework with App.config + log4net config. If yes, ilspycmd should give us full source.
2. **What is the *full* XML command surface MotionEngine actually queries?** We need to capture a real exchange (existing 40001 connections) and see exactly which messages flow. The earlier docs list only 4 commands; there are likely more.
3. **What is the device-present response shape?** What payload makes MotionEngine satisfied that a Haptic Bridge exists?
4. **Does anything besides MotionEngine query 40001?** HaptiSyncCenter, HaptiSyncBgApp, HaptiSyncApi may also query. Our spoof must respond to all of them coherently or only the ones that gate the haptic-system list.
5. **What service controls `MonitorService.exe`'s lifecycle?** `DboxMotionPlayerMonitor` is a candidate (string evidence) but unconfirmed.

The DLL-proxy work (`tools/dbox-service-proxy/`) is not wasted — it proved the interception technique end-to-end and produced the live-process map that revealed the real architecture. It will stay in the repo as a reference implementation.

## (Superseded) Earlier Path: Option 1 — Push Deeper on D-BOX

We are not pivoting to a hybrid log/DirectInput/audio approach. The bet remains on D-BOX as the telemetry source, despite the difficulty, because the alternative cannot deliver the fidelity the goal requires.

Within "push deeper," two sub-paths are on the table. Both are non-trivial.

### Sub-path A — Hook `dbxService64.dll`

Build a replacement / forwarder DLL at `C:\ProgramData\D-BOX\MotionService\dbxService64.dll` that:

1. Loads the renamed original and forwards almost all exports unchanged.
2. Intercepts the device-enumeration path so Motion Engine sees one synthetic Haptic Bridge.
3. Intercepts `WinUsb_ReadPipe` / `WinUsb_WritePipe` so we can:
   - Return synthetic responses convincing enough that Motion Engine reports a "ready" Haptic System.
   - Capture whatever Motion Engine writes to the (fake) bridge — that data is the digested SC telemetry the user wants.

Pros: software-only; no driver-signing problems for end users; runs in a non-EAC-protected process (`MotionEngine.exe`).

Cons: we don't yet know the Bridge protocol over the bulk pipes. We can probably get past enumeration with a stub, but acing the "ready" handshake and reading meaningful data downstream requires reverse-engineering the byte-level protocol — possibly with no real Bridge to compare against.

### Sub-path B — Emulate the USB device

Either:
- A virtual USB driver (USB/IP, usbip-win, or a custom kernel driver) presenting VID 15C9 / PID 0102 to the OS, or
- A physical microcontroller (Pi Pico, STM32) flashed to enumerate as the same VID/PID over WinUSB.

Pros: the entire stack believes a real Bridge is present; less reverse engineering once the device responds correctly.

Cons: driver signing on Windows 11 is painful for distribution. The microcontroller route works but requires every end user to flash a board — kills the "just-an-exe" distribution model. Still requires reverse engineering the Bridge protocol on the wire.

**Working assumption: Sub-path A is the better fit for the distribution goal**, because a single DLL swap in `C:\ProgramData\D-BOX\MotionService\` is achievable from an installer. EAC is not a concern at that layer (the DLL is loaded into `MotionEngine.exe`, not `StarCitizen.exe`).

## Binary Surface (probed 2026-05-26)

Artifacts under `artifacts/dbxservice64/`.

### `dbxService64.dll` (in `C:\ProgramData\D-BOX\MotionService\`)
- **7 exports total** — tiny surface:
  1. `GetAvailableDevices` — prime target for enumeration spoof.
  2. `GetMotionServiceManager` — returns `IMotionServiceManager*` (C++ vtable interface). Most real functionality is behind this vtable.
  3. `ResetDevice`
  4. `StartDeviceTest`
  5. `StartLocationTest`
  6. `StopDeviceTest`
  7. `StopLocationTest`
- **Internal interfaces (from strings):** `IMotionServiceManager`, `IMotionServiceSession`, `IDeviceAction`.
- **Imports:** `SETUPAPI`, `WINUSB`, `WS2_32` (yes — also does network I/O), `CFGMGR32`, `ADVAPI32`, `AVRT`, `WTSAPI32`, `PROPSYS`, `WINMM`, `SHELL32`, `ole32`, `KERNEL32`.
- Not statically imported by `MotionEngine.exe` — loaded dynamically. The two binaries that reference the string `dbxService64` are `HEMotionControl.dll` and `MotionEngine.exe`.

### `HEMotionControl.dll` (in `C:\Program Files\D-BOX\Motion Engine\`)
This is where `dbxService64.dll` is referenced and likely loaded. The actual SC-telemetry / haptic-code processing probably lives here.

- **24 exports**, mostly C++ class methods on `Cysca::HomeMotionControl` and surrounding helpers.
- **`HomeMotionControl::SetDevicesAvailable(bool)` is an exported setter.** This is the most actionable discovery: if Motion Engine gates "session ready" on this flag, simply calling it with `true` may unblock the entire stack — no USB device emulation needed.
- Other exported methods on `HomeMotionControl`: `InitializeMotionGeneration`, `TerminateMotionGeneration`, `HandleGoodIdent`, `HandleTrackLost`, `SetMotionDelay`, `SetErrorCineMotionCallBack`, `Terminate`.
- **Internal interfaces (from strings):** `IMotionServiceManagerFinder`, `IMotionServiceSessionHandler`, `IMotionSourceEventHandler` (**strong candidate for where SC telemetry events arrive**), `IMotionReader`, `IMotionHandler`, `IODevice`, `IODevicePrivate`, `Sampler`, `ICineMotionSyncLogger`.
- Imports (from `MotionEngine`-side and other internal DLLs) include a `RemoteControl::RequestDispatcher` / `IRequestHandler` / `CommandResponse` command-bus pattern — worth probing as a possible existing IPC route to `SetDevicesAvailable`.

### `MotionEngine.exe` — broader component graph
Motion Engine is a host that loads many internal D-BOX DLLs (none statically link `dbxService64`):
`ACRControl`, `AudioDataCollector`, `AudioIdent`, `AudioRecorderDll`, `AudioSynchronizerConfig`, `AudioUtils`, `Authorization`, `BaseComm`, `BaseConfig`, `Component`, `ContentUpdateComponent`, `CoreUtils`, `DBManagerComponent`, **`HEMotionControl`**, `InterprocMessaging`, `MotionIPC`, `NetworkAccessComponent`, `QAutoDiscoveryComponent`, `RemoteControl`, `SettingsComponent`, `SocketCommComponent`, `Threading`, `UpgradeComponent`, `Wasabi`, `ZCommComponent`.

The audio-named DLLs are for D-BOX Coded **Video** (ACR-based movie sync). Not relevant to our SC use case.

The `SocketCommComponent` / `MotionIPC` / `InterprocMessaging` / `ZCommComponent` cluster is where the SC ↔ Motion Engine TCP/UDP traffic is handled — that's our future telemetry-capture target *once we unblock a session*.

## Revised Spoof Strategy

Given `SetDevicesAvailable(bool)` exists as a high-level setter, the path of least resistance is now:

### Spoof Path C (new, preferred) — call `HEMotionControl::SetDevicesAvailable(true)` from outside

Possibilities, easiest to hardest:
1. **Trigger via `RemoteControl` IPC** — if the command-bus accepts a "SetDevicesAvailable" command, send it from our app. Pure user-space, no DLL replacement. Need to map the command vocabulary first.
2. **Inject a small helper DLL into `MotionEngine.exe`** that walks to the live `HomeMotionControl` instance and calls the setter. Heavier; admin/DLL-injection rights required.
3. **Proxy `HEMotionControl.dll`** the way we'd otherwise proxy `dbxService64.dll`. Replacement at this layer might let us short-circuit "is hardware present" without touching the USB enumeration code at all.

Path C-1 is the dream: pure IPC, no patching, distributable.

### Falls back to:
- **Spoof Path A** (proxy `dbxService64.dll`, stub `GetAvailableDevices`) — still viable if C doesn't pan out. Lower-level, more brittle to D-BOX updates.
- **Spoof Path B** (emulate the USB device) — only if everything above fails. Requires driver work or a microcontroller; kills distribution story.

### `RemoteControl.dll` — the IPC bus

`RemoteControl.dll` is a generic command bus, ~115 exports. Boost.Asio under the hood. Imports `qwave.dll` for QoS-prioritized sockets.

Key types (from decorated export names):
- `CommandRequest` — fields exposed via `GetTarget`, `GetCommand`, `GetArguments`, `GetQuery`
- `CommandResponse` — return shape
- `IRequestHandler::Process(CommandRequest) → CommandResponse`
- `RequestDispatcher::getInstance()` — singleton, holds the handler registry
- `RegisterHandler` / `UnregisterHandler`
- Transports: `TcpCommServer`, `TcpCommClient`, `UdpCommServer`, `SerialCommServer`
- Wire types: `TcpCommandRequest`, `UdpCommandRequest`, `ServerCommandRequest`

**No HTTP/XML/JSON evidence on the wire** — this is a custom binary framing of `CommandRequest` objects.

### `HaptiSyncApi.WebApi.exe` — NativeAOT compiled (no managed metadata)

- **PE32+ native binary, ~72 MB, no CLR header.** Compiled with .NET **NativeAOT** — no IL, no decompile-to-C# possible. `ilspycmd` and `sfextract` both reject it (`PE file does not contain any managed metadata`).
- **Imports only Win32 system DLLs** (kernel32, ws2_32, crypt32, bcrypt, iphlpapi, etc.). It does **not** statically import `RemoteControl.dll` — the MotionEngine IPC protocol is implemented in C# and AOT-compiled into the .exe.
- **String literals survive AOT** and document the architecture (extracted to `artifacts/haptisync-decompile/strings-{ascii,utf16}.txt`):

**Discovery: HaptiSyncApi finds MotionEngine via mDNS**, not via a hardcoded port:
- Service instance name: `DboxMotionEngine`
- TXT records: `swport` (dynamic Motion Engine port), `swver` (version), `swname`
- DLLs involved: `MotionEngineClient.Net.dll`, `MotionEngineClient.Net.Discovery.dll`
- This means our spoof can't simply hardcode a port — it must respond to the mDNS query *or* intercept further upstream.

**Confirmed call surface (from method-name strings):**
- Use-case methods called by REST controllers: `GetHapticSystemsAsync`, `GetHapticSystemAsync`, `GetHapticSystemState`, `SetHapticSystemState`, `ValidateHapticSystemUpdateAsync`, `SetHapticSystemMotion`, `SetHapticSystemVibration`, `SetHapticSystemMuted`, `SetHapticSystemLinkedIntensity`, `StartHapticSystemTest`, `StopHapticSystemTest`, `ResetDevice`, `RegisterDevice`.
- Wrapper class: **`MotionServiceWrapper64`** — P/Invoke layer that loads `C:\ProgramData\D-BOX\MotionService\dbxService64.dll` and calls its 7 exports.
- Field `_isMotionServiceWrapperInitialized` gates everything: if false, `ThrowIfMotionServiceWrapperNotInitialized` fires.
- Error codes seen in strings: `MOTION_ENGINE_UNREACHABLE`, `MOTION_ENGINE_TIMEOUT`, `ERROR_MOTION_ENGINE_CONNECTION`. `300003` ("No Haptic Systems") corresponds to `NoHapticSystemException`.

**Key takeaway:** `HaptiSyncApi.WebApi.exe` calls `dbxService64.dll` directly via P/Invoke (not through Motion Engine). So intercepting at the `dbxService64.dll` layer (Spoof Path A) catches both Motion Engine *and* HaptiSyncApi — a single proxy serves both consumers.

**Caveat on prior string-analysis results:** the `Create_*` names (`Create_HapticSystemDTO`, etc.) are **System.Text.Json source-generated JSON converters**, NOT RemoteControl command names. They confirm the *DTO vocabulary* but not the wire-level command names that travel to Motion Engine.

### Confirmed DTO vocabulary (from .NET source-gen converters)
Useful as a list of types that flow through the system:
HapticSystem, GenericAudioDevice, GenericAudioDeviceCommUnit, CommUnitSettings, GameHapticCode, InstalledHapticCode, Profile, ProfilePropertyValue, ExperienceMode, Seat, SeatStatus, PlatformSettings, MotionDelay, MotionCodeError, MotionEngineErrorResponse, HapticSoftwareParameters, SoftwareHealthStatus, HealthCheckResponse, PingResponse, ContentUpdateState, AccountSubscription, NetworkState, Wifi, etc.

Notably also: `Create_LaunchHandler`, `Create_MotionCodeHandler`, `Create_GameHandler`, `Create_ProcessEnableChecker`, `Create_SteamLauncher`, `Create_DbxLauncher`, `Create_GameFinder`, `Create_LiveInfoFinder`, `Create_ProcessFinder`, `Create_RegFinder` — these suggest the launch-flow is a pipeline of finders → checkers → launchers, and there is a `DbxLauncher` specifically (likely RSI/D-BOX-aware game launch).

## Open Questions

1. **What does Motion Engine write to the Bridge after enumeration?** This is the most important unknown. If the protocol is simple (length-prefixed actuator commands, telemetry mirrored as-is, etc.), the project is tractable. If it includes cryptographic handshakes, signed firmware exchanges, or anti-tamper challenge-response, the project is functionally dead.
2. **Does Motion Engine verify `dbxService64.dll`'s integrity** (signature check, hash check) before loading it? If yes, a swap won't load. If no, swap is straightforward.
3. **Is there a "demo" / "test" / "diagnostics" code path inside `dbxService64.dll` or Motion Engine** that bypasses real hardware? D-BOX QA must have some way to develop without an actuator on the bench. Worth searching for.
4. **What is the SC ↔ Motion Engine packet format on TCP 12740/12745/UDP 61556?** Once the gate is open, this is the wire we ultimately want to capture, and we can decode it once we have any real session active.
5. **Is the per-game `dbxLive64.dll` (under `LiveMotion\StarCitizen\`) still relevant in SC 4.8?** It is never loaded by SC, but it may be loaded by Motion Engine when a session is active. Worth re-checking after we unblock a session.

## Next Probes (concrete to-dos for resuming)

1. **Dump exports of `dbxService64.dll`** — `dumpbin /exports` or equivalent. Identify candidate functions for enumeration interception. Pair with import dumps of `MotionEngine.exe` to see exactly which entry points it relies on.
2. **Process Monitor session** — record `MotionEngine.exe` and `MonitorService.exe` for ~30 seconds. Filter to `USB` / `HID` / `SetupAPI` / file-system reads under `\\.\` device paths. Confirms exactly how and when enumeration runs (boot, hot-plug, on session start).
3. **Search `dbxService64.dll` strings for diagnostic/test paths** — patterns like `Test`, `Demo`, `Simulator`, `Virtual`, `Bypass`, `Diagnostic`, `Loopback`. Any one of these could unlock a hardware-free path.
4. **Static analysis of `dbxService64.dll`** in Ghidra / IDA — locate the enumeration entry point, the function that returns the list of haptic systems to Motion Engine, and the WinUSB read/write loop. Goal: find the minimum surface to fake.
5. **Re-run `GET /api/v1/haptic-systems`** after any spoof attempt to confirm Motion Engine now sees the synthetic system. This is the fastest feedback loop.
6. **Once a Haptic System is reported**, hit `GET /api/v1/game-haptic-codes/installed` to confirm SC is recognized, then launch SC and re-check whether it now opens connections to Motion Engine's game-facing ports.

## Distribution Implications (do not forget)

Even if Sub-path A succeeds:

- End users must install D-BOX software (free, but a step).
- The installer for our app must replace `dbxService64.dll` in `C:\ProgramData\D-BOX\MotionService\` — admin rights required.
- Every D-BOX update is a potential break. The installer must detect a refreshed original and re-swap.
- D-BOX's EULA almost certainly prohibits reverse engineering. *Personal* interop is generally tolerated; *distribution* of a tool that emulates their hardware to a community is a separate conversation. This should be revisited before any public release.

## `dbox-service-proxy` Design (v1 — pass-through observer)

A new proxy under `tools/dbox-service-proxy/`, modeled on `tools/dbox-proxy/`, with these properties:

**Goal of v1:** load cleanly into both `MotionEngine.exe` and `HaptiSyncApi.WebApi.exe`, forward every call to the real `dbxService64.dll` unchanged, and log:
- Which export was called, with raw 64-bit register arguments (RCX, RDX, R8, R9) and the first 4 stack args.
- The return value.
- For `GetMotionServiceManager`: the returned pointer and the first ~32 vtable slots (same technique as `dbox-proxy`'s `GetEventHandler` vtable dump).
- Module-load context so we can confirm both processes loaded the proxy.

No spoofing in v1. The purpose is purely observation — once we know which vtable methods MotionEngine actually invokes (and in what order), v2 will swap in synthetic returns for the enumeration path.

### Export list

The 7 exports from `artifacts/dbxservice64/exports.txt`:
```
GetAvailableDevices
GetMotionServiceManager
ResetDevice
StartDeviceTest
StartLocationTest
StopDeviceTest
StopLocationTest
```

### Wrapper strategy

Each wrapper is declared as taking 8 `void*` args (covers up to 8 register/stack params under x64 calling convention) and returning `void*`. This is safe for any function whose args are integer/pointer-sized (no struct-by-value, no float-by-register), which matches the names — `Start/Stop/Reset` style functions taking handles, indices, booleans, strings. If any signature uses XMM registers we'll see corruption and switch that specific export to a MASM `jmp` trampoline.

### Install layout

```
C:\ProgramData\D-BOX\MotionService\
    dbxService64.dll       (our proxy — replaces original)
    dbxService64_real.dll  (renamed original)
    _backup-moza-proxy\
        dbxService64-<timestamp>.dll  (safety copy of original)
```

The D-BOX motion service is loaded at boot by `MotionService.exe`, so installation requires stopping `DBOXMotionService` (or rebooting). Install/uninstall scripts will be added under `scripts/`.

### Log location

`%LOCALAPPDATA%\MozaStarCitizen\dbx-trace\dbxsvc-<timestamp>-<exe>-<pid>.log` — same scheme as the existing proxy, but with a different filename prefix to distinguish runs.

## HaptiSync Strings — mDNS & Gating Findings (2026-05-27)

Extracted from `artifacts/haptisync-decompile/strings-ascii.txt` (NativeAOT — readable strings but no decompile):

**mDNS service constants HaptiSync uses internally:**
- `MOTION_ENGINE_SERVICE_INSTANCE_NAME` = `DboxMotionEngine`
- TXT records: `swport`, `swver`, `pname`
- HaptiSync has both `Discover@` and `Advertise@` / `Unadvertise` / `RegisterDevice` paths. Open: is HaptiSync the advertiser, or only the client looking up MotionEngine's advertise? (Task #13.)

**Session-state codes (key for understanding the gate):**
`MotionSessionCannotBeCreated`, `DeviceCannotBeSelected`, `StreamCannotStart`, `SessionAlreadyInUse`, `DeviceNotFound`, `NoTestRequired`, `ModeNotCodedGaming`.
`ModeNotCodedGaming` confirms there is an "Experience Mode" check (enum: `CodedVideo`, `CodedGaming`, `AdaptiveAudio`, `AdaptiveGaming`) — SC's path is `CodedGaming`.

**Hub commands on HaptiSync's WebApi:**
`SetWifiSetting`, `MONITOR_SERVICE_PASSTHROUGH` / `MonitorServicePassthrough`, `RequestContentUpdate`, `StartAuthorizationProcess`, `DisconnectFromDboxConnect`. The `MonitorServicePassthrough` hub command is worth investigating — name implies it forwards arbitrary calls through to MonitorService.

**Architectural confession in HaptiSync source:**
> "DboxMotionProcessDetectionService is a temporary solution, will be replaced by MotionEngine MQ message within Coded Gaming 2.0. That's why we're fair with code duplication from HSC Layer."
Implies HaptiSync watches for the MotionEngine process (process-detection), and a future MQ message will replace that — but the current shipping version uses process detection.

**Virtual hardware enums (static type tables — no obvious config toggle):**
`Virtual ACM` (CommUnit type, line 80573) and `Virtual Actuator` (Actuator type, line 80611) exist in MonitorService as recognized hardware types alongside KCU-1X, ACM-II, ACM G3 FLEX. No "enable virtual mode" config flag found in either binary. Likely instantiated by a specific XML payload structure, not a config option.

**False lead noted:** "Failed to send advertise message" string is unrelated — it's part of the .NET runtime's diagnostic IPC (`DOTNET_IPC_V1`).

## Useful Reference Material in This Repo

- `docs/telemetry-inputs.md` — current `IStarCitizenTelemetrySource` shape; what discovery scripts exist; how artifacts are written.
- `scripts/collect-dbox-discovery.ps1` — packaged pktmon-based capture run.
- `scripts/probe-dbox-receiver-port.ps1` — opportunistic listener on `127.0.0.1:61666`.
- `src/MozaStarCitizen.App/Telemetry/` — current code path for telemetry sources.
- `tools/dbox-proxy/` — the (now-superseded) `dbxLive64.dll` proxy. Useful as a reference implementation for the technique now being applied to `dbxService64.dll`.
- `tools/dbox-service-proxy/` — the `dbxService64.dll` proxy (see "Design" section above).
