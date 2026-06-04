# Telemetry Inputs

The app is designed around `IStarCitizenTelemetrySource`. A source initializes itself, exposes diagnostics, and streams `StarCitizenTelemetryFrame` values. The force-feedback controller only depends on those normalized frames, so future telemetry channels can be added without rewriting the AB6 output path.

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
