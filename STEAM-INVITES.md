# Steam invites and direct-IP multiplayer (Preview 16)

1. Update both computers to Preview 16 and restart Timberborn through Steam.
2. Keep **Enable Steam Networking** enabled, then **Host co-op game**.
3. Choose **Invite Friends**. The friend accepts and downloads the host save.
4. Choose **Start Game** after the friend appears in the connected-player list.

Steam relay connections do not require Hamachi or router port forwarding. Direct IP still uses TCP and needs a reachable host address/port. Initial save transfer, download progress, worker-thread receiving, ordered sending and backpressure handling remain.

## Desyncs and disconnects

There is no automatic snapshot resync, reconnect grace period, or full mod compatibility scan. Keep game versions, gameplay mods and settings aligned manually. Steam retains a lightweight network-protocol check; Preview 15 and Preview 16 cannot share a session.

Use **Save and rehost** after a desync. Steam guests should accept a new invite; direct-IP guests reconnect to the host. Joining after the host has begun simulation still requires rehosting. An unexpected guest drop does not pause or reload the host. The disconnected guest pauses and receives a message; no automatic retry runs.

## Validation

Steam socket/listener/invite tests simulate the native Steam boundary and include multi-megabyte save transfer and mixed Steam/real TCP event ordering. Additional tests exercise disconnects, manual fresh sessions, replay failures, activity, diagnostics, status, input cleanup and localization. The production mod builds against the installed game and Steamworks assemblies.

The owner confirmed Steam invites in Preview 15. Preview 16 still needs a live two-player playtest: join via Steam, play, disconnect the guest, confirm the host continues, manually rehost and accept a new invite. Also test the direct-IP route and status/activity display.

Capture both Player.log files and local rolling diagnostic ZIPs when reporting problems.

Run the SteamTests, StabilityTests, StatusTests, DiagnosticsTests, ActivityTests and RuntimeChecks harnesses, plus Python unittest discovery in RuntimeChecks. RuntimeChecks requires the built mod and game/dependency paths.
