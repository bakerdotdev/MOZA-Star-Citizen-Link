# D-BOX SDK sample-log observer

This observer follows one explicitly named, already-existing XML log in the
format written by the D-BOX SDK's `SampleRacer` and `SampleFlyer` examples. It
reads from byte zero, waits at a temporary end of file, and emits each complete
SDK call as one line of NDJSON. Mapped calls also include a normalized telemetry
frame.

This is a safe development observer for the SDK sample format. It is **not yet a
live Star Citizen telemetry observer**.

## Boundary

The observer:

- opens only the absolute local file path supplied by the user;
- never searches for logs or follows a replacement file;
- does not launch an SDK sample, the app, Star Citizen, EAC, or HaptiSync;
- does not discover, attach to, inject into, or modify any process;
- does not install or replace D-BOX handlers, services, drivers, or hardware;
- does not query the registry, local ports, or network endpoints, and does not
  search `ProgramData`, game folders, or EAC folders;
- does not change an OS setting, require elevation, or require a restart; and
- does not send force-feedback commands to the MOZA base.

UNC paths, mapped network drives, alternate data streams, symlinks, junctions,
mount points, and other reparse paths are rejected. The file must exist before
the observer starts. It is held read-only without delete sharing for the session.
That explicit file is the observer's sole data read: do not select a path inside
Star Citizen, EAC, HaptiSync, or another installed production component. Path
labels cannot establish provenance, so the observer cannot enforce such a
semantic distinction beyond rejecting risky filesystem path types.

## Build and run

Build from already-restored dependencies:

```powershell
dotnet build tools\dbox-log-inspect\DBoxLogInspect.csproj --configuration Release --no-restore
```

Follow an existing log:

```powershell
.\scripts\observe-dbox-sdk-sample-log.ps1 `
  -LogPath "D:\path\to\dbxLive64_sample.log"
```

The default idle timeout is 30 seconds. It applies only while no new bytes are
arriving and no valid `Terminate` has completed the run:

```powershell
.\scripts\observe-dbox-sdk-sample-log.ps1 `
  -LogPath "D:\path\to\dbxLive64_sample.log" `
  -IdleTimeoutSeconds 120
```

The wrapper launches only the repository's standalone observer executable. It
does not create the log or start its producer. Status goes to stderr; stdout is
NDJSON so it can be redirected to a user-selected development tool.

## Output

Each line contains:

- a contiguous `sequence` and local `observedUtc` timestamp;
- the SDK `elapsedMilliseconds`, method, method ID, application label, build, event key,
  event meaning, schema fields, and posted values; and
- `normalizedFrame` when the schema-aware mapper produces one.

The SDK `ApiKey` is deliberately not emitted. The `AppKey` is retained as a
provenance label, but it is self-asserted XML—not cryptographic proof that D-BOX
created the file.

The observer uses the same strict record/session validator as the offline
inspector and app replay. It fails closed on malformed XML, comments or
processing instructions, unknown methods, incorrect or missing method IDs,
unsupported application labels, schema/payload errors, lifecycle-order errors,
timestamp regression, duplicate initialization, records after termination,
missing termination, and resource limits. Its streaming layer additionally
rejects file truncation and times out an idle, incomplete run.
It accepts only the official sample labels `SampleRacer` and `SampleFlyer`.

Because NDJSON is streamed, records already emitted remain visible if a later
record makes the run invalid. App replay has a different output guarantee: it
prevalidates a stable, read-locked finished file in full before yielding any
Preview frames.

## What this tells us about the hardware spoof

The existing hardware-presence spoof may be enough to make HaptiSync select a
coded-game path, but it does not itself expose the producer-to-HaptiSync event
stream. This observer becomes useful only if a sanctioned D-BOX component writes
that stream to the explicit XML file.

The public SDK exposes producer calls such as `Initialize`, `RegisterEvent`, and
`PostEvent`; it does not expose an external listener/subscription API. Its XML
Logger is a development replacement handler used by an SDK sample, not a tee for
an independently running coded game. Consequently, pointing this observer at a
sample log proves the streaming parser and telemetry mapping, while pointing it
at the current spoof cannot manufacture missing Star Citizen records.

A genuine live Star Citizen path still requires one of these vendor-supported
seams:

1. CIG mirrors its D-BOX producer calls to a documented local file or API.
2. D-BOX supplies a signed tee/observer handler that forwards calls to HaptiSync
   while exposing a read-only copy.
3. D-BOX exposes a supported subscription endpoint in HaptiSync.

The copy-ready request is in
[`vendor-telemetry-access-request.md`](vendor-telemetry-access-request.md).
