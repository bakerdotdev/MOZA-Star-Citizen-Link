# Star Citizen 4.8 / D-BOX telemetry investigation

> **Safety status (2026-07-14): archived; do not execute the live-capture plan.**
> Handler substitution, production file changes, process inspection, service
> proxying, port impersonation, and packet capture are outside the supported
> workflow and should not be used with EAC. Use only the
> [offline SDK sample replay](dbox-sdk-sample-replay.md) while requesting a
> [vendor-sanctioned interface](vendor-telemetry-access-request.md).

Date: 2026-07-14

Status: capture point identified and validated with the official SDK; a live
Star Citizen capture has not yet been attempted.

## Executive conclusion

The high-fidelity Star Citizen telemetry path is real, structured, and accessible
before D-BOX motion synthesis. It is not an audio listener, screen watcher, or
guessed network protocol.

Star Citizen 4.8 statically links the D-BOX Live Motion API. The API validates and
loads a D-BOX-signed event-handler DLL directly into StarCitizen.exe, then passes
typed event registrations and raw structure pointers to it in-process. For the
application key StarCitizen, the first handler path is:

    C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll

The downloaded SDK includes another valid D-BOX-signed implementation of the same
handler interface: Live Motion XML Logger. It logs every RegisterEvent schema and
every PostEvent value as plaintext XML. An isolated run with the official
SampleRacer64 executable proved that this logger works without D-BOX hardware,
MotionEngine, HaptiSync, or any network transport.

This is the decisive capture experiment:

1. Back up and hash the installed Star Citizen D-BOX handler.
2. Temporarily substitute the SDK's signed XML logger.
3. Run a short, marked Star Citizen session.
4. Collect the self-describing XML telemetry.
5. Exit the game and restore the original handler, verifying its hash.

No game or D-BOX installation file has been changed yet. Although the substitute
is an official D-BOX-signed DLL and no Star Citizen executable is patched, it will
be loaded inside an Easy Anti-Cheat-protected process. That deserves explicit
user approval and a tightly controlled backup/restore procedure before the live
test.

This discovery also explains the earlier failures:

- The first semantic boundary is inside StarCitizen.exe, not MotionEngine.
- The loader checks Authenticode and a D-BOX signer allowlist before loading a
  handler, so an unsigned homemade proxy is rejected.
- The ProgramData application-specific handler has priority over a DLL placed
  beside StarCitizen.exe.
- Socket hooks were aimed downstream of the clean, typed event boundary.

## Evidence standard

Conclusions in this document are labeled as follows:

- Verified: directly observed in official SDK material, disassembly, an installed
  binary, or a retained runtime result.
- Strong inference: several independent facts agree, but the decisive runtime
  observation has not yet been made.
- Unknown: requires a discriminating experiment.

A string, listening endpoint, or owned port alone is not treated as proof of a
telemetry path.

## Verified findings

### 1. SDK inventory and documentation

D:\DBOX-SDK contains 56 files totaling 18,777,381 bytes:

- two confidential PDF manuals;
- five public C++ headers;
- 32 static libraries for several Visual C++ runtime/toolset combinations;
- five C++ samples and ten prebuilt 32/64-bit sample executables;
- signed 32-bit and 64-bit XML logger DLLs.

The distributed static libraries identify the current wrapper as LiveMotion API
1.0.13289. The prebuilt samples use API 1.0.12713 and the logger is 1.0.12715,
which demonstrates compatibility across those revisions.

The package contains no standalone license/EULA text or redistribution grant.
Every manual page is marked CONFIDENTIAL, the headers reserve all rights, and the
logger displays DO NOT DISTRIBUTE WITH GAME. Do not commit or redistribute SDK
files. Any eventual product path must address D-BOX's license and distribution
terms independently of this local investigation.

Primary local references:

- D:\DBOX-SDK\D-BOX_Compatibility_Guide.pdf
- D:\DBOX-SDK\LiveMotionSdk\226-989-0006-EN1_2 - D-BOX SDK Overview.pdf
- D:\DBOX-SDK\LiveMotionSdk\include\LiveMotion\dboxLiveMotion.h
- D:\DBOX-SDK\LiveMotionSdk\include\LiveMotion\dboxLiveMotionTypes.h
- D:\DBOX-SDK\LiveMotionSdk\include\LiveMotion\dboxEEventMeaning.h
- D:\DBOX-SDK\LiveMotionSdk\include\LiveMotion\dboxEFieldMeaning.h

### 2. Definitive Live Motion runtime architecture

The application links the Live Motion wrapper statically. Initialization with an
application key causes that wrapper to locate a handler DLL, verify it, load it,
resolve GetEventHandler, and call the returned C++ interface.

The recovered 64-bit search order is:

1. %ProgramData%\D-BOX\Gaming\LiveMotion\<AppKey>\dbxLive64.dll
2. <process-directory>\D-BOX\dbxLive64.dll
3. <process-directory>\Motion\dbxLive64.dll
4. <process-directory>\dbxLive64.dll

DllEventHandlerFinder.obj proves this order through SHGetFolderPathA with
CSIDL_COMMON_APPDATA, GetModuleFileNameA, LoadLibraryA, and GetProcAddress for
GetEventHandler.

Before LoadLibraryA, DllValidator.obj calls WinVerifyTrust and the Windows image
certificate/cryptographic APIs. It requires a valid embedded certificate and
checks the signer against an internal allowlist. Both the installed Star Citizen
handler and the SDK logger pass this validation and are signed by D-BOX
Technologies Inc.

The resulting architecture is:

    Star Citizen game systems
      -> statically linked Live Motion API
      -> D-BOX signature validation
      -> GetEventHandler from app-specific dbxLive64.dll
      -> RegisterEvent(schema)
      -> PostEvent(raw typed structure)
      -> chosen handler implementation

With the production handler selected, its subsequent network/motion path is
handler-specific and downstream. With the XML logger selected, the same semantic
calls are written directly to a plaintext log.

There is no SDK-level port, packet format, or socket to discover. Port hunting was
looking after the most useful boundary.

### 3. Exact event-handler ABI

The factory is:

    extern "C" dbox::IEventHandler* __cdecl GetEventHandler();

On x64 the vtable operations, in order, are:

1. Initialize(appKey, appBuild, apiKey)
2. Terminate
3. Open
4. Close
5. Start
6. Stop
7. ResetState
8. RegisterEvent
9. PostEvent
10. PostEventToSession
11. extra-session setup
12. PostEvents
13. PostEventsToSession

The structures recovered from the static library are:

    FieldDef, size 6
      +0  uint8  type
      +1  uint8  flags
      +2  uint16 meaning
      +4  uint16 offset

    EventDef, size 16
      +0  uint32    key
      +4  uint16    meaning
      +6  uint16    fieldCount
      +8  FieldDef* fields

    EventInfo, size 32
      +0  uint32      key
      +4  padding
      +8  void const* data
      +16 uint64      structSize
      +24 uint64      count

RegisterEvent makes each later PostEvent self-describing: it supplies field type,
semantic meaning, and byte offset before any payload is posted.

### 4. Official hardware-free XML logger

The SDK logger is:

    D:\DBOX-SDK\LiveMotionSdk\Samples\dbxLive64.dll

- File description: Live Motion XML Logger
- Version: 1.0.12715.0
- SHA-256: 8CA66CED19B03142E1804C9CD8311EC02E2689ED20D2A985239B1325DE448584
- Authenticode: valid, D-BOX Technologies Inc.
- PE exports: GetEventHandler only

Static imports show file, console, thread, synchronization, and signature/runtime
support. They do not show Winsock, HTTP, named-pipe, USB/serial, service-control,
or registry telemetry transports.

An official SampleRacer64 run produced a file named like:

    dbxLive64_<date>_<time>.log

The file followed the process current working directory, not the executable or
DLL directory. It recorded, with millisecond timestamps:

- Initialize with SampleRacer, build 1001, and API version;
- every event key, meaning, field count, field type, semantic name, and offset;
- every posted structure size and decoded scalar/vector/four-corner value;
- Open, ResetState, Start, Stop, Close, and Terminate.

Known sample values such as acceleration (0.4, 0.2, 1.0), boost intensity 0.75,
and impact intensity/direction 0.4 and 1.57 appeared exactly in the XML. No D-BOX
hardware or service was required.

The logger is a diagnostic substitute, not a tee. While selected, it replaces
the production motion handler for that process.

### 5. Star Citizen 4.8 calls the same boundary

The inspected executable is:

    D:\StarCitizen\LIVE\Bin64\StarCitizen.exe

- Version: 4.8.184.2887
- Size: 180,734,976 bytes
- SHA-256: B89187708198FB9F3F35C979043DD51A6E7A7B08112F90F298B935106D363FCC
- Linked Live Motion wrapper: LiveMotionApiMD.13289

At virtual address 0x145C605DD the binary calls:

    LiveMotion::Initialize("StarCitizen", 1)

It then registers twelve permanent event keys, calls Open(1), ResetState, and
Start, and later posts structure sizes that exactly match the registered layouts.
This proves Star Citizen is a native SDK producer rather than an audio-derived
consumer.

### 6. Recovered Star Citizen event schemas

All flags are FF_NORMAL. Meanings and layouts below were recovered from the
registration routine and embedded FieldDef arrays. Runtime correlation is still
required to name fields that CIG intentionally registered as CUSTOM.

| Key | Adjacent purpose string | Event meaning | Size | Registered fields |
|---:|---|---|---:|---|
| 0 | Global Frame | FRAME_UPDATE | 4 | elapsed time |
| 1 | Enter Vehicle Config | CONFIG_UPDATE | 8 | vehicle type string pointer |
| 2 | Leave Vehicle Event | ACTION_TRIGGER_PULSE | 0 | no data |
| 3 | Vehicle Frame | FRAME_UPDATE | 116 | 13 fields; detailed below |
| 4 | Vibration Frame | FRAME_UPDATE | 76 | 19 float fields; detailed below |
| 5 | Weapon Frame | FRAME_UPDATE | 8 | action slot, weapon ID |
| 6 | Explosive Ordnance Frame | FRAME_UPDATE | 8 | action slot, weapon ID |
| 7 | Vehicle Impact Frame | FRAME_UPDATE | 32 | vector plus five floats |
| 8 | Vehicle Explosion Impact Frame | FRAME_UPDATE | 24 | vector plus three floats |
| 9 | Vehicle Decoupled Frame | FRAME_UPDATE | 4 | custom integer |
| 10 | Player Role | FRAME_UPDATE | 4 | custom integer |
| 11 | Turret Frame | FRAME_UPDATE | 12 | custom XYZ vector |

Vehicle Frame, key 3:

| Offset | Type | Public field meaning |
|---:|---|---|
| 0 | XYZ float32 | ACTOR_GFORCE_XYZ |
| 12 | XYZ float32 | FRAME_CUSTOM_01 |
| 24 | XYZ float32 | FRAME_CUSTOM_02 |
| 36 | XYZ float32 | FRAME_CUSTOM_03 |
| 48 | XYZ float32 | VELOCITY_XYZ |
| 60 | XYZ float32 | ANGULAR_ACCELERATION_XYZ |
| 72 | XYZ float32 | ANGULAR_VELOCITY_XYZ |
| 84 | float32 | LANDING_GEAR_GENERAL_DEPLOYMENT |
| 88 | float32 | AIRCRAFT_VLO_KT |
| 92 | int32 | ENGINE_RPM_IDLE |
| 96 | XYZ float32 | FRAME_CUSTOM_04 |
| 108 | float32 | FRAME_CUSTOM_05 |
| 112 | int32 | FRAME_CUSTOM_06 |

Vibration Frame, key 4:

- offsets 0 through 36: ten consecutive float32 values registered as
  FRAME_CUSTOM_01 through FRAME_CUSTOM_10;
- offsets 40 through 72: nine consecutive float32 values registered as
  CONFIG_CUSTOM_01 through CONFIG_CUSTOM_09.

Vehicle Impact Frame, key 7:

- offset 0: XYZ float32, FRAME_CUSTOM_01;
- offsets 12, 16, 20, 24, and 28: float32,
  FRAME_CUSTOM_02 through FRAME_CUSTOM_06.

Vehicle Explosion Impact Frame, key 8:

- offset 0: XYZ float32, FRAME_CUSTOM_01;
- offsets 12, 16, and 20: float32,
  FRAME_CUSTOM_02 through FRAME_CUSTOM_04.

The public meanings already expose useful continuous telemetry: actor-local
G-force, velocity, angular acceleration, angular velocity, and landing-gear
deployment. The custom fields must remain numbered until marker-based runtime
correlation establishes their meanings.

The D-BOX coordinate system is left-handed:

- X positive is right;
- Y positive is up;
- Z positive is forward.

### 7. Installed production handler

The production handler is:

    C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll

- Version: 1.0.11387.0
- Size: 6,863,264 bytes
- SHA-256: 186F3D0EFA7A9AADB0C9060ADA4169D8B1E8A5F10904351FCF1823016429CD96
- Authenticode: valid, D-BOX Technologies Inc.
- PE exports: GetEventHandler only

It imports WS2_32 and qwave and contains local UDP sender/receiver and
queue/latency code. Those details describe what the production handler does after
receiving semantic events; they do not change the upstream handler boundary.

MotionEngine and its ports may still be relevant to final D-BOX motion output,
but MotionEngine is not the process that first receives Star Citizen's registered
structures. The earlier architecture in this repository inverted that
relationship.

### 8. What the manuals mean by audio events

The Compatibility Guide recommends actor-local physics information and audio
events as sources inside a game engine. It then defines FRAME_UPDATE,
CONFIG_UPDATE, ACTION, and ACTION_WITH_DATA and shows typed structures passed to
the SDK.

That is analogous to audio programming's event/spatialization model. It does not
describe microphone capture, waveform analysis, WASAPI loopback, or an
audio-driven telemetry fallback. The guide's statement that output toward the
D-BOX Motion System is encrypted also concerns the downstream motion path. The
official logger proves that the upstream semantic SDK boundary is plaintext
loggable.

## Corrections to prior repository conclusions

The following previous claims are now disproven or unsupported:

### Star Citizen never loads dbxLive64.dll

Disproven statically. Star Citizen initializes the SDK with application key
StarCitizen, and the wrapper's first candidate is exactly the installed
ProgramData StarCitizen handler.

The old observation likely failed because the feature was not active during the
probe, the module tool missed the load, or initialization returned early. It
cannot override the recovered loader code.

### MotionEngine loads the Star Citizen handler

Incorrect for the SDK input boundary. StarCitizen.exe loads the handler itself.
The production handler may communicate with MotionEngine afterward.

### The old unsigned proxy should have worked at the right path

Incorrect. The static loader verifies Authenticode and an allowed signer before
LoadLibraryA. An unsigned locally built proxy is rejected. A DLL beside the game
also cannot override a valid higher-priority ProgramData handler.

### Real hardware is required to observe SDK telemetry

Disproven for the logger path. The SDK's signed XML logger captured registrations
and values from the official sample without hardware.

Production motion output may still report NO_DEVICE_FOUND, but that gate is
downstream and irrelevant when the diagnostic handler is selected.

### TCP/UDP port discovery is the first task

Incorrect. The cleanest data exists as typed in-process structures before the
production handler chooses any transport. Network analysis is now a fallback for
a distributable long-term path, not the first decoding task.

### The 840-case hardware sweep completed

Unsupported by retained evidence. artifacts/gen-sweep-results.txt stops at
17/840, every saved row says present=False, and no successful haptic-system API
response is retained.

### MonitorServicePassthrough necessarily needs real-device secrets

Unsupported. The synthetic responder omitted protocol behaviors such as Enumerate
and LayoutUpdate. A timeout did not distinguish a missing hardware secret from an
incomplete emulator.

These hardware-emulation questions no longer block the signed logger experiment.

## Controlled live-capture plan

### Gate 0: explicit approval and risk boundary

Do not perform the Star Citizen substitution silently.

Before it:

1. Explain that the logger is official and validly D-BOX-signed but is not an
   Easy Anti-Cheat guarantee.
2. Confirm StarCitizen.exe, RSI Launcher, Easy Anti-Cheat bootstrap processes,
   HaptiSync, and MotionEngine are stopped.
3. Keep the experiment short and restore the original handler before any normal
   play session.
4. Do not patch StarCitizen.exe, inject code, attach a debugger, or read process
   memory.

### Gate 1: preflight and rollback

The capture helper should:

1. Refuse to run if StarCitizen.exe is active.
2. Recompute both known hashes and validate both D-BOX signatures.
3. Copy the installed handler to a timestamped backup outside the active
   StarCitizen handler directory.
4. Record the original file hash, size, version, timestamps, and ACL.
5. Determine Star Citizen's current working directory and confirm the user can
   create the logger output there.
6. Install only the SDK logger under the expected dbxLive64.dll name.
7. Arm a timeout/process-exit watcher that restores the original even if the game
   or launcher fails.

Restoration is successful only when the active file again hashes to:

    186F3D0EFA7A9AADB0C9060ADA4169D8B1E8A5F10904351FCF1823016429CD96

### Gate 2: short marked capture

Launch Star Citizen normally through its supported launcher. Record wall-clock
marker timestamps for:

1. menu idle;
2. enter pilot seat;
3. engine off, then engine on;
4. stationary throttle pulses;
5. controlled forward acceleration and braking;
6. roll, pitch, and yaw separately;
7. landing gear down and up;
8. coupled then decoupled mode;
9. isolated primary weapon shots;
10. missile/ordnance event;
11. turret movement;
12. one low-risk directional impact;
13. exit to menu and close the game.

Keep the first capture to a few minutes because plaintext per-frame XML can grow
quickly.

### Gate 3: restore before analysis

After Star Citizen exits:

1. restore the original production handler;
2. verify the exact original SHA-256;
3. verify no StarCitizen.exe process remains;
4. retain the XML log and marker timeline in a workspace artifact folder;
5. do not launch the game again if restoration verification fails.

### Gate 4: decode and correlate

The logger output is a sequence of standalone XML Log elements rather than one
document root. A parser should:

1. ingest RegisterEvent records into a schema keyed by permanent event key;
2. normalize PostEvent values to timestamped NDJSON;
3. preserve raw key, field meaning, type, offset, and value;
4. attach the recovered key-purpose labels;
5. compute event cadence and gaps;
6. correlate changes against the marker timeline;
7. leave CUSTOM fields numbered until at least two discriminating tests agree.

Success criteria for the first run:

- Initialize reports AppKey StarCitizen;
- all twelve recovered keys are registered;
- key 3 produces continuous G-force/velocity/angular data;
- gear state changes at offset 84;
- at least one weapon or ordnance frame changes;
- impact fields respond to the marked impact;
- the original production handler is restored and hash-verified.

## Path from capture to usable MOZA force feedback

The XML logger solves telemetry discovery and can potentially be tailed locally
for a development proof of concept. It does not by itself solve product
distribution:

- it replaces D-BOX motion output rather than forwarding to the production
  handler;
- it is a disk/console diagnostic, not a low-latency supported API;
- D-BOX explicitly says not to distribute it with a game;
- loading any alternate handler inside Star Citizen remains an anti-cheat risk
  until tested or approved.

After a successful capture, pursue these paths in order:

1. Build an offline/live-tail parser and map verified fields into the existing
   telemetry model. This proves the MOZA effects using the user's local SDK
   logger without redistributing it.
2. Test D-BOX's already-installed, signed LiveMotionConnector as the selected
   handler. It exposes typed RegisterEvent/PostEvent messages over gRPC and is a
   better real-time receiver if its configuration and license permit this use.
3. Ask D-BOX/CIG for a supported signed tee/connector or signing path. A forwarding
   handler could preserve D-BOX output while exposing telemetry, but an unsigned
   homemade DLL will fail the wrapper's validator.
4. Reverse the production handler's downstream UDP only if the signed connector
   route fails. The downstream data may be motion code rather than the original
   semantic telemetry, so it is a less attractive source.

The immediate blocker is no longer technical uncertainty about where telemetry
exists. It is authorization to perform the short, reversible live substitution
under Easy Anti-Cheat.

## Recommended next action

Prepare and review a fail-safe install/capture/restore script, but do not execute
its install action yet. Once the user explicitly accepts the anti-cheat risk,
perform one short logger capture and use the resulting self-described XML to
build the Star Citizen telemetry adapter.

No part of this plan returns to audio DSP or screen watching as a substitute for
the native telemetry path.
