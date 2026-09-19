# Steam invites and relay networking (Preview 15)

Preview 15 includes all Preview 14 networking work and fixes the malformed
English localization CSV that prevented Preview 13/14 from loading. Both players must
install the exact same compiled ZIP and restart Timberborn.

## Play through Steam

1. Launch Timberborn through Steam on both computers, with the same mods enabled.
2. Keep **Enable Steam Networking** enabled in BeaverBuddies settings.
3. Host your saved game using the normal **Host co-op game** flow.
4. Choose **Invite Friends** and invite your friend through the Steam overlay.
5. Your friend accepts. The mod checks compatibility, downloads the host save,
   and loads their world. Connection/download progress is shown.
6. Once your friend appears in the host's connected-player list, choose
   **Start Game**. The simulation waits for everyone's loaded compatibility check.

Hamachi and manual router port forwarding are not required for the Steam path.
Steam's networking service handles connectivity and relaying. Both players still
need working Internet and Steam connections. Direct IP uses the existing TCP
transport; its network reachability requirements remain the same.

Invite before starting the hosted simulation. Joining an already-running world
still requires the host to save and rehost; this update does not add late joining
to an arbitrary live simulation. For an initial Steam test, have both players
already at Timberborn's main menu. Startup '+connect_lobby' arguments are also
handled, but cold-launch invites need live testing.

## Recovery

Desyncs and unexpected established-connection drops can now recover in Steam,
direct-IP, and mixed sessions. The host finishes its tick, saves, and everyone
reloads the same snapshot. The lobby is retained, so friends reconnect to the
same host Steam identity without exchanging another invite.

Each native connection is new. Discarded commands and unread packet tails never
carry into the replacement session. The host requires the original session
tickets; Steam recovery also checks the original authenticated Steam identities.
Those identities can reconnect even if Steam dropped their lobby membership.

The 30-second reconnect admission countdown now starts after the host scene
loads. Before then, returning guests can connect while the host loads. Guests
admitted in time have additional time to download/load. Continue/cancel controls,
previous speed restoration, snapshot digest checks, full mod checks, and the
two-minute repeated-recovery guard remain.

## Implementation and checks

- SteamNetworkingSockets P2P listener/connect, with early relay initialization.
- Reliable ordered messages adapt to the existing byte-stream framing.
- Worker reads continue during main-thread stalls. Native message buffers are
  released after copying and incoming sizes are bounded.
- Native send backpressure stays on workers and is bounded by a timeout; no
  unbounded managed packet queue or fixed 128 KiB/s Steam throttle.
- Managed queues and native reliable buffers drain before restarting transport.
- Lobby membership is required for initial Steam admission. Original identities
  and session tickets are required for recovery admission.
- Lobby creation failures remain isolated from TCP hosting.
- Duplicate/stale invite responses, failed lobby joins, mismatched protocols,
  canceled connection attempts, late lobby creation, and route discovery delays
  are handled explicitly.
- Large mod compatibility profiles are fragmented to the transport chunk limit.

The production Steam socket, listener and invite service are tested using a
simulated native Steam boundary. Tests cover multi-megabyte snapshots, congestion,
disconnects, connection-handle isolation, same-lobby snapshot reconnection,
mixed real TCP/Steam command ordering and admission, and invite/error handling.
The production snapshot coordinator is tested with mocked Unity scene/save
boundaries. The mod also builds against the actual installed Steamworks library.

**These checks do not establish that a real Steam relay session has succeeded.**
Two separate Steam accounts and a live Unity game are needed for that validation.

## Playtest checklist

Use this sequence on both computers after installing and restarting:

1. Disable/disconnect Hamachi for this test. Join using **Invite Friends**, not IP.
2. Load the settlement and play at different speeds; check cursor/activity and HUD.
3. Select **Resync from host** once. Both players should reload and resume without
   another invite. Check camera zoom, selection, and building controls afterward.
4. After at least two minutes of stable play (the loop guard), briefly interrupt
   the guest's Internet connection and restore it. The host should pause and
   recover. Longer interruptions may legitimately exceed the reconnect window.
5. Return to the main menu normally; the guest's intentional leave should not
   trigger automatic recovery.
6. Start another session using your existing Hamachi/direct-IP method to verify
   that route still works. A mixed Steam/IP session can be tested with a third
   player if available.

If joining or recovery fails, capture both Player.log files and any new local
rolling diagnostic ZIPs before restarting. Include whether the guest joined
through Steam or IP and which step failed.

## Reproduce automated checks

Run 'dotnet run --project SteamTests -c Release' and the existing StabilityTests,
SnapshotChecks, StatusTests, DiagnosticsTests, ActivityTests and RuntimeChecks
harnesses. RuntimeChecks requires the compiled mod and installed game/dependency
paths; see its source usage. Run Python unittest discovery in RuntimeChecks.
Seven old transport tests were replaced by coverage of the new Steam transport;
all non-Steam regression checks remain.

**246 automated checks passed**; Release Steam build completed with zero errors.
