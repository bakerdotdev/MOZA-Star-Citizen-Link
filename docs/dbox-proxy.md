# D-BOX Proxy

> **Safety status (2026-07-14): archived; do not install or run this proxy.**
> Production-handler replacement and in-process interception are outside the
> supported workflow. Use the [offline SDK sample replay](dbox-sdk-sample-replay.md)
> and pursue a [vendor-sanctioned interface](vendor-telemetry-access-request.md).

> **Correction (2026-07-14):** Static analysis of the official SDK and Star
> Citizen 4.8 proves that StarCitizen.exe does load the ProgramData
> app-specific dbxLive64.dll. The proxy below failed because the SDK validates a
> D-BOX Authenticode signer before loading a handler, and because its socket
> hooks target a downstream boundary. The SDK's signed XML logger is the
> verified semantic capture tool. See
> [dbox-investigation-2026-07-14.md](dbox-investigation-2026-07-14.md).
>

> **Status: superseded (2026-05-26).** The approach described here — proxying `dbxLive64.dll` inside `StarCitizen.exe` — does not work on SC 4.8. `StarCitizen.exe` never loads that DLL; no process on a hardware-less rig does. See [`dbox-telemetry-research.md`](dbox-telemetry-research.md) for the current goal, the architecture as we now understand it, and the chosen path forward (hooking `dbxService64.dll` inside `MotionEngine.exe`). This document is kept as a reference for the proxy-DLL technique itself, which is still the basis for the new approach — just applied at a different layer.

---

A drop-in replacement for `dbxLive64.dll` that:

1. Loads the renamed original (`dbxLive64_real.dll`) and forwards `GetEventHandler` to it.
2. Enumerates every module currently loaded into the host process whose base name starts with `dbx` (e.g. `dbxLive64_real.dll`, `dbxService64.dll`) and hooks each one's WS2_32 imports - `WSASendTo`, `WSASend`, `WSARecvFrom`, `WSARecv`, `WSASocketW`, `sendto`, `send`, `recvfrom`, `recv`, `bind`, `connect`. Every socket call those modules make is logged with destination address, port, and payload bytes.
3. Logs `GetEventHandler` factory calls plus the first few vtable slots of the returned object, so we can later wrap it for semantic capture if the wire format turns out opaque.

Both `dbxLive64.dll` (game-specific adapter) and `dbxService64.dll` (motion runtime) do their own socket I/O - the proxy needs to hook both, which is why it enumerates all `dbx*` modules rather than just the one it's replacing.

The proxy never modifies traffic - it only observes.

## Build

Requires Visual Studio 2022 Community (already installed). From a normal shell:

```cmd
tools\dbox-proxy\build.cmd
```

Output: `tools\dbox-proxy\out\dbxLive64.dll` (and `.pdb`, `.lib`).

The build script auto-locates `vcvars64.bat` if `cl.exe` is not already on PATH.

## Pre-deploy check

Confirm Star Citizen actually loads `dbxLive64.dll` from the D-BOX `ProgramData` path (versus being injected from somewhere else). With Star Citizen running and in a session where D-BOX would normally be active:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\check-dbox-loaded-in-sc.ps1
```

The script lists any module loaded into `StarCitizen.exe` whose path contains `dbx`, `D-BOX`, `LiveMotion`, `HaptiSync`, etc. The output also explains what the result means for the install approach.

If the loaded path is under `C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\`, the file-replacement install below will work. If the loaded path is somewhere else, point `install-dbox-proxy.ps1 -TargetDir` at that directory instead.

## Install

Star Citizen must not be running.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-dbox-proxy.ps1
```

This:

1. Backs the original DLL into `<TargetDir>\_backup-moza-proxy\dbxLive64-<timestamp>.dll`.
2. Renames the original to `dbxLive64_real.dll`.
3. Copies the proxy in as `dbxLive64.dll`.

Default target is `C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen`. Override with `-TargetDir`.

## Run

1. Start D-BOX Haptic Center / motion connector (the SDK probably needs a configured destination before it emits anything; without an active connector it may just initialize and stay silent).
2. Launch Star Citizen and enter the flight situations you want telemetry for - idle thrust, boost, weapon fire, gear deploy, impact, atmospheric flight, etc.
3. Exit Star Citizen cleanly so `DLL_PROCESS_DETACH` flushes the log.

## Logs

```
%LOCALAPPDATA%\MozaStarCitizen\dbx-trace\dbx-trace-<timestamp>-<exe>-<pid>.log
```

Each line is timestamped (ms since DLL attach) and tagged with the calling thread. Expected structure:

```
[       0.000 ms tid=1234 ] === dbxLive64 proxy DLL_PROCESS_ATTACH ===
[       0.123 ms tid=1234 ] host_exe=C:\...\StarCitizen.exe
[       0.456 ms tid=1234 ] loading real dll: C:\ProgramData\...\dbxLive64_real.dll
[       2.789 ms tid=1234 ] real_dll loaded base=0x00007FFXXXXXXXXX
[       2.812 ms tid=1234 ] hooks installed:
[       2.815 ms tid=1234 ]   WSASendTo   prev=0x...
...
[     245.000 ms tid=5678 ] >> GetEventHandler called
[     245.020 ms tid=5678 ] << GetEventHandler returned 0x000001ABCDEFGH00
[     245.022 ms tid=5678 ]    object vtable=0x...
[     245.024 ms tid=5678 ]    vtable[ 0] = 0x...
...
[    9999.000 ms tid=9999 ] WSASendTo socket=1234 bufs=1 total=64 flags=0x0 overlapped=0x...
[    9999.001 ms tid=9999 ]   dest: 127.0.0.1:61555
[    9999.002 ms tid=9999 ]   buf[0] len=64 ascii=...
[    9999.003 ms tid=9999 ]   hex=...
```

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-dbox-proxy.ps1
```

This removes the proxy and renames `dbxLive64_real.dll` back to `dbxLive64.dll`. The timestamped backup in `_backup-moza-proxy\` is left in place as a safety net.

## What we are looking for in the logs

- Confirmation that the SDK is loaded into Star Citizen at all (`DLL_PROCESS_ATTACH` line appears).
- Confirmation that the SDK opens sockets (`WSASocketW` lines).
- The destination address+port the SDK sends to (`WSASendTo` / `sendto` / `connect` lines).
- The wire format of the telemetry payload (`buf[0] hex=...` lines).

If `DLL_PROCESS_ATTACH` shows up but no `WSASocketW`, the SDK loaded but never tried to network. Likely Haptic Center / connector not running.

If `WSASocketW` shows up but no `WSASendTo`, the SDK opened sockets but the game never pushed events. Likely the game does not consider this session "D-BOX active" - check whether D-BOX is enabled in the game settings, and whether you are in a vehicle/flight context.

If `WSASendTo` lines appear, we have the wire format. Once decoded, the FFB app can consume the same packets directly without any DLL injection in production.
