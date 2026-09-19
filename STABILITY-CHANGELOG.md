# Changelog

Every change this fork makes relative to the original BeaverBuddies `v1.1` branch at commit
`a13b1f20dacb6e30efa967cc8ac83e73779c0755` (24 August 2026), built against Timberborn
1.1.2.4. For a plain-language summary, see the [README](README.md). Future releases add a new
entry above the current one.

## 1.1.3

The current release. See `STEAM-INVITES.md`, `CONNECTION-PANEL.md` and `PLAYER-ACTIVITY.md`.

### Steam friend invites

- Replace the legacy `ISteamNetworking` P2P transport (deprecated by Valve) with
  `ISteamNetworkingSockets`, so Steam friends can be invited from Steam's overlay without
  Hamachi or port forwarding. It is offered alongside direct IP, not instead of it.
- All Steam calls run on the game thread; TimberNet talks to Steam through queues. Writes
  never block, and the connection completes in the background instead of inside the
  3-second wait in `TimberClient.Start()`.
- The host accepts only players who joined its friends-only Steam lobby. The lobby records
  whether the host is still accepting players, so an old invite explains itself. A friend
  whose game was closed joins through Steam's launch invite (`+connect_lobby`).
- Raise Steam's send rate and buffer limits so the save transfer is not throttled (the
  original capped it at 128 KB/s). Every connection failure, stall or timeout ends with an
  explanation that includes Steam's own end reason, in the error dialog and in `Player.log`.
- The transport keeps unread data between reads, validates read ranges and wakes blocked
  readers when a connection closes. A comment in the original's Steam read routine says it
  "will fail" if Steam merges several messages into one packet.
- A Steam failure can no longer prevent hosting over direct IP.
- TimberNet: transports can report why they failed and complete connecting in the
  background (`IFailureDescriber`, `IConnectionAwaitable`).
- Remove an unused upstream handler that still called the legacy Steam P2P API.

### Connection panel

- A small HUD panel during multiplayer showing connected players, each player's ping
  (green, yellow or red), whether you are in sync, the tick rate, game speed, how far a
  guest is behind the host, and whether players are connected directly or through Steam.
- It collapses to one line by clicking its title (remembered), and can be hidden from Mod
  Settings or with an optional key. Its corner is a setting.
- Ping is measured by the network layer (a probe once a second, answered on the guest's
  network thread) so it works the same over Hamachi, direct IP and Steam. Probes and the
  player roster use the separate presentation lane: never replayed, never hashed, never
  sent to a guest that is still joining, and validated on arrival.
- The panel docks into the game's own HUD layout, and only reads: it sends no gameplay
  event, and if it fails it disables itself.

### Player activity

- Show other players' translucent, colored cursors with names, remote selection
  outlines in each player's color, and **Viewing / Editing** labels on buildings.
- An in-game **Player cursors** dialog (Options menu) sets, per connected player, the
  cursor's color (their color, presets or exact RGB), size (50%-300%) and transparency
  (0%-90%). Choices are local, applied live, and remembered by player name in
  `BeaverBuddiesCursorStyles.json`.
- The **Player activity indicators** setting (on by default) turns sharing on and off.
- Activity uses its own lane on the existing connection, separate from the replay
  script and desync hash: host-assigned identities, latest-wins coalescing, no
  game-thread blocking, and nothing sent to a guest until its join has finished.

### Compatibility and connection safety

- Negotiate compatibility before requesting or loading the shared map. Compare the running
  game version, full mod version, and loaded BeaverBuddies/TimberNet module IDs. Replacing
  files without restarting cannot disguise an old process. Reject builds that lack the
  check and mismatched binaries. Bound the compatibility wait to 15 seconds, close failed
  connections, and report an update/restart message. The original only warned about a
  version mismatch after the save had loaded.
- Stop replay after a failed action, discard pending actions, pause the session, notify
  connected peers, and restore the replay flag even if error handling throws. Block
  further simulation and rehosting until the scene is reloaded; the affected player is told
  to reload a known-good save. A failure stop contains partial state; it does not roll back
  the action or recover unsaved progress, and peer notification is best-effort if the
  connection has already failed.
- Lock complete network frames so concurrent header and payload writes cannot mix.
- Close corrupted or failed connections, stop consuming events after failure, and deliver
  client error callbacks through the update thread.
- Close discarded client connections and prevent an old socket's cleanup from
  unregistering its replacement. Synchronize socket registry access.
- Synchronize client-list access during joins, broadcasts and shutdown.

### Desync fixes

- **Water and frame rate.** Replace the render-frame clock in
  `WaterDepthStrengthModifier.GetStrengthModifier` with Timberborn's configured simulation
  tick interval during multiplayer. This prevents different frame rates from producing
  different water-seep output. Inject `ITickService` into the existing water-source
  buffer. Preserve the game's depth thresholds, hysteresis, fade speed, disabled-state
  reset and maximum-strength clamp. Single-player keeps its original frame clock. Validate
  that the targeted method contains exactly one clock call to replace. The fork owner
  confirmed this resolved their reported badtide desync. A regression experiment
  reproduced different output at 30 and 144 FPS using the installed game's ramp
  instructions, then verified identical output after the production transpiler; its depth
  query and frame clock are test doubles, and it does not start Unity or install Harmony
  into a live game. Evaporation settings are unchanged. The source ramp now advances by
  simulation seconds rather than local frame duration, so its timing can differ from
  upstream.
- **Water-source ordering.** Apply water-source simulation snapshots in a consistent order
  by coordinates, strength and contamination. Preserve the live source registry and
  values. This corrects a demonstrated order-dependent case when multiple sources affect
  the same water column: the installed game's source-update task produced three results
  across six registration orders; canonical ordering produced one. This case was not
  established as the cause of the reported badtide desync.
- **Saving flag.** Restore the previous saving flag after exit saves, including
  exceptions, using a Harmony finalizer. Clear stale saving state when resetting between
  scenes. Restore the previous flag after deferred normal saves using `try/finally`. This
  addresses a defect consistent with immediate join-time desync reports, in which one
  peer omitted a moisture trace because its saving flag remained set.
- **Random numbers.** Preserve nested gameplay and non-gameplay RNG classification and
  restore it after exceptions in random-selection wrappers. Make all ten RNG
  classification patches exception-safe, counting nested calls instead of using simple set
  membership, and restore the ticker's prior RNG classification in a finalizer, including
  nested updates. The ordinary RNG check stays enabled regardless of detailed logging
  settings.
- **Equal-distance demolition jobs.** Choose exactly equal-distance jobs by persistent
  entity ID in multiplayer. Preserve nearer-job preference, eligibility, priority and
  reservation rules; single-player behavior is unchanged. Not confirmed in a live session,
  and the logs of the incident that prompted it did not prove an equal-distance tie
  caused it.
- **Stuck controls.** Recover input on desync notification and multiplayer scene load,
  including direct-IP Save and Rehost, using the game's built-in device reset. Recovery is
  deferred to Update and consumes cached held/down/release binding state. Repeated requests
  are coalesced; devices are not reset every frame and saved keybindings are not
  changed. The service is registered only in multiplayer scenes. The native device reset
  is mocked in regression tests. This is a targeted recovery measure, not a proven root
  cause, and the original report has not been confirmed fixed.
- **Entity IDs.** Apply regenerated entity IDs to the entity builder and fail explicitly if
  a unique ID cannot be found after the retry limit.

### Crash fixes

- **Animation.** Reset the animation path cursor before interpolation that can move
  backward between ticks. Fall back to the simulation position for non-finite visual
  coordinates before water and swimming listeners consume them. The fork owner reported
  that this fixed their crashing issue.
- **Demolition-selection replay.** `ClearResourcesMarkedEvent` looked up each entity in a
  replayed demolition selection with no null check, so if builders had already demolished
  some of the selected entities before the event arrived (it was stamped for tick 1771 and
  replayed at 1773), a `NullReferenceException` aborted the whole session. Missing
  entities are now skipped with a warning, the same way `BuildingsDeconstructedEvent` does,
  and an event with nothing left is skipped. The host re-stamps and forwards events at its
  own tick, so both sides skip the same entities and stay in sync. The regression check
  replays a stale selection against a real empty entity registry and fails on the old
  code. A selection where only some entities are missing needs live Unity objects, so it
  is untested, and the fix has not been confirmed in a live session. `DuplicationEvent` has
  the same kind of weak spot and is left unchanged.

### Performance

- Recycle expired water diagnostic arrays, preserving snapshot retention and captured
  values while removing steady-state map-sized allocations.
- Optimize ordered event insertion and bulk removal of consumed backlog.
- Parse each incoming transport frame once instead of twice.
- Gate routine event, packet and tick logging before formatting and serialization.
- Synthetic tests measured about 85 MB versus zero bytes allocated for 16 warmed-up
  captures, and 438.92 ms versus 0.28 ms for 4,000 ordered inserts. These are not FPS
  tests. Detailed logging, synchronous sends and throttling remain performance costs.

### Diagnostics

- Add separate hashes for active depth, contamination, overflow, geometry, inactive
  storage and source inputs without disabling existing checks.
- With detailed logging enabled, retain up to four water-map snapshots within a 64 MiB
  budget and write a local ZIP on desync. Reset capture between sessions and catch
  diagnostic failures. No automatic diagnostic upload exists.
- Add a Python tool to compare retained snapshots by tick, cell, field and exact
  floating-point bits.
- Include individual water-source coordinates and exact strength and contamination bits in
  detailed traces. Record selected demolition target IDs and distances there too, with up
  to eight eligible candidates in verbose local logs, without treating harmless
  candidate-order differences as synchronized trace mismatches.
- Add transport, animation and player-activity regression tests and a compiled-mod
  runtime test executable.

## Installation

1. Fully close Timberborn on every computer.
2. Download `BeaverBuddies-stability-1.1.3.zip` from the
   [latest release](https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork/releases/latest),
   extract it, and copy the `BeaverBuddies-StabilityPreview` folder into
   `Documents/Timberborn/Mods`, replacing any earlier copy.
3. Make sure **Harmony** and **Mod Settings** are enabled, then enable **BeaverBuddies -
   Stability Preview**, version **1.1.3**, on every computer. Disable the Workshop
   BeaverBuddies and any duplicate local copies: they share one mod ID.
4. Every player must use the same build. Test on a copied save first.

For a diagnostic session, enable **Always Use Detailed Logging** on both peers. It adds
overhead. If a desync occurs, keep both Player.log files and the newest water ZIP from each
peer under:

`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics`

Large maps may retain fewer snapshots; peers running far apart may have no shared retained
ticks. Old ZIPs remain until removed. See `RuntimeChecks/compare_water_snapshots.py` for
comparison commands.

## Validation and limits

The 1.1.3 validation run passed **144 checks**: 87 in `StabilityTests` (network transport,
the Steam transport against a simulated Steam network, direct-versus-Steam protocol parity,
animation, player activity, ping measurement and the connection panel), 55 in
`RuntimeChecks` (the compiled mod running against the game's own assemblies) and two Python
archive-comparison checks. The mod builds against Timberborn 1.1.2.4 with no warnings.
Tests require .NET 8; game-dependent checks additionally require the user's installed game
assemblies and Harmony directory. No proprietary game assemblies, decompiled game code,
player logs or saves are included in this fork.

The Steam transport is tested against a fake Steam network that fails any Steam call made
off the game thread. Two protocol-parity checks run one scripted session (about 60 events
in each direction, one of 220 KB, plus cursor traffic) over a direct connection and over
Steam under stress, and require both peers to end with identical events and state hashes;
corrupting one byte in the Steam path makes them fail. The layer that calls the real Steam
client is not covered by the automated checks; it has been confirmed in real playtests.

The fork owner's two-player playtests confirmed: the animation crash fix, the badtide
desync fix, the player activity indicators, Steam invites and the connection panel, and
that the compatibility check, failed-action stop and performance changes play well. They
confirm the reported issues and that this build plays well, not universal determinism.

Not confirmed in a live session: the demolition-selection crash fix, equal-distance
demolition tie-breaking and the stuck-controls recovery. Only two players have been tested.
Everyone in a session must run the identical build; other mods and settings are not
compared. Text added by this fork is English only. Other game versions and combinations of
mods may still have unrelated problems. No Housing Optimize changes are included. Upstream
authorship and GPL licensing are preserved in `License.txt` and the repository history.
