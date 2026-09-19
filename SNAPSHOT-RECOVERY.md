# Sending and snapshot recovery

Preview 14 supports Steam invites, direct IP, and mixed sessions. Preview 13
extended this flow to transport disconnects with a reconnect grace
period. See [RECONNECT-GRACE.md](RECONNECT-GRACE.md) for its countdown, controls,
and timeout behavior. The original desync recovery behavior is described below.

Preview 9 includes all fixes from Preview 8. Its compiled ZIP must be installed
on every computer, followed by restarting Timberborn. Keep Harmony and Mod
Settings enabled; disable duplicate BeaverBuddies installations.

## Player behavior

**Automatic host snapshot recovery** is enabled by default in BeaverBuddies' mod
settings. The host's setting controls it. It supports direct-IP and Steam relay connections, including mixed sessions.
Steam recovery retains the lobby and reconnects authenticated Steam identities
using the same session-ticket and snapshot checks as direct IP.

On a detected desync, guests pause and request a host snapshot. The host finishes
its current simulation tick, waits for parallel work, saves, then reloads along
with every guest. Guests reconnect to the original Steam host or IP address and port automatically.
The host's saved state is authoritative. Commands pending in the discarded session
are not replayed; a click made just before recovery may need repeating.

The game stays paused until the host is loaded and every expected guest has both
loaded and replayed its initialization event. Then the host restores the previous
simulation speed through the ordinary ordered event stream. A previously paused
game stays paused. Progress appears in a dismissible recovery dialog.

Recovery uses a normal full save and full scene reload. Its duration depends on
save size, network throughput and both computers' loading times; it is not promised
to finish in a fixed number of seconds. The recovery save remains available in
the save list with the existing `Rehost` naming convention.

**Resync from host** in the in-game options menu deliberately exercises the same
recovery path, without needing to wait for a naturally occurring desync.

## Failure handling

- A failed replay may have partially changed the host world. It still requires
  loading a known-good save; automatic recovery never snapshots that failure.
- Snapshot SHA-256 must match before a guest replaces its scene.
- Repeated requests and ready acknowledgements are idempotent. Old-session
  messages and mismatched recovery IDs cannot complete a new recovery.
- Save failures, canceled/failed sends, missing players and timeouts leave the
  game paused with a manual reconnect/rehost option. Main-menu navigation remains
  available. Canceling recovery notifies connected peers where possible.
- Another desync within two minutes of a completed recovery stops automatic
  reloads, rather than repeatedly reloading a persistently broken simulation.
- Connection retries are bounded by a ten-minute recovery deadline. The host's
  tick-completion and final-notice stages have shorter deadlines.

## Network implementation

Each connection has one ordered send pump. Callers capture immutable JSON, then
return without waiting for compression, socket writes or transport throttling.
The queue permits at most 2,048 active/pending messages and 8,388,608 UTF-16
characters (about 16 MiB of text). Overflow disconnects the slow peer; it never
silently skips a gameplay command. Broadcast JSON is shared between peer queues.

Complete frame writes retain the stream lock. Join backlogs are bounded, final
fault notices drain before shutdown, and connection attempts run asynchronously.
Join initialization runs on the update thread so workers do not read game state.
Closing combined listeners wakes their accept workers and closes queued sockets.

Recovery uses a separate control channel over the existing framed connection,
outside simulation replay/hash/tick scheduling. It creates fresh transport sessions
for the snapshot, so old commands, transport hashes and tick queues cannot leak
into the new world. Host and guests use the same map bytes to initialize RNG state.

## Automated validation

The Release Steam build succeeds. **114 checks passed:** 41 transport/replay/
performance/animation checks, 14 recovery coordinator checks, 57 compiled mod/game
checks, and two Python water-diagnostic checks.

Run from the repository root with the .NET SDK:

```powershell
dotnet run --project StabilityTests
dotnet run --project SnapshotChecks
```

`StabilityTests` includes blocked writers, independent peer queues, immutable
messages, FIFO/framing, bounded backlogs, graceful close and actual loopback TCP
sessions that transfer commands and reload a new snapshot on the same port.
`SnapshotChecks` compiles the production recovery coordinator unchanged against
controlled scene/save/UI boundaries. It exercises readiness, save/flush failures,
timeouts, stale IDs, duplicate messages, canceled sessions, SHA-256 rejection,
repeat-desync protection. Steam recovery coverage is described in STEAM-INVITES.md.

The existing `RuntimeChecks` suite loads the compiled mod and installed game
assemblies. New checks exercise actual compiled action/tick guards and prevention
of saving before simulation buckets finish. See `StabilityTests/README.md` for
that suite's invocation and the Python water-diagnostic tests.

These tests do **not** launch Unity, perform native scene reloads or prove that
every gameplay mod remains deterministic. Live multiplayer validation is pending.

## Two-player acceptance test

1. Install the identical Preview 9 ZIP and restart both games. Load a disposable
   copy of your settlement and connect by IP and port.
2. Select **Resync from host** in the options menu. Confirm that both computers
   reload without entering an address, and that the host waits for the guest.
3. Resume play, then verify construction commands, pause/speed controls, zoom and
   layer scrolling from both computers. Compare visible settlement/water state.
4. After at least two minutes, repeat from the other player. Test once while a
   badtide is active. The manual menu action may leave the session paused because
   opening the options menu paused it first.
5. In a separate test, stop recovery or disconnect one guest during loading. The
   host must remain paused and provide recovery options, not resume alone.

Capture both `Player.log` files if any step fails. Preview 8 remains the previously
playtested release. Download the compiled archive from the GitHub release; every player must use the same ZIP.
