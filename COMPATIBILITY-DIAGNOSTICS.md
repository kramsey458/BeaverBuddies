# Mod compatibility and rolling diagnostics

Available in **Preview 11 (1.1.0-stability.11)**. Install the same compiled archive
on every computer and restart Timberborn before joining.

## What is checked

Before transferring the map, peers compare:

- Timberborn and BeaverBuddies versions, including the module IDs of the loaded
  BeaverBuddies and TimberNet assemblies.
- Every enabled mod's manifest ID, version and load order.
- SHA-256 fingerprints of each enabled mod's DLL, JSON, asset bundle, CFG, INI
  and TOML files. Workshop presentation metadata is excluded. File paths are
  relative to each mod, so different installation folders are fine.
- Module IDs of matching assemblies actually loaded in the process. Timberborn
  loads DLL bytes, so this check does not depend on `Assembly.Location`.
- Registered Mod Settings values, hashed before they are sent. Mismatch messages
  identify the mod/setting key; they do not disclose its value.

Some mods register settings only in the game scene. After both maps load, a second
check compares the complete registered setting set. Simulation and local replayed
actions wait until this passes. A guest who loads first may wait for the host to
choose **Start game**. A waiting dialog explains this; the check times out after
ten minutes from the local map load.

Known BeaverBuddies preferences such as address/port, player name/color, activity,
logging, reporting consent and rolling diagnostics are local and excluded. The
host's recovery preference is also allowed to differ. Detailed trace mode is
checked because it changes replay traffic. Other mods' registered settings are
strictly compared, including their cosmetic options: matching those options avoids
guessing which third-party preferences affect simulation.

Profiles are captured at connection and map initialization, including automatic
snapshot reloads. Change other mods' gameplay settings in the main menu and rehost;
this is not a system for synchronizing live settings changes. Custom settings
registered without public properties use their registration ordinal as an identity.

This is a configuration equality check, not a certification that every third-party
mod is deterministic. Unregistered settings stored elsewhere, external data, and
unlisted file formats cannot be discovered generically. A mod can still have a
multiplayer bug when its versions and settings match. Fingerprinting runs during
join/load, with unchanged file hashes cached; it does not scan files every tick.

## Automatic local reports

**Rolling desync diagnostics** is enabled by default in Mod Settings. It does not
enable detailed tracing. At completed simulation tick boundaries it retains:

- Up to 120 checkpoints, taken every 20 ticks (roughly the last 2,400 ticks).
- RNG state, entity update/position hashes and entity/water-column counts.
- A rotating window of up to 64 entities: job/behavior and inventory fingerprints,
  including stock and reservations. Large inventories are explicitly marked as
  truncated (eight inventories and 64 goods per list per sampled entity).
- A rotating window of up to 256 water columns, including active status, depth,
  contamination, overflow and geometry using exact float bits.
- The latest 256 useful action summaries: type, tick, before/after/failure phase,
  RNG, target entity IDs and an argument fingerprint. Player-entered strings are
  hashed, and bulky tracing/ping/heartbeat events are excluded.
- Network backlog and event hash for context, not as a direct world-state checksum.

The buffers have fixed count and record-size limits. Sampling only reads state
after parallel work has finished. It never draws RNG values, edits the world,
changes command hashes or automatically decides that the world has desynced.
An error disables the recorder for that scene rather than failing multiplayer.

On a desync, replay failure or snapshot recovery request, a small control notice
asks connected peers to export their own history. Each scene exports at most once.
Compression and disk writing run in the background; pending writes are bounded.
Recovery can reload the map without erasing the captured report. If the connection
is already broken, a peer notice may not arrive, so collect any available reports
and both players' logs. Sudden process crashes cannot guarantee a report.

Reports are saved in:

```text
%USERPROFILE%/AppData/LocalLow/Mechanistry/Timberborn/BeaverBuddiesDiagnostics/
```

The exact path is also printed in `Player.log` as **Saving local rolling
diagnostics**. Files are named `rolling-<UTC timestamp>-<capture ID>.zip` and contain
`report.json`. The newest ten rolling ZIPs are retained; existing detailed water
diagnostic archives are left alone. Reports contain mod IDs, hashed configuration,
sampled game state and an error reason, not a full save. Error reasons may include
paths supplied by an exception. Nothing is uploaded automatically by this feature.

## Comparing reports

Send the host and guest ZIPs from the same incident, plus both `Player.log` files.
The same capture ID is useful; the shared snapshot SHA-256 and simulation ticks
also correlate independently triggered reports.

A Python 3 comparison utility is included in the install archive's `Diagnostics`
folder and in the source repository:

```sh
python RuntimeChecks/compare_rolling_diagnostics.py host-rolling.zip guest-rolling.zip
```

It finds the earliest differing shared checkpoint and reports RNG, job/inventory,
entity order or water differences and nearby commands. Different snapshot identities
and non-overlapping histories are reported explicitly. Samples can narrow a cause;
matching samples do not prove that unsampled state or intervening ticks matched.

## Validation and in-game check

Automated tests cover profiles, registered setting extraction, file changes, loaded
modules, culture independence, bounded buffers, read-only sampler behavior, report
writing/retention, and real TCP admission/rejection. Game-facing tests use native
API-shaped stubs; compiling against Timberborn 1.1.2.4 also verifies API signatures.
These checks do not substitute for a live two-player Unity playtest.

On a spare save:

1. Match mods and settings, join by direct IP and start the world. Confirm the
   compatibility wait closes and simulation advances.
2. Change a third-party registered setting on one PC in the main menu. Confirm
   joining is refused with its setting key, then restore the same value.
3. Play at least 40 ticks, choose **Resync from host**, and confirm both PCs create
   rolling ZIPs and recover with the new compatibility gate.
4. Compare the ZIPs and inspect their coverage and recorder-failure fields. Restore
   normal play and check that player activity and paused commands still work.
