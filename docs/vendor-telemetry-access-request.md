# Vendor request drafts: sanctioned Star Citizen telemetry access

These drafts request an expressly authorized, read-only interface for translating Star Citizen vehicle telemetry into force-feedback effects on a MOZA AB6 base. They do not request D-BOX Motion Code, proprietary haptic content, control of the game, or access to other players' data.

## Draft for D-BOX

**Subject:** Request for an approved Star Citizen telemetry observer interface for MOZA AB6 force feedback

Hello D-BOX Gaming / Live Motion team,

I am developing a personal, community-oriented adapter that would convert authorized Star Citizen vehicle telemetry into force-feedback effects for a MOZA AB6 base. RSI's [Star Citizen Alpha 4.8 release notes](https://robertsspaceindustries.com/en/comm-link/Patch-Notes/21168-Star-Citizen-Alpha-48) describe the first experimental D-BOX integration, and D-BOX's [Star Citizen page](https://www.d-box.com/star-citizen) confirms that the experience uses real-time telemetry.

Could D-BOX offer or approve a read-only observer, companion API, or other documented integration path for this use? We are seeking game facts and event notifications only—not D-BOX Motion Code, authored haptic assets, or proprietary effect definitions. If CIG owns the relevant interface or permissions, a referral to the appropriate partner contact or a jointly approved path would be very helpful.

For a robust adapter, we would appreciate documentation or guidance covering:

- Schema: field names, data types, units, coordinate frames, ranges, validity flags, vehicle/seat context, and availability by game mode.
- Timing: a monotonic timestamp, frame or sequence number, source/update timestamp, and guidance for pauses, gaps, reconnects, and stale samples.
- Rates: nominal and maximum update rates, whether delivery is per-frame or fixed-rate, expected jitter, batching, and back-pressure/drop behavior.
- Continuous values: linear/angular acceleration and velocity, orientation or gravity-relative vectors, engine/thruster state, and any other fields approved for physical feedback.
- Events: boost/afterburner, coupled/decoupled changes, landing gear, weapon recoil, missile and countermeasure launches, directional shield/hull hits, impacts, and explosions, including direction, intensity, duration, and unique event identifiers where available.
- Versioning: interface and schema identifiers, feature discovery, compatibility rules, deprecation policy, and a way to test against future Star Citizen builds.
- Safety and privacy: a one-way, read-only data surface limited to the local player's relevant vehicle experience, with no gameplay-control capability or sensitive account/network data.

Please also advise what agreement or license would apply. In particular, may an adapter's source and compiled binaries be distributed to the community; may required headers or runtime components be redistributed; what attribution, trademark, telemetry retention, and commercial-use restrictions apply; and is review or certification required before release?

Finally, we would follow only a deployment method explicitly approved by D-BOX and CIG. Please identify any required code signing, application registration, installer constraints, Easy Anti-Cheat review or allowlisting, validation environment, and support contact for keeping the adapter compliant across updates.

Thank you for considering a sanctioned way to extend Star Citizen's telemetry-driven physical feedback to additional cockpit hardware.

Best,

[Name / handle]  
[Project URL, if appropriate]  
[Contact information]

## Draft for Cloud Imperium Games

**Subject:** Request for a sanctioned read-only Star Citizen telemetry API for MOZA AB6 force feedback

Hello Cloud Imperium Games developer relations / vehicle systems team,

I am developing a personal, community-oriented adapter that would translate authorized Star Citizen vehicle telemetry into force-feedback effects for a MOZA AB6 base. The [Alpha 4.8 release notes](https://robertsspaceindustries.com/en/comm-link/Patch-Notes/21168-Star-Citizen-Alpha-48) describe the first experimental D-BOX SDK integration, say that the haptic system processes telemetry, and identify cockpit effects such as directional G-forces, engine activity, recoil, impacts, boost, landing gear, and countermeasures. D-BOX likewise describes its [Star Citizen integration](https://www.d-box.com/star-citizen) as using real-time telemetry.

Would CIG consider providing or approving a stable, opt-in, read-only telemetry/observer API for local accessibility and simulation peripherals? The requested interface would expose only data explicitly approved for the local player's physical-feedback experience. It would not send commands to the game or provide access to private account, network, or other-player information.

For a useful and maintainable interface, we would appreciate:

- A documented schema with field names, types, units, coordinate frames, ranges, validity flags, and vehicle/seat/game-mode scope.
- Monotonic timestamps plus frame or sequence identifiers, source timestamps where relevant, and defined pause, stale-data, reconnect, and discontinuity behavior.
- Nominal and maximum update rates, delivery cadence, expected jitter, batching, and sample/event loss semantics.
- Approved continuous signals such as linear/angular acceleration and velocity, orientation or gravity-relative vectors, and engine/thruster state.
- Approved events such as boost/afterburner, coupled/decoupled state, landing gear, weapon recoil, missile/countermeasure launches, directional shield/hull hits, impacts, and explosions, with direction, intensity, duration, and unique identifiers where appropriate.
- API/schema version identifiers, capability discovery, compatibility and deprecation policy, and access to an appropriate test environment or recorded fixtures.

Could you also clarify the licensing and distribution model: whether a community adapter may be open source and distributed in compiled form; whether an SDK, generated bindings, or runtime may be redistributed; applicable attribution/trademark and telemetry retention rules; permitted personal, non-commercial, and commercial uses; and any required developer agreement or release review?

Anti-cheat compliance is a hard requirement. RSI explains that Easy Anti-Cheat restricts unauthorized third-party software in its [official EAC guidance](https://support.robertsspaceindustries.com/hc/en-us/articles/4412282759191-Easy-Anti-Cheat). Please specify the approved application architecture and installation method, whether code signing or application registration is required, how to request EAC compatibility review or allowlisting, which environments may be used for testing, and how developers should revalidate after game or anti-cheat updates. We would not ship until CIG confirms the integration path is permitted.

If a general API is not currently planned, would CIG consider a narrowly scoped peripheral partner program, documented local telemetry service, or reviewed proof-of-concept for force-feedback hardware? I would be glad to provide a concise data-requirements document and prototype design for review before implementation.

Thank you for supporting the sim-rig and custom-cockpit community.

Best,

[Name / handle]  
[Project URL, if appropriate]  
[Contact information]

## Official public references

- [Star Citizen Alpha 4.8 release notes — Experimental D-BOX Haptic Support](https://robertsspaceindustries.com/en/comm-link/Patch-Notes/21168-Star-Citizen-Alpha-48)
- [D-BOX: Star Citizen](https://www.d-box.com/star-citizen)
- [D-BOX Live Motion SDK download and description](https://www.d-box.com/en/software-downloads-0)
- [D-BOX Coded Gaming Mode](https://support.d-box.com/en/knowledge/hsc-dbox-coded-gaming)
- [RSI: Easy Anti-Cheat](https://support.robertsspaceindustries.com/hc/en-us/articles/4412282759191-Easy-Anti-Cheat)
