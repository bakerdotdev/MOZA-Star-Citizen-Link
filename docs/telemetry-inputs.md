# Telemetry Inputs

The app is designed around `IStarCitizenTelemetrySource`. A source initializes itself, exposes diagnostics, and streams `StarCitizenTelemetryFrame` values. The force-feedback controller only depends on those normalized frames, so future telemetry channels can be added without rewriting the AB6 output path.

## Audio DSP (default)

`AudioDspTelemetrySource` derives force-feedback signals from Star Citizen's own audio. It is the default `Auto` source and the one to use day to day, because it requires no D-BOX software, no process injection, and no anti-cheat-adjacent access — it only reads the system audio loopback.

Pipeline:

1. `NAudio.Wave.WasapiLoopbackCapture` captures the default render endpoint (override with `MOZA_SC_AUDIO_DEVICE`).
2. Frames are downmixed to mono and scaled by `MOZA_SC_AUDIO_GAIN`.
3. `AudioTelemetryAnalyzer` runs a 2048-point FFT with 50% overlap (~47 windows/sec) and computes:
   - **Engine rumble** — RMS of the 30–160 Hz band mapped through a dB window, with attack/release smoothing.
   - **Engine frequency** — spectral centroid of the engine band folded into a 12–55 Hz tactile range.
   - **Atmosphere** — RMS of the 2–8 kHz band (slow envelope so transients don't inflate it).
   - **Impact** — spectral flux in 40–200 Hz, compared against a slow running average so detection is volume-independent.
   - **Weapon fire** — spectral flux in 1.5–6 kHz, same onset method.
   - **Boost** — a conservative detector: sustained engine loudness well above its slow baseline.
4. Each window is emitted as a `StarCitizenTelemetryFrame` over a bounded `Channel` (drop-oldest) so the audio thread never blocks.

What it cannot provide: G-force/attitude, landing gear, countermeasure, and decouple state are not recoverable from audio and are left unset. Accurate values require a supported semantic telemetry interface.

Known cross-talk: a single mixed stream means loud broadband sounds bleed across channels (sustained gunfire raises atmosphere; explosions fire both impact and weapon; the onset of engine rumble fires a one-shot impact). The `ForceFeedbackController` debounce windows absorb most of this. Tightening it further requires tuning against real game audio, not synthetic tones.

Tuning: the band edges, dB windows, and flux thresholds are named constants at the top of `AudioTelemetryAnalyzer`. `GetDiagnosticsAsync` reports the live engine/air dB, the impact/weapon flux ratios, and the latest signal values — use those readings to set `MOZA_SC_AUDIO_GAIN` and, if needed, adjust the constants.

Context gating: by default the audio path only drives feedback when Star Citizen is in an active flight context, so menu music or other audio can't cause phantom FFB. `GameLogContextWatcher` tails `Game.log` and tracks "a vehicle was spawned" vs. "returned to menu / shut down," falling back to "is `StarCitizen.exe` running" when no log is found. It **fails open** — feedback is allowed whenever context is uncertain, so real flight is never wrongly muted; it only suppresses on high-confidence non-flight states. Disable with `MOZA_SC_CONTEXT_GATE=0`; set `MOZA_SC_GAMELOG` to the log path if auto-detection misses your install.

## D-BOX HaptiSync

`DBoxHaptiSyncTelemetrySource` connects to:

```text
http://localhost:42010/
```

It probes common Swagger/OpenAPI JSON locations and scans available paths for telemetry-like endpoints. The current public HaptiSync API is useful for discovery because it proves the D-BOX software is installed and reachable, but it appears to control haptic settings rather than expose raw Star Citizen telemetry frames.

No process injection, memory reading, or packet interception is used.

`DBoxHaptiSync` is a legacy, explicit discovery mode for HaptiSync's documented
local HTTP surface. It is not selected by `Auto` and is not used by the SDK
sample-log workflow below.

## D-BOX SDK sample-log replay

`DBoxSdkSampleLogTelemetrySource` is an offline validation source. It reads one
explicitly selected XML log created by the official D-BOX `SampleRacer` or
`SampleFlyer` SDK program. It refuses all other application keys, including
`StarCitizen`, and it never searches the SDK, game folders, ProgramData, running
processes, services, the registry, or network endpoints.

Run the sanitized parser/mapper self-test:

```powershell
.\tools\dbox-log-inspect\bin\Release\net8.0-windows\DBoxLogInspect.exe --self-test
```

Validate a selected sample log without launching the app:

```powershell
.\scripts\replay-dbox-sdk-sample-log.ps1 -LogPath "D:\path\to\dbxLive64_sample.log" -ValidateOnly
```

Replay it with Preview output:

```powershell
.\scripts\replay-dbox-sdk-sample-log.ps1 -LogPath "D:\path\to\dbxLive64_sample.log"
```

The activity feed renders concise sample-provenance summaries, including
zero-valued boost and gear transitions and lifecycle resets. This source has a
`VisualizationOnly` output policy: it forces a null Preview device and bypasses
the force-feedback controller regardless of the configured hardware-output
mode. It holds the finished log read-only, validates the entire file with the
same strict session rules used by the inspector and observer, and yields no
Preview frames unless that preflight succeeds through terminal `Terminate`.

Follow one already-existing sample-format file as strict NDJSON without launching
the app or producer:

```powershell
.\scripts\observe-dbox-sdk-sample-log.ps1 -LogPath "D:\path\to\dbxLive64_sample.log"
```

This validates the schema-aware parser and normalized frame model. It does not
capture live Star Citizen data or turn the
sample-only G/load/engine/boost/impact/gear meanings into hardware effects. Full
instructions and mappings are in
[`dbox-sdk-sample-replay.md`](dbox-sdk-sample-replay.md); the streaming boundary
and why a hardware-presence spoof is not itself a telemetry source are in
[`dbox-sdk-sample-observer.md`](dbox-sdk-sample-observer.md).

## Parked anti-cheat-adjacent research

Packet capture, local receiver impersonation, process/module inspection, handler
substitution, service proxying, and production D-BOX file changes are not part of
the supported workflow. Older scripts and research notes describing those
experiments are retained only as historical evidence and should not be run on an
EAC-protected installation. Live integration now depends on a sanctioned D-BOX or
CIG interface; draft requests are in
[`vendor-telemetry-access-request.md`](vendor-telemetry-access-request.md).

## Official HTTP

`HttpJsonTelemetrySource` is a compatibility hook for a future public Star Citizen telemetry endpoint. Configure it with:

```powershell
$env:MOZA_SC_TELEMETRY="OfficialHttp"
$env:MOZA_SC_TELEMETRY_URL="http://localhost:12345/telemetry"
```

The mapper accepts loose JSON field names for early testing. It normalizes likely signal names into engine rumble, atmosphere, G-load, boost, afterburner, impact, weapon fire, countermeasure, landing gear, and decouple/couple state.
