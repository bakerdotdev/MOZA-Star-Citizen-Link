# MOZA Star Citizen Telemetry

Windows desktop app that maps Star Citizen telemetry signals to force-feedback effects on a MOZA AB6 FFB flight base.

The app is now telemetry-first. The old `Game.log` parser and experimental screen-capture detector have been removed.

The default input path derives force-feedback signals from Star Citizen's own audio: it captures the Windows render endpoint with WASAPI loopback and runs lightweight DSP (band energy + spectral-flux onset detection) to produce engine rumble, atmosphere, and impact/weapon transients. This needs no D-BOX software, touches neither `StarCitizen.exe` nor any anti-cheat surface, and ships as a single self-contained `.exe`. The trade-off is that audio cannot carry true motion data (G-force, attitude) or discrete state (gear, decouple), so those effects are not produced from this source.

The app also retains a D-BOX HaptiSync discovery path and a configurable JSON telemetry URL for any official/public channel CIG exposes later. The deeper goal of intercepting Star Citizen's native D-BOX telemetry (for true motion data) is a parked research thread documented in `docs/dbox-handoff.md` — it needs access to real D-BOX hardware to finish.

## Current Status

The working hardware output path is Windows DirectInput force feedback. The AB6 should appear to Windows as:

```text
MOZA AB6 FFB Base
```

D-BOX HaptiSync Center 1.3.0 added a local REST API at:

```text
http://localhost:42010/index.html
```

That public API currently appears to expose haptic settings/control, not raw Star Citizen telemetry frames. This app probes the local Swagger/OpenAPI surface and will start reading a likely telemetry endpoint if one appears. Until then, the D-BOX path is a discovery/watch path rather than a confirmed telemetry feed.

## Download And Run

For normal users, download the portable release ZIP from this repo's GitHub Releases page. Do not use GitHub's "Source code" ZIP if you only want to run the app.

Extract the portable ZIP and run:

```text
Run-Auto.cmd
```

Launchers:

- `Run-Auto.cmd` - recommended; audio-DSP telemetry with DirectInput AB6 output
- `Run-Audio.cmd` - force the audio-DSP telemetry path with DirectInput output
- `Run-DirectInput.cmd` - force Windows DirectInput output
- `Run-DBoxTelemetry.cmd` - force D-BOX HaptiSync telemetry discovery mode
- `Run-Preview.cmd` - no hardware output

No installer is required. The release build is self-contained and does not require users to install the .NET runtime.

## Telemetry Inputs

Default mode:

```text
MOZA_SC_TELEMETRY=Auto
```

Supported modes:

```text
Auto
AudioDsp
DBoxHaptiSync
OfficialHttp
Preview
```

`Auto` resolves to `AudioDsp` (unless a telemetry URL is configured, in which case it uses the HTTP source).

### Audio DSP (default)

```text
MOZA_SC_TELEMETRY=AudioDsp
```

Captures the default render device via WASAPI loopback and maps the audio to engine rumble, atmosphere, and impact/weapon transients. Optional environment variables:

```text
MOZA_SC_AUDIO_GAIN=1.0      # input sensitivity multiplier; raise/lower to calibrate
MOZA_SC_AUDIO_DEVICE=       # substring of a render device name; default = default device
MOZA_SC_CONTEXT_GATE=1      # default on: only drive FFB when SC is in active flight; 0/off to disable
MOZA_SC_GAMELOG=            # path to SC Game.log; auto-detected if blank
```

The context gate suppresses force feedback when Star Citizen is at the menu, on foot, or closed (so menu music can't cause phantom rumble), and re-enables it when you're flying. It fails open — if it can't tell, it leaves feedback on.

Calibration: click `Refresh` in the app while Star Citizen is making noise. The diagnostics show the live engine/air levels in dB and the impact/weapon flux ratios, plus the latest derived signal values. Adjust `MOZA_SC_AUDIO_GAIN` until engine rumble sits near the top of its range under thrust without pegging at idle.

Limitations: because the signal is a single mixed audio stream, loud broadband sounds can cross-trigger (e.g., sustained gunfire nudges the atmosphere channel; an explosion fires both impact and weapon). The capture is the whole system mix, so other loud audio (music, Discord) also feeds it — keep those quiet, or point `MOZA_SC_AUDIO_DEVICE` at a render device used only by the game.

To test a future official/public JSON telemetry endpoint:

```powershell
$env:MOZA_SC_TELEMETRY="OfficialHttp"
$env:MOZA_SC_TELEMETRY_URL="http://localhost:12345/telemetry"
dotnet run --project src\MozaStarCitizen.App\MozaStarCitizen.App.csproj
```

The JSON mapper intentionally accepts loose field names so early telemetry payloads can be tested without recompiling. It looks for signal names such as `engineRumble`, `engineVibration`, `thrust`, `boost`, `afterburner`, `impact`, `weaponFire`, `landingGear`, `countermeasure`, `decoupled`, and G-force/acceleration fields.

## Force Feedback Mapping

Current telemetry-driven effects:

- Engine rumble: sustained periodic vibration
- Atmosphere/turbulence: sustained low-amplitude vibration
- G-load: sustained pressure vibration
- Boost/afterburner: transient kick plus stronger engine rumble
- Impact/explosion/damage: short bump
- Weapon fire: short recoil pulse
- Landing gear/countermeasure: short mechanical pulse
- Decouple/couple change: short confirmation bump

The `Stop Effects` button stops all sustained and transient effects.

## Diagnostics

Click `Refresh` in the app to see:

- Selected telemetry mode and source status
- D-BOX HaptiSync API discovery results
- Selected output mode
- DirectInput game controllers
- DirectInput force-feedback devices

For AB6 output to work, diagnostics should list `MOZA AB6 FFB Base` under DirectInput force-feedback devices.

Runtime logs are written to:

```text
%LOCALAPPDATA%\MozaStarCitizen\app.log
```

For deeper D-BOX telemetry discovery, use:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\collect-dbox-discovery.ps1 -CaptureFirst -EnablePacketTrace -InstallPktMonPortFilters -PacketTraceSeconds 180 -SampleSeconds 60
```

Run that from an elevated PowerShell with Star Citizen closed, then launch Star Citizen while capture is active and trigger engine, boost, weapon, gear, countermeasure, and impact events. If you are not already elevated, `scripts\start-dbox-discovery-elevated.ps1` opens the same capture in a UAC-elevated PowerShell window. The script writes a discovery text report plus packet summary, hex dump, and PCAPNG files under `artifacts\`.

To directly test the configured D-BOX LiveMotion receiver port, close Star Citizen and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\probe-dbox-receiver-port.ps1 -DurationSeconds 240
```

Then launch Star Citizen while the probe is active. Any TCP/UDP bytes sent to `127.0.0.1:61666` are logged under `artifacts\`.

## Development

Requirements:

- Windows
- .NET SDK 8 or newer
- MOZA AB6 FFB Base for hardware testing
- Optional: D-BOX HaptiSync Center 1.3.0 or newer for API discovery

Build:

```powershell
dotnet build MozaStarCitizen.sln --configuration Release
```

Run from source:

```powershell
dotnet run --project src\MozaStarCitizen.App\MozaStarCitizen.App.csproj
```

Create a portable ZIP:

```powershell
.\scripts\package-portable.ps1
```

## Project Layout

```text
src/MozaStarCitizen.App/             WPF app
src/MozaStarCitizen.App/Telemetry/   D-BOX/API discovery and JSON telemetry readers
src/MozaStarCitizen.App/ForceFeedback/
                                     DirectInput, fallback, and preview output
scripts/package-portable.ps1         Self-contained Windows release package
docs/                                Lower-level implementation notes
```
