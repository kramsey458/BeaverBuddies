# Stability previews: cumulative changelog



Based on upstream BeaverBuddies `v1.1` commit

`a13b1f20dacb6e30efa967cc8ac83e73779c0755`. All previews are cumulative.

Preview 4 was built against Timberborn 1.1.2.4.



## 1.1.2-steam.1 (pre-release) - Steam relay invites

Built on 1.1.1.

- Replace the legacy `ISteamNetworking` P2P transport (deprecated by Valve) with
  `ISteamNetworkingSockets`, so Steam friends can be invited from Steam's overlay without
  Hamachi or port forwarding. It is offered alongside direct IP, not instead of it. See
  `STEAM-INVITES.md`.
- All Steam calls run on the game thread; TimberNet talks to Steam through queues. Writes
  never block, and the connection completes in the background instead of inside the
  3-second wait in `TimberClient.Start()`.
- The host accepts only players who joined its Steam lobby. The lobby records whether the
  host is still accepting players, so an old invite explains itself. A friend whose game was
  closed joins through Steam's launch invite (`+connect_lobby`).
- Raise Steam's send rate and buffer limits so the save transfer is not throttled. Every
  connection failure, stall or timeout ends with an explanation that includes Steam's
  own end reason, in the error dialog and in `Player.log`.
- A Steam failure can no longer prevent hosting over direct IP.
- TimberNet: transports can report why they failed and complete connecting in the
  background (`IFailureDescriber`, `IConnectionAwaitable`).
- 63 StabilityTests pass against a fake Steam network that fails any Steam call made off
  the game thread; 55 RuntimeChecks pass; Release Steam and non-Steam builds succeed. Two
  protocol-parity checks run one scripted session (about 60 events in each direction, one
  of 220 KB, plus cursor traffic) over a direct connection and over Steam under stress, and
  require both peers to end with identical events and state hashes; corrupting one byte in
  the Steam path makes them fail. The layer that calls the real Steam client has **not** been
  run against Steam and needs a two-account playtest.

## 1.1.1 — Preview 17

Built on Preview 8. Previews 9-16 are deprecated and are not included. This is the
same code as the `1.1.0-stability.17` pre-release; only the version number changed.

- Show other players' translucent, colored cursors with names, remote selection
  outlines in each player's color, and **Viewing / Editing** labels on buildings.
  See `PLAYER-ACTIVITY.md`.
- Add an in-game **Player cursors** dialog (Options menu) to set, per connected
  player, the cursor's color (their color, presets or exact RGB), size (50%-300%)
  and transparency (0%-90%). Choices are local, applied live, and remembered by
  player name in `BeaverBuddiesCursorStyles.json`.
- Add the **Player activity indicators** setting (on by default).
- Activity uses its own lane on the existing connection, separate from the replay
  script and desync hash: host-assigned identities, latest-wins coalescing, no
  game-thread blocking, and nothing sent to a guest until its join has finished.
- Fix a multiplayer crash in `ClearResourcesMarkedEvent`: a replayed demolition
  selection was looked up entity by entity with no null check, so if builders had
  already demolished some of the selected entities before the event arrived (it was
  stamped for tick 1771 and replayed at 1773), a `NullReferenceException` aborted the
  whole session. Missing entities are now skipped with a warning, the same way
  `BuildingsDeconstructedEvent` does, and an event with nothing left is skipped.
  The host re-stamps and forwards events at its own tick, so both sides skip the same
  entities and stay in sync. The regression check replays a stale selection against a
  real empty entity registry and fails on the old code.
- Not covered: a selection where only some entities are missing needs live Unity
  objects, so it is untested, and the fix has not been confirmed in a live session.
  `DuplicationEvent` has the same kind of weak spot and is left unchanged.
- 50 StabilityTests, 55 RuntimeChecks and 2 Python checks pass; Release Steam build
  succeeds. Cursor rendering and the dialog's layout are not covered by automated
  tests; the fork owner reported that this release works very well in a
  multiplayer playtest.

## Preview 8 — 1.1.0-stability.8

- Recover input on desync notification and multiplayer scene load, including
  direct-IP Save and rehost, using the game's built-in device reset.
- Defer recovery to Update and consume cached held/down/release binding state.
  Coalesce repeated requests; do not reset devices every frame or change saved
  keybindings. Service is registered only in multiplayer scenes.
- 83 automated checks passed; Release Steam build succeeded. Native device reset
  is mocked in regression tests. Live confirmation of the stuck-controls report
  remains pending; this is a targeted recovery measure, not a proven root cause.

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

