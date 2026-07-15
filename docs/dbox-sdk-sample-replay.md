# D-BOX SDK sample-log replay

This is the safe, offline development path for the D-BOX telemetry translator. It
reads an explicit XML log produced by the official `SampleRacer` or `SampleFlyer`
SDK executable and converts the documented sample meanings into the app's
normalized telemetry frames.

It is intentionally **not** a Star Citizen capture mechanism. The reader rejects
every other D-BOX `AppKey`, including `StarCitizen`.

## Safety boundary

This path:

- reads only the log file supplied on the command line;
- does not launch, inspect, attach to, or modify Star Citizen or EAC;
- does not read or replace a D-BOX production handler;
- does not contact HaptiSync, D-BOX services, local ports, or network endpoints;
- does not inspect processes, services, drivers, or the registry;
- does not require elevation, an OS setting change, or a restart; and
- defaults to the app's Preview output, so it does not command the AB6.

The repository contains no D-BOX SDK binaries, source, manuals, or captured
sample logs. Keep the downloaded SDK and its generated logs local.

## 1. Produce an official sample log

With the SDK extracted at `D:\DBOX-SDK`, use one of its standalone samples:

```powershell
Set-Location D:\DBOX-SDK\LiveMotionSdk\Samples
.\SampleRacer64.exe
```

or:

```powershell
Set-Location D:\DBOX-SDK\LiveMotionSdk\Samples
.\SampleFlyer64.exe
```

The SDK's signed XML Logger writes a `dbxLive64_*.log` file in the sample's
working directory. These sample programs do not need Star Citizen, EAC, or D-BOX
hardware. Exit the sample normally after it completes.

## 2. Inspect and validate offline

Build the Release binaries from already-restored dependencies:

```powershell
dotnet build src\MozaStarCitizen.App\MozaStarCitizen.App.csproj --configuration Release --no-restore
dotnet build tools\dbox-log-inspect\DBoxLogInspect.csproj --configuration Release --no-restore
```

The inspector has a sanitized, generated-in-code self-test:

```powershell
.\tools\dbox-log-inspect\bin\Release\net8.0-windows\DBoxLogInspect.exe --self-test
```

Inspect a local official sample log and print its normalized frames as JSON:

```powershell
.\tools\dbox-log-inspect\bin\Release\net8.0-windows\DBoxLogInspect.exe "D:\path\to\dbxLive64_sample.log"
```

The command exits with an error if the log identifies any application other than
`SampleRacer` or `SampleFlyer`.

To follow the same format while an authorized SDK producer is appending it, use
the standalone read-only observer. It neither starts nor discovers the producer:

```powershell
.\scripts\observe-dbox-sdk-sample-log.ps1 `
  -LogPath "D:\path\to\dbxLive64_sample.log"
```

Its stricter streaming boundary and the remaining live-capture limitation are
documented in
[`dbox-sdk-sample-observer.md`](dbox-sdk-sample-observer.md).

## 3. Validate app replay configuration

This checks the path and allowed `AppKey` but does not launch the app:

```powershell
.\scripts\replay-dbox-sdk-sample-log.ps1 `
  -LogPath "D:\path\to\dbxLive64_sample.log" `
  -ValidateOnly
```

To replay into the app's Preview output:

```powershell
.\scripts\replay-dbox-sdk-sample-log.ps1 `
  -LogPath "D:\path\to\dbxLive64_sample.log"
```

`-Speed 0` replays without timestamp delays. Values from `0.01` through `100`
select a replay multiplier. Replay is finite: the source does not search for or
follow other logs, and it forces a final neutral frame at end of file.

The script sets environment variables only for its current process:

```text
MOZA_SC_TELEMETRY=DBoxSdkSampleLog
MOZA_SC_DBOX_XML_LOG=<explicit path>
MOZA_SC_DBOX_REPLAY_SPEED=1
MOZA_SC_OUTPUT=Preview
```

## Current mappings

The mapper uses numeric SDK meanings and the `RegisterEvent` field order; it does
not infer semantics from transient event keys.

| SDK sample meaning | Normalized value |
|---|---|
| `ACTOR_GFORCE_XYZ` | Lateral, vertical, and longitudinal G directly |
| `ACCELERATION_XYZ` | Same axes divided by standard gravity (`9.80665`) |
| `ENGINE_RPM` + `ENGINE_RPM_MAX` | Engine rumble and shaft frequency |
| `ENGINE1_N1` | Engine rumble (the value assigned by the official Flyer sample) |
| `ENGINE_BOOST` / boost start and stop | Generic boost |
| `IMPACT` + event intensity | One-frame impact pulse |
| `LANDING_GEAR_GENERAL_DEPLOYMENT` | Landing-gear deployment |

Lifecycle records clear sustained state. Unknown meanings remain unmapped and are
counted in diagnostics. Generic sample boost is **not** relabeled as Star Citizen
afterburner.

## Known limitation

This milestone validates `sample XML -> schema-aware parser -> normalized frame`.
The current force-feedback controller intentionally consumes only the audio
source's `Afterburner` and `Atmosphere` signals. The verified SDK samples do not
provide either semantic, so replay is presently a Preview/diagnostics tool and
will not create visible AB6 effects. Adding sample-driven test effects is a
separate change and should preserve source provenance rather than weakening the
semantic mapping.

The sample log is untrusted input: its `AppKey` is self-asserted rather than
cryptographically authenticated. The launcher therefore performs a full offline
schema/lifecycle validation and always forces Preview output.

Live Star Citizen integration remains parked until D-BOX or CIG provides a
supported observer, connector, or telemetry interface. See
[`dbox-sdk-sample-observer.md`](dbox-sdk-sample-observer.md) and
[`vendor-telemetry-access-request.md`](vendor-telemetry-access-request.md).
