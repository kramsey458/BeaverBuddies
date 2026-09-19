# Stability previews: cumulative changelog

## Preview 15 UI hotfix — 1.1.0-stability.15-ui.1

- Fix overlapping status-panel text with explicit label and row sizing.
- Preserve collapse and hide controls; restore with **Esc → Multiplayer status**.
- Includes all Preview 15 localization, Steam networking and recovery changes.
- Release Steam build passed; 18/18 status checks passed. Panel appearance confirmed working in-game by the host.




Based on upstream BeaverBuddies `v1.1` commit

`a13b1f20dacb6e30efa967cc8ac83e73779c0755`. All previews are cumulative.

Preview 4 was built against Timberborn 1.1.2.4.



## Preview 15 - 1.1.0-stability.15

- Fix the startup localization exception present in Preview 13 and inherited by
  Preview 14: remove four blank CSV records and restore the required Comment
  column on both reconnect-grace entries. Preserve all existing localization
  keys and text.
- Include all Preview 14 Steam relay, invite, recovery and direct-IP changes.
- Add checks for every shipped locale and all settings localization keys,
  including regression fixtures for blank records and missing columns.
- Check the fixed files using the validator decompiled from installed
  Timberborn 1.1.2.4 with its actual LINQtoCSV library; the old Preview 13
  English asset reproduces the failure.
- Release Steam build and automated checks are required before packaging.
  Live two-account Steam/Unity playtesting remains pending.

## Preview 14 - 1.1.0-stability.14 (included in Preview 15)

- Replace the deprecated Steam P2P transport with SteamNetworkingSockets and
  Valve relay initialization. No Hamachi, shared IP or router forwarding is needed
  for Steam invites. Keep the TCP direct-IP transport and mixed sessions.
- Receive save and game data on transport workers during Unity stalls; use
  native connection handles to isolate each reload. Remove the legacy per-frame
  Steam packet router and fixed 128 KiB/s transfer limit.
- Apply bounded native send backpressure off the game thread, release received
  native message buffers, wake blocked reads/sends on close, and drain native
  reliable buffers before restarting a host.
- Retain Steam lobbies during snapshot recovery and admit original authenticated
  Steam identities plus session tickets. Guests automatically reconnect to the
  original Steam host; recovery also works when Steam lost lobby membership.
- Start the 30-second admission countdown after the host scene loads. All
  transports retain full-tick saves, snapshot verification, compatibility checks,
  initialization barriers, cancellation and repeat-recovery protection.
- Handle invite readiness, failed/expired/superseded lobby responses, mismatched
  Steam protocols, startup lobby arguments and duplicate callbacks. Show connection
  stages and save-download progress; cancel closes the attempt.
- Fragment large compatibility profiles to respect transport message limits.
- Add native-boundary Steam tests and real TCP/Steam-mixed transport checks.
  Live two-account Steam validation is pending; see STEAM-INVITES.md.

## Preview 13 - 1.1.0-stability.13

- Recover unexpected direct-IP disconnects by finishing the host tick, pausing,
  saving and reloading a shared authoritative snapshot. Retain all Preview 12 work.
- Allow 30 seconds to reconnect after the new host listener starts; admitted
  players get a separate loading allowance. Continue with connected players when
  the window expires, preserving the previous simulation speed (including pause).
- Show a reconnect countdown and host controls to continue without missing
  players or cancel recovery. Canceling leaves the world paused.
- Issue temporary session tickets outside replay so only original participants
  can claim recovery slots and obtain the new snapshot identity after a lost notice.
- Detect silent established connections after 20 seconds without incoming frames;
  suspend the liveness timeout during compatibility/loading admission. Worker
  keepalives remain active through main-thread saves and UI stalls.
- Distinguish graceful guest departure from a transport drop. Discard old commands
  and retain compatibility, snapshot digest, full-tick and initialization checks.
- Invalidate late save callbacks after cancellation; retry transport interruptions
  separately from disk or scene-loading failures. Retain the recovery loop guard.
- Direct-IP recovery only; mixed Steam invite sessions retain manual fallback.
- Release Steam build completed with zero errors; 210 automated checks passed.
  Automated validation and live-playtest limitations are documented in
  RECONNECT-GRACE.md and the packaged release notes.

## Preview 12 - 1.1.0-stability.12 (included in Preview 13)

- Add a compact native HUD for host/guest connection state, application round-trip
  time, reliable send queue memory/count, received events, buffered ticks, measured
  simulation tick rate, and approximate per-peer progress.
- Add collapse and hide controls, pause-menu restore, and persistent local display
  preferences. Integrate with the normal HUD and its scaling/visibility behavior.
- Explain compatibility waits, pause/recovery, delayed responses, simulation
  catch-up and disconnection without labeling every delay as a network fault.
- Use bounded disposable telemetry, one outstanding probe per peer, main-thread
  measurements, four UI refreshes per second, and priority for gameplay traffic.
  Telemetry does not alter replay hashes, RNG, simulation speed or recovery rules.
- Retain all Preview 11 compatibility, diagnostic and activity behavior.
- 188 automated checks passed; Release Steam build completed with zero errors
  against Timberborn 1.1.2.4.
- Native Unity rendering and a live two-player session remain to be checked.

## Preview 11 - 1.1.0-stability.11

- Compare enabled mod IDs, versions, load order, code/data fingerprints and
  loaded assembly module IDs, plus hashed registered Mod Settings values.
- Add a second admission check after map load for game-only settings. Hold
  simulation and patched actions until it passes; show useful mismatch keys and
  a waiting dialog. Repeat admission after host snapshot recovery.
- Exclude known local BeaverBuddies preferences; retain the detailed tracing check.
  Bound compatibility payloads and compressed expansion.
- Keep rolling diagnostics without detailed tracing: 120 checkpoints at 20-tick
  intervals, 64 entity and 256 water-column samples per checkpoint, and 256 recent
  useful command summaries. Include RNG, jobs, inventory reservations, water
  contamination, command arguments/targets and network context.
- Coordinate local exports before recovery or replay failure, write reports in
  the background, and retain ten rolling archives. Recorder failures do not stop
  multiplayer. Add a comparison utility and COMPATIBILITY-DIAGNOSTICS.md.
- Include Preview 10 player activity, previously delivered as a local ZIP.
- 164 automated checks passed; Release Steam build completed with zero errors.
- Built against Timberborn 1.1.2.4. Live two-player Unity playtesting of the new
  features remains necessary; automated tests use stubs at native game boundaries.

## Preview 10 - 1.1.0-stability.10 (included in Preview 11)

- Show other players' world cursors at 50% opacity, interpolated between updates,
  with player names and connection IDs. Reproject onto each player's own camera.
- Show native outlines for remote selected entities using independent secondary
  highlighters, preserving local primary selection colors and input behavior.
- Label selected buildings with **Viewing** and locally initiated building changes
  with **Editing** for three seconds. These indicators do not lock buildings.
- Reuse the existing player name/color preferences; add a default-on activity toggle.
  Hide world cursors over UI/off-map and clear shared activity when unfocused.
- Add a presentation-only channel for TCP and Steam, capped at ten sends per second.
  Coalesce old cursor updates, bound incoming/outgoing activity buffers, prioritize
  gameplay messages, and assign guest identities on the host. Activity never enters
  replay, simulation RNG, or desync hashes.
- Clear displays on disconnect, expiry, settings changes, recovery and scene reload.
- Release Steam build succeeded; 133 automated checks passed (48 transport/replay,
  12 activity service, 14 snapshot coordinator, 57 compiled mod/game, 2 Python water).
- Includes all Preview 9 fixes and recovery behavior. Native rendering and a full
  two-player playtest remain to be verified in Timberborn.

## Preview 9 - 1.1.0-stability.9

- Queue outgoing events per connection; compress and write on workers. Bound
  queues and preserve immutable messages, ordering, framing, and final notices.
- Run connection attempts asynchronously and initialize joins on Update.
- Add automatic host-snapshot recovery for direct-IP sessions, enabled by default
  and controlled by the host. Finish the host tick and parallel work; save once;
  reconnect everyone; verify snapshot SHA-256; reload host and guests from the same
  bytes/seed; wait for all loaded guests before restoring the previous speed.
- Add a **Resync from host** options-menu button and visible recovery progress.
  Discard old-session commands. Refuse recovery after a failed replay; stop on
  save/connection/timeout failures or rapidly recurring desyncs.
- Steam invite sessions explicitly retain manual rehosting. Queued sending applies
  to both TCP and Steam. Stop combined TCP/Steam accept workers when closing a
  session, and avoid spinning on a failed listener.
- Release Steam build succeeded; 114 automated checks passed.
- See [SNAPSHOT-RECOVERY.md](SNAPSHOT-RECOVERY.md) for behavior and validation.
  Native scene loading still requires a two-player playtest; Preview 9 has not
  been validated in live multiplayer.

## Preview 8 — 1.1.0-stability.8

- Recover input on desync notification and multiplayer scene load, including
  direct-IP Save and rehost, using the game's built-in device reset.
- Defer recovery to Update and consume cached held/down/release binding state.
  Coalesce repeated requests; do not reset devices every frame or change saved
  keybindings. Service is registered only in multiplayer scenes.
- 83 automated checks passed; Release Steam build succeeded. Native device reset
  is mocked in regression tests. The fork owner reports that the current build works well in multiplayer. This recovery measure does not establish the original input fault root cause.

## Preview 7 — 1.1.0-stability.7

- Choose exactly equal-distance demolition jobs by persistent entity ID in
  multiplayer. Preserve nearer-job preference, eligibility, priority and
  reservation rules; retain vanilla single-player behavior.
- Record selected target IDs and distances in detailed traces. Include up to
  eight eligible candidates in verbose local logs without treating harmless
  candidate-order differences as synchronized trace mismatches.
- 77 checks passed, including six new demolition selection checks. Release Steam
  build succeeded. Multiplayer playtesting is still required; the original
  Preview 6 logs did not prove an equal-distance tie caused that incident.

## Preview 6 — 1.1.0-stability.6



- Recycle expired water diagnostic arrays, preserving snapshot retention and

  captured values while removing steady-state map-sized allocations.

- Optimize ordered event insertion and bulk removal of consumed backlog.

- Parse each incoming transport frame once instead of twice.

- Gate routine event/packet/tick logging before formatting and serialization.

- 71 automated checks passed; Release Steam build succeeded. Synthetic tests

  measured about 85 MB versus zero bytes allocated for 16 warmed-up captures,

  and 438.92 ms versus 0.28 ms for 4,000 ordered inserts. These are not FPS tests.

- The fork owner confirmed Preview 6 works well in a two-player playtest. Detailed logging, synchronous sends

  and throttling remain performance costs. The optional Harmony installation

  fixture retains its previously documented test-runtime limitation.



## Preview 5 â€” 1.1.0-stability.5



- Negotiate compatibility before requesting or loading the shared map. Compare

  the running game version, full mod version, and loaded BeaverBuddies/TimberNet

  module IDs. Replacing files without restarting cannot disguise an old process.

- Reject older previews and mismatched binaries. Bound the compatibility wait

  to 15 seconds, close failed connections, and report an update/restart message.

- Stop replay after a failed action, discard pending actions, pause the session,

  notify connected peers, and restore the replay flag even if error handling

  throws. Block further simulation and rehosting until the scene is reloaded;

  the affected player is told to reload a known-good save.

- Make the remaining ten RNG classification patches exception-safe. Count

  nested calls instead of using simple set membership. Restore the ticker's

  prior RNG classification in a finalizer, including nested updates.

- Keep the ordinary RNG check enabled regardless of detailed logging settings.

- Synchronize client-list access during joins, broadcasts and shutdown.



Both players must install the same compiled archive and fully restart the game.

Independently compiled binaries may have different module IDs and be rejected.

This checks the game and BeaverBuddies binaries, not every third-party mod or its

settings. No changes to Housing Optimize, evaporation, district fallbacks, or

animation timing are included in this preview.



Validation: 64 passing checks (23 transport/replay/animation, 39 compiled-mod/game,

two Python snapshot comparisons), plus a successful Release Steam build against

Timberborn 1.1.2.4. The compiled scope checks exercise the production prefix and

finalizer methods. An additional attempt to install Harmony on a managed test

fixture failed because the installed MonoMod dependency could not access

SignatureHelper.GetMethodSigHelper under .NET 8; that integration check remains

opt-in and is not counted as passing. The fork owner subsequently confirmed Preview 5 works well in their two-player

playtest. A failure stop contains partial state; it does not roll back the action

or recover unsaved progress. Peer notification is best-effort if the connection

has already failed.



## Preview 4 â€” 1.1.0-stability.4



- Replace the render-frame clock in `WaterDepthStrengthModifier.GetStrengthModifier`

  with Timberborn's configured simulation tick interval during multiplayer.

  This prevents different frame rates from producing different water-seep output.

- Inject `ITickService` into the existing water-source buffer. Preserve the

  game's depth thresholds, hysteresis, fade speed, disabled-state reset and

  maximum-strength clamp. Single-player retains its original frame clock.

- Validate that the targeted method contains exactly one clock call to replace.

- Include individual source coordinates and exact strength/contamination bits

  in detailed traces.

- Add five runtime checks covering the original frame-rate divergence, the

  configured clock, matching patched results, reset/clamp behavior and rejection

  of an incompatible method body.



The fork owner confirmed Preview 4 resolved their reported badtide desync.

The regression experiment also reproduced different output at 30 and 144 FPS

using the installed game's ramp instructions, then verified identical output

after the production transpiler. Its depth query and frame clock are test doubles;

the test does not start Unity or install Harmony into a live game.



Evaporation settings are unchanged. The source ramp now advances by simulation

seconds rather than local frame duration, so its timing can differ from upstream.



## Preview 3 â€” 1.1.0-stability.3



- Apply water-source simulation snapshots in a consistent order by coordinates,

  strength and contamination. Preserve the live source registry and values.

- Correct a demonstrated order-dependent case when multiple sources affect

  the same water column. The installed game's source-update task produced three

  results across six registration orders; canonical ordering produced one.

  This case was not established as the cause of the reported badtide desync.

- Add separate hashes for active depth, contamination, overflow, geometry,

  inactive storage and source inputs without disabling existing checks.

- With detailed logging enabled, retain up to four water-map snapshots within

  a 64 MiB budget and write a local ZIP on desync. Reset capture between sessions

  and catch diagnostic failures. No automatic diagnostic upload was added.

- Add a Python tool to compare retained snapshots by tick, cell, field and

  exact floating-point bits.



## Preview 2 â€” 1.1.0-stability.2



- Restore the previous saving flag after exit saves, including exceptions,

  using a Harmony finalizer.

- Clear stale saving state when resetting between scenes.

- Restore the previous flag after deferred normal saves using `try/finally`.

- Address a defect consistent with immediate join-time desync reports in which

  one peer omitted a moisture trace because its saving flag remained set.



## Preview 1 â€” 1.1.0-stability.1



- Reset the animation path cursor before interpolation that can move backward

  between ticks. Fall back to the simulation position for non-finite visual

  coordinates before water/swimming listeners consume them. The fork owner

  reported that this fixed their crashing issue.

- Preserve unread Steam packet tails across partial reads, validate read ranges,

  handle zero-length reads, and respect the actual received byte count.

- Wake blocked readers on close and tolerate packets arriving during shutdown.

- Reject failed Steam sends and writes to closed sockets.

- Lock complete network frames so concurrent header/payload writes cannot mix.

- Close corrupted/failed connections, stop consuming events after failure,

  and deliver client error callbacks through the update thread.

- Close discarded client connections and prevent an old socket's cleanup from

  unregistering its replacement. Synchronize socket registry access.

- Preserve nested gameplay/non-gameplay RNG classification and restore it after

  exceptions in random-selection wrappers.

- Apply regenerated entity IDs to the entity builder and fail explicitly if a

  unique ID cannot be found after the retry limit.

- Add transport/animation regression and compiled-mod runtime test executables.



## Installation



1. Fully close Timberborn on both computers.

2. Extract the preview build into the actual `Documents/Timberborn/Mods` directory,

   replacing the prior `BeaverBuddies-StabilityPreview` files.

3. Enable **BeaverBuddies - Stability Preview**, version **1.1.0-stability.8**, on

   both computers. Disable Workshop BeaverBuddies and duplicate local previews.

4. Test a copied save. Both players must use the same preview.



For a diagnostic session, enable **Always Use Detailed Logging** on both peers.

It adds overhead. If a desync occurs, retain both Player.log files and the newest

water ZIP from each peer under:



`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics`



Large maps may retain fewer snapshots; peers running far apart may have no shared

retained ticks. Old ZIPs remain until removed. See

`RuntimeChecks/compare_water_snapshots.py` for comparison commands.



## Validation and limits



Preview 4: **41 passing checks** (13 transport/animation, 26 compiled mod/game,

two Python archive comparison). The mod builds successfully against the stated

game version. Tests require .NET 8; game-dependent checks additionally require

the user's installed game assemblies and Harmony directory. No proprietary game

assemblies, decompiled game code, player logs or saves are included in this fork.



The owner's playtest confirms the reported issue, not universal determinism.

Other game versions and combinations of mods may still have unrelated problems.

No Housing Optimize changes are included. Upstream authorship and GPL licensing

are preserved in `License.txt` and the repository history.
