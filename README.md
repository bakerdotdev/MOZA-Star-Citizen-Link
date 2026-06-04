# MOZA Star Citizen Telemetry

Windows desktop app that maps Star Citizen telemetry signals to force-feedback effects on a MOZA AB6 FFB flight base.

The app is now telemetry-first. The old `Game.log` parser and experimental screen-capture detector have been removed. The current input path tries to discover Star Citizen telemetry through D-BOX HaptiSync's local API, and it also supports a configurable JSON telemetry URL for any official/public channel CIG exposes later.

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

- `Run-Auto.cmd` - recommended; D-BOX/API telemetry discovery with DirectInput AB6 output
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
DBoxHaptiSync
OfficialHttp
Preview
```

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
