# DirectInput Output

The app uses Windows DirectInput force feedback for AB6 hardware output. It enumerates attached force-feedback game controllers, prefers devices with `MOZA`, `AB6`, or `AB9` in the name, sets the simple two-axis absolute joystick data format, acquires the device, and creates DirectInput effects from telemetry-derived force requests.

Current telemetry mappings:

- Engine rumble: sustained sine periodic effect with telemetry-controlled intensity/frequency.
- Atmosphere/turbulence: sustained sine periodic effect at low intensity.
- G-load: sustained sine periodic effect representing load pressure.
- Boost/afterburner: short constant-force bump.
- Impact/explosion/damage: short constant-force bump.
- Weapon fire: short constant-force recoil pulse.
- Landing gear/countermeasure: short periodic mechanical pulse.
- Decouple/couple: short constant-force confirmation bump.

DirectInput effect intensity is clamped to the normal `0..10000` force-feedback range. Sustained effects use state keys so a new telemetry state update replaces the old effect instead of stacking force indefinitely.

The MOZA wheelbase SDK output path was removed from active selection because the tested SDK APIs are wheelbase-specific and do not control the AB6. If the AB6 does not advertise DirectInput force feedback, the app falls back to preview output.
