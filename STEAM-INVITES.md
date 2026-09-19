# Steam invites

Invite a Steam friend into your hosted game from Steam's own overlay. This is an
**alternative to direct IP**, not a replacement: both are offered at the same time when
you host, so Hamachi / port forwarding / direct IP keep working exactly as before.

## Using it

**Host**
1. Make sure **Enable Steam Networking** is on in Mod Settings (it is by default).
2. Load your save and choose **Host co-op game**.
3. Choose **Invite Friends** and pick your friend in the Steam overlay.
4. Choose **Start Game** once your friend appears in the connected-player list.

**Friend**
- Accept the invite (Steam notification or overlay). If Timberborn is not running, Steam
  launches it and joins for you. With **Allow Friends to Join Directly via Steam** on, a
  friend can also use **Join Game** from Steam's friends list.
- Both players need the same BeaverBuddies build and the game version must match.

Nobody can join after **Start Game**. An old invite then says the host already started,
instead of hanging; the host rehosts and sends a new one.

No port forwarding or Hamachi is needed. Connections go directly between players when
Steam can find a route, and are otherwise relayed through Steam's network. Valve documents
that relaying prevents players' IP addresses from being revealed to each other.

## How it works

Previously this used Valve's legacy `ISteamNetworking` P2P API, which Valve marks
deprecated ("we may remove this API from the SDK in a future release"). It now uses
`ISteamNetworkingSockets`, the API Valve recommends, which ships in the game's own
Steamworks assembly.

- **Lobby.** The host opens a friends-only Steam lobby; that is what the overlay invites
  into. Lobby data records whether the host is still accepting players.
- **Admission.** The host accepts a connection only from a player who is in its lobby, so a
  stranger who knows a Steam ID cannot connect. A guest that appears before the host's lobby
  view catches up gets a five-second grace period.
- **One thread for Steam.** Every Steam call happens on the game's main thread, where Steam
  callbacks are delivered. TimberNet's threads use plain in-memory queues. `Write` never
  blocks (a blocked game thread would stop the pump that drains the queue) and `Read`
  blocks on a queue the pump fills. Steam's documentation does not promise these calls are
  safe from other threads, so nothing depends on it.
- **Connecting in the background.** The connection completes after `ConnectAsync` returns.
  The client waits for it on a worker thread (up to 45 s) before the compatibility
  handshake's own 15 s clock starts. `TimberClient.Start()` used to wait 3 s on the game
  thread, which a transport that needs the game thread could never satisfy.
- **Speed.** Steam's defaults cap a connection at 256 KB/s with a 512 KB send buffer. The
  mod raises the send rate ceiling to 8 MB/s and the send buffer to 4 MB, so the initial
  save transfer is not throttled. If Steam's buffer fills, data stays queued and is
  retried next frame; nothing is dropped and message order is preserved.
- **Never hangs silently.** A send that makes no progress for 30 s, a connect that takes
  over 40 s, or a queue that grows past 128 MB fails the connection with an explanation.
  Steam's own end reason is translated into plain language and shown in the error and in
  the log, for example "The connection timed out. Steam could not find a working route
  between you. (Steam code 5003: ...)".
- **Steam can never break hosting.** If Steam fails to start, direct-IP hosting continues.
- **Closing.** Local close flushes queued data for up to 3 s before closing, and data the
  peer sent before closing is still delivered before end-of-stream.

## Validation

`dotnet run --project StabilityTests` runs the production transport (`SteamLinkSocket`,
`SteamLinkManager`) against a fake Steam network. The fake **fails any test that calls
Steam off the game thread**, rejects messages over Steam's 512 KB limit, and uses a small
send buffer to force backpressure. It covers: background connect; byte-exact ordered
transfer of 3 MB through a tiny buffer; write coalescing; `Write` never blocking with the
pump stopped; the queue cap; failure explanations; connect timeout; the stall detector;
draining before end-of-stream; close and idempotent close; one bad connection not affecting
another; lobby-only admission with the grace period; duplicate arrival notices; replacing
an unstopped listener; a full TimberNet session (handshake, a 2 MB save, events and
player activity in both directions) over the Steam transport; and protocol parity: one
scripted session (about 60 events each way, one of 220 KB, plus cursor traffic) is run over
a direct connection and over Steam under stress, and both peers must end with identical
events and state hashes. Corrupting a single byte in the Steam path makes these fail.

**Not verified: the real Steam client.** The thin layer that calls Steam
(`SteamLinkBackend`, `SteamNet`, `SteamListener`, `SteamOverlayConnectionService`) compiles
against the game's real Steamworks assembly but has not been run against Steam, because
that needs two Steam accounts. Treat the first two-account session as the real test.

## Two-account playtest

1. Host with Steam Networking on. In `Player.log` expect:
   `Steam networking started`, `Steam relay network: Current`, `Steam is listening for players`,
   `Steam lobby created with ID ...`.
2. Choose **Invite Friends**; the overlay should open. If not, the log says the lobby is not
   ready yet.
3. Friend accepts. Expect `Steam link to <name>: accepted` then `... Connecting -> Connected`
   on the host, and the friend appears in the connected-player list.
4. Start the game, play, then have the friend leave. Confirm the host keeps running.
5. Repeat with the friend's game **closed** when they accept (tests the launch invite).
6. Repeat with an invite sent **after** Start Game (expect the "already started" message).
7. Host with Steam Networking **off** and confirm direct IP works as before.

If something fails, send both `Player.log` files. Every state change and close is logged with
Steam's numeric end reason and debug text, which is what makes a failure diagnosable.

## Known limits

- Both players must be online in Steam, and the friend must own Timberborn.
- Joining after **Start Game** is not possible (as before). After a desync, the host uses
  **Save and rehost** and Steam guests accept a fresh invite.
- The **Invite Friends** button does nothing for the first moment after hosting starts,
  until the lobby exists; click it again.
- New strings are English only; other languages fall back to English.
