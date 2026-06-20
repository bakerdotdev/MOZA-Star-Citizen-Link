# audio-tune

Offline tuning harness for the audio-DSP force-feedback detection. It records
Star Citizen's loopback audio to a WAV and replays it through the **exact**
production analyzer (`AudioTelemetryAnalyzer.cs` is shared via a linked compile,
so there's no drift between what we tune and what ships), writing a per-window
feature timeline to CSV.

The point: audio detection is empirical. Instead of guessing thresholds and
re-flying for every tweak, capture one flight, then iterate in seconds —
edit the analyzer, rebuild, replay, diff the CSV.

## Pick the capture device

```powershell
dotnet run --project tools/audio-tune -c Release -- list
```

Lists render (playback) endpoints. Loopback captures whatever is *played to* an
endpoint. Two strategies:

- **Full mix (simplest):** capture the default device — gets the game plus any
  other audio (music, comms, system). If the default is a pro interface held in
  exclusive mode (Voicemeeter/ASIO/an interface console), either disable
  "Allow applications to take exclusive control" in Windows Sound > device
  Properties > Advanced, or pick a different endpoint below.
- **Game-only (cleanest):** point SC's audio output at a dedicated virtual
  device (a Voicemeeter input / VB-Cable) that nothing else uses, then capture
  that. Far less cross-talk. Set `MOZA_SC_AUDIO_DEVICE` to a substring of its
  name (this is the same env var the shipping app uses).

## Capture a flight

Star Citizen must be **playing audio** — WASAPI loopback delivers nothing while
the system is silent.

```powershell
# 120s capture of the default device (set MOZA_SC_AUDIO_DEVICE first to pick another)
dotnet run --project tools/audio-tune -c Release -- record artifacts/audio-tune/flight2.wav 120
```

While it counts down, tab to SC and fly. **Mark real events as they happen** so
detections can be scored against ground truth — hold **Ctrl+Alt** and press:

- **1** = firing weapons   **2** = boost/afterburner   **3** = impact / taking a hit

These global hotkeys work while SC is focused and are written with timestamps to
`<wav>.markers.csv`. Anything the detector fires *outside* a marked window is a
false positive; marked windows with no detection are misses.

## Replay through the analyzer

```powershell
dotnet run --project tools/audio-tune -c Release -- replay artifacts/audio-tune/flight1.wav
# -> artifacts/audio-tune/flight1.features.csv  (+ a peak/hit summary on stdout)
```

Optional args: `replay <in.wav> [out.csv] [gain]`. `gain` mirrors
`MOZA_SC_AUDIO_GAIN` so you can see its effect without launching the app.

## CSV columns

`t_sec, engineRumble, engineHz, atmosphere, boost, impact, weapon, engineDb,
airDb, impactRatio, weaponRatio`

The first seven are the emitted `AudioFeatures`; the last four are the analyzer's
internal diagnostics (band levels in dB, onset flux ratios) — they're what you
set thresholds against. Correlate spikes against your event notes to judge true
vs. false detections.
