# Reconnect grace period (Preview 14)

Preview 14 extends the Preview 13 recovery flow to Steam invites and mixed sessions.
Install the same compiled ZIP on every computer and restart Timberborn.

## What players see

Reconnect grace is enabled by default in BeaverBuddies' mod settings. The host
also needs automatic snapshot recovery enabled. It supports Steam invites, direct IP, and sessions containing both kinds of guest.

After an unexpected established connection closes, the host completes the current
simulation tick, pauses, and immediately prepares a shared save. All participants
reload that snapshot; the mod never resumes the disconnected guest's old world.
The new host listener accepts returning players while the host loads. A
**30-second reconnect window** starts once the host scene is loaded, so loading
does not consume the window. Guests retry their original Steam identity or IP
address and port automatically. Steam lobby membership is retained across reloads. If everyone is ready sooner, play resumes
without waiting for the countdown to finish.

The host's recovery dialog shows connected players, the countdown, and two choices:

- **Continue without player** closes admission and waits for the currently
  connected players to finish loading. Anyone still missing must join a later rehost.
- **Cancel recovery** stops automatic recovery and keeps the world paused. Use the
  resulting manual-rehost option or return to the menu. Escape uses this cancel
  action; it does not silently continue without a guest.

At the end of the window the host automatically continues with connected players,
including alone if nobody rejoined. Players who established their ticketed
connection in time can finish downloading/loading after the countdown. The existing
10-minute recovery/load timeout still applies. Everyone must finish initialization
and compatibility checks before simulation resumes. A previously paused host
stays paused; otherwise its prior speed is restored through ordered gameplay events.

The save and full reload still take time depending on settlement size and computer
speed. A brief transport drop can therefore cause a noticeable loading screen.
Commands not applied before the host snapshot are discarded, so a recent click may
need repeating. This feature reduces recovery steps; it does not fix desync causes.

## Connection and failure handling

Established connections are checked for silence using bounded transport keepalives
sent by a worker every three seconds, including while paused or with the HUD
hidden. They keep running during main-thread saves or UI stalls, so those pauses
alone do not look like a failed network. Keepalives yield to gameplay traffic. Twenty seconds without incoming
frames closes a silent connection, then starts recovery. During loaded-mod admission,
that liveness timeout is suspended so slow scene loads are not treated as drops.
The 30-second grace window is additional to detection, saving, and loading time.

Guests who lost the host before its reload notice retry for up to two minutes to
allow snapshot preparation. If the host is gone, recovery is disabled, the grace
window closed, or the host canceled, they eventually get the existing manual
reconnect dialog. Guest retries do not force a host to reopen a closed window.

Returning to the main menu sends a best-effort graceful-leave notice, preventing a
routine departure from triggering recovery. If that notice cannot be delivered,
the host treats the closure as an interruption. A game crash is also an interruption.

A dropped connection while downloading can retry within the open window. Once the
host closes admission, another admitted guest disconnecting stops recovery paused.
Disk/save/scene errors, replay failures, incompatible mods and snapshot digest
mismatches are not treated as harmless network hiccups. The existing two-minute
repeat-recovery guard prevents a persistently bad connection from causing a reload
loop; repeated failures require manual intervention.

## Protocol and implementation

After build/mod compatibility succeeds, a small bounded admission exchange issues
a random 256-bit session ticket. Tickets remain in memory; they are not written to
saves, diagnostics, replay events, or logs. Recovery accepts only the previous
session's tickets, prevents concurrent claims of one slot, and revokes missing
slots when admission closes. This is session continuity, not user-account login
or encryption of the existing transport.

The admission response carries the recovery ID and snapshot digest for a guest
that missed the reload notice. Snapshot bytes must match before replacing the
scene. Gameplay hashes, RNG and event ordering do not include admission traffic.
A new network session replaces all old streams and queued commands. Callbacks
from a canceled save cannot restart a failed recovery.

## Validation

Automated checks exercise actual TCP admission and snapshot transfer, invalid and
duplicate tickets, bounded rosters, graceful leave, silent connections, reconnect
countdowns, readiness barriers, paused hosts, repeated transport loss, disk errors,
and cancellation during saving. Production code is compiled against Timberborn
1.1.2.4; coordinator tests mock Unity scene/save boundaries.

Live multiplayer and native dialog appearance still need a game playtest. On a
spare save: interrupt a guest's network briefly, restore it, check automatic reload
and prior speed; repeat with a missing guest and test timeout/continue/cancel; verify
slow loading after admission and the normal main-menu departure. Allow more than
two minutes between successful recovery experiments because of the loop guard.
