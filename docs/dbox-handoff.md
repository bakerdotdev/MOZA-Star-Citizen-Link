# D-BOX Telemetry Interception — Handoff & Resume Plan

> **Safety status (2026-07-14): archived; do not resume these interception
> steps.** The current supported work is limited to
> [offline SDK sample replay](dbox-sdk-sample-replay.md) and obtaining a
> [vendor-sanctioned interface](vendor-telemetry-access-request.md). Do not use
> the process, service, port, packet, spoofing, or production-file procedures
> below with EAC.

> **Superseded on 2026-07-14:** The downloaded SDK disproved this document's
> architecture and hardware-blocker conclusion. StarCitizen.exe statically links
> the Live Motion wrapper and directly loads the signed app-specific
> dbxLive64.dll. The SDK's signed XML logger captured typed RegisterEvent and
> PostEvent data without hardware. Use
> [dbox-investigation-2026-07-14.md](dbox-investigation-2026-07-14.md) as the
> current source of truth.
>

> **Evidence warning (2026-07-14):** This handoff records the earlier
> investigation; it is not verified ground truth. The retained sweep stops at
> 17/840, no saved API response proves that enumeration succeeded, and the
> conclusion that real hardware is required is not established. Continue from
> [dbox-investigation-2026-07-14.md](dbox-investigation-2026-07-14.md), which
> audits these claims and gives the current investigation plan.

**Status: PARKED (2026-06-11) — software-only spoofing cannot open the gate. Resume when a real D-BOX rig is available (borrowed or bought).**

Goal: intercept the real-time telemetry Star Citizen sends to D-BOX (SC 4.8+ "Coded Gaming") and translate it into MOZA AB6 force feedback. This is the high-fidelity path (true motion data), as opposed to the shipping audio-DSP path (see `telemetry-inputs.md`).

This doc is the single resumable summary. Deeper background is in `dbox-telemetry-research.md`; live state notes are in the project memory (`dbox-telemetry-interception-state.md`).

---

## TL;DR

- We **cracked the hardware-enumeration gate** in pure software (no D-BOX hardware): a synthetic MonitorService makes D-BOX report a Haptic Bridge present. This is the part that previously blocked all progress.
- But SC still won't stream, because there are **two deeper gates** that need *real-device data we cannot fabricate from XML*: the **system-generation classifier** and a **MonitorServicePassthrough** hardware query.
- An ~840-config brute-force sweep found no spoof that satisfies them. The deciding logic lives in a **NativeAOT binary (not decompilable)**.
- **Conclusion:** finishing this requires capturing the protocol from a **real D-BOX device** (even briefly). With a real rig, the rest is straightforward — you stop spoofing and just *observe and decode*.

---

## What works (achieved this round)

- **Enumeration gate (DONE).** `scripts/start-monitor-service-responder.ps1` binds TCP `127.0.0.1:40001` and answers MonitorService's XML protocol (`GetLayout`, `GetStatus`, `GetSoftwareParameter`, `GetGenericAudioDevices`) with a synthetic Haptic Bridge → Platform → ACM → Actuator. **Confirmed live:** MotionEngine connects, accepts it, and HaptiSync's API (`GET http://localhost:42010/api/v1/haptic-systems`) reports the system. No hardware required for this layer.
- **The actuator-bridge is NOT the blocker.** MotionEngine only ever polls `GetStatus`/`GetLayout` on 40001 — it never sends motion or activation commands to the "bridge." So a WinUSB bridge emulator is unnecessary; don't build one.

## The wall (why it's parked)

Even with the system reported present, SC never streams and the system never becomes usable. Two gates remain, both requiring real-device data:

1. **System-generation classification.** HaptiSync (`GetSystemGenerationFromLayout` / `GetSystemGenerationFromCommUnit`) classifies our spoofed layout as **"System generation unknown"** — `commUnitTypeId`, `acmTypeId`, `acmModelId` all resolve `null` regardless of the values we put in the XML. The classifier reads these from somewhere our XML-only spoof doesn't populate (real-device identity), and its logic is compiled into **`HaptiSyncCenter.App.exe` (NativeAOT — not decompilable**; we can read strings/method names but not the logic).
2. **MonitorServicePassthrough.** For configs that get further, HaptiSync forwards a command down to MonitorService (our responder) and **times out** because our spoof can't produce the expected reply, so the haptic system never registers as present (API returns empty).

We swept ~840 layout permutations (`scripts/sweep-system-generation.ps1`): none produced a recognized, error-free, present system. **Config-spoofing alone is ruled out.**

---

## RESUME PLAN — when you have a real D-BOX rig

The project becomes tractable with a real device because you stop spoofing and start **observing the genuine protocol**.

**1. Get the minimum hardware.**
- A *complete* D-BOX chain: bridge/controller **+ ACM + at least one actuator**, generation-matched. A bare controller will NOT enumerate as a system.
- **Gen-1 is fine and cheapest** — HaptiSync still recognizes `G1_KAI` / `G1_KCU`. Newer (G3/G5) also fine.
- **Borrowing for a single capture session is ideal** (see "two payoffs" below — you may not need to keep it).
- Requires a D-BOX Connect account (free) + HaptiSync Center installed (already set up on this machine).

**2. Capture the real protocol** (with the rig connected and HaptiSync showing a healthy system):
- **MonitorService side (TCP 40001):** capture what a *real* bridge reports for `GetLayout`/`GetStatus` AND the full `MonitorServicePassthrough` exchange. This reveals the exact type/model/identity fields and the passthrough protocol our spoof is missing. Tool: `tools/loopback-sniff/` (raw-socket loopback sniffer — pktmon and RawCap can't capture localhost; this one can). Or run our responder as a logging *proxy* in front of the real MonitorService (see `scripts/swap-monitor-port.ps1`).
- **SC telemetry (the prize):** launch SC — it WILL stream now that the system is genuinely active — and capture `StarCitizen.exe → MotionEngine` on the game ports (**TCP 12740/12745/61555, UDP 61556**; SC's source is UDP **64090**) with `tools/loopback-sniff/`. Decode this format → drive MOZA FFB. (This is the original "Gate C".)

**3. Two payoffs from one capture:**
- **Decode the SC telemetry** → map to `StarCitizenTelemetryFrame` and reuse the existing FFB output. This is the goal.
- **Replicate the captured MonitorService protocol** (real identity + passthrough responses) in `start-monitor-service-responder.ps1` → potentially run **hardware-free afterward** (capture once, return/resell the rig). Viability depends on whether the protocol is static or has a live challenge-response — check the capture for any non-replayable handshake before assuming.

---

## Tools built (all in this repo)

| Tool | Purpose |
|---|---|
| `scripts/start-monitor-service-responder.ps1` | Synthetic MonitorService XML spoof on 40001 (the enumeration-gate crack). Takes CLI params for comm-unit/ACM/actuator/config. |
| `tools/loopback-sniff/` | Raw-socket `127.0.0.1` sniffer (`LoopbackSniff.exe`). **Use this to capture real traffic** — pktmon/RawCap can't do loopback here. |
| `scripts/sweep-system-generation.ps1` | Automated config sweep + robust detection (proved spoofing is a dead end). |
| `tools/aot-analyze/` | NativeAOT PE/string/xref analyzer (used to confirm the classifier is non-decompilable). |
| `scripts/watch-motionengine-clients.ps1` | Watches MotionEngine's listener ports for SC connecting (EAC-safe, from the server side). |
| `artifacts/dbox-decompile/` | Decompiled managed DLLs (`HWMonitor.Data` = the XML→object parser; the real schema). |

## Key facts (don't re-derive)

- **Recognized system generations** (from HaptiSyncCenter assets): `G1_KAI`, `G1_KCU`, `G2`, `G3`, `G3FLEX_G5_MIX`, `G5`, `G5_KCU` (× actuator/ACM counts).
- **Real bridge = KCU-1X.** Actuator-model enum (in `MonitorService.exe` strings): AC13/AC218/AC230/AC231/AC330/**AC360** AKM32D etc.; ACM types `ACM G3 FLEX`, `ACM-II`, `ACM-Lite`.
- **Ports:** MotionEngine listens TCP 12740/12745/61555 (12740/12745 are used by HaptiSync's own components), UDP 61556. SC opens UDP 64090.
- **EAC hides SC's socket ownership** — always observe SC↔MotionEngine from MotionEngine's side.
- SC's D-BOX support is **native (CodedGaming)**; there is **no public SC telemetry API** (confirmed). If CIG ever exposes one, the `OfficialHttp` source hook is already in the app.
- The XML parser (`HWMonitor.Data.dll`) reads layout attributes: `TypeId`, `TypeName`, `ModelName`/`Model`, `ConfigurationCode`, `Serial`, `Index`, etc. (decompiled — see `artifacts/dbox-decompile/HWMonitor.Data.cs`).
- Distribution caveat: spoofing/emulating D-BOX hardware is a EULA matter. Personal use / content creation is the assumed scope here; revisit before any public release.

## Gotchas for whoever resumes

- The D-BOX stack can drift into a `MonitorServicePassthrough timed out` state after many rapid responder restarts — **reboot** to restore it to a healthy baseline.
- HaptiSync Center must be **open** for the classifier to run.
- `MonitorService.exe` is **native** (not decompilable); the managed parsers/classifiers are in `HaptiSync Center\*.dll` (decompilable) and `HaptiSyncCenter.App.exe` (NativeAOT — strings only).
