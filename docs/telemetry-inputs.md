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

What it cannot provide: G-force/attitude, landing gear, countermeasure, and decouple state are not recoverable from audio and are left unset. Those would come from the optional `Game.log` augmentation or from the D-BOX coded-telemetry path (see `dbox-telemetry-research.md`).

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

## D-BOX Discovery Runs

Use the discovery script when testing whether a newer Star Citizen or D-BOX build exposes a usable local telemetry path.

For the most useful run, close Star Citizen first, start an elevated PowerShell, then run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\collect-dbox-discovery.ps1 -CaptureFirst -EnablePacketTrace -InstallPktMonPortFilters -PacketTraceSeconds 180 -SampleSeconds 60
```

If you are not already in an elevated shell, this helper opens the capture in a UAC-elevated PowerShell window:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\start-dbox-discovery-elevated.ps1
```

While the `pktmon capture active` message is visible, launch Star Citizen and trigger known haptic events: ship engine idle, boost/afterburner, landing gear, weapon fire, countermeasure, and a small landing/impact if practical.

The script writes:

- `artifacts\dbox-discovery-*.txt` - D-BOX API probes, process/port inventory, logs, and Monitoring Service XML replies.
- `artifacts\dbox-discovery-*-pktmon.txt` - brief packet summary.
- `artifacts\dbox-discovery-*-pktmon-hex.txt` - packet text with hex dumps for quick payload checks.
- `artifacts\dbox-discovery-*-pktmon.pcapng` - full packet capture for Wireshark-style inspection.

The Monitoring Service probe talks to `127.0.0.1:40001` using D-BOX's documented XML commands (`GetVersion`, `GetLayout`, `GetStatus`, `GetSoftwareParameter`). That port is expected to expose hardware/service state rather than raw Star Citizen frames, but it helps prove whether D-BOX sees a platform and whether any stream-related status changes are visible.

## D-BOX Receiver Port Probe

`LiveMotionConnector.config` points at `127.0.0.1:61666`, but discovery runs have not shown a listener there. To test whether the Star Citizen/D-BOX LiveMotion code attempts to connect to that configured receiver, close Star Citizen and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\probe-dbox-receiver-port.ps1 -DurationSeconds 240
```

Launch Star Citizen while the probe is active and trigger the same haptic events. If anything connects or sends a UDP datagram to `61666`, the probe writes ASCII and hex payload previews to `artifacts\dbox-receiver-probe-*.txt`.

This probe temporarily occupies `127.0.0.1:61666`, so use it only for discovery, not normal D-BOX use.

## Official HTTP

`HttpJsonTelemetrySource` is a compatibility hook for a future public Star Citizen telemetry endpoint. Configure it with:

```powershell
$env:MOZA_SC_TELEMETRY="OfficialHttp"
$env:MOZA_SC_TELEMETRY_URL="http://localhost:12345/telemetry"
```

The mapper accepts loose JSON field names for early testing. It normalizes likely signal names into engine rumble, atmosphere, G-load, boost, afterburner, impact, weapon fire, countermeasure, landing gear, and decouple/couple state.
