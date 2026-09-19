# Connection and simulation status panel

Preview 12 adds a small native HUD panel in the upper-right stack, below the
existing game panels. It uses Timberborn's UI scaling and hides with the game HUD.
It appears only in a multiplayer-loaded scene.

## Controls

- **−** collapses it to a single status header; **+** expands it again.
- **×** hides it completely, including its mouse hit area.
- **Pause menu → Multiplayer status** restores a hidden panel.
- **Mod Settings → Connection and simulation status** controls visibility;
  **Collapse connection status** controls the remembered compact view.

Visibility and collapse choices are saved locally and survive rehosting/restarts.
No keyboard shortcut is added.
The panel is attached to the normal HUD, not an input overlay over the whole screen.
Long peer lists scroll within a capped area; detailed rows show up to eight peers,
with an explicit count of additional peers included in the response summary.

## Reading the panel

| Item | Meaning |
| --- | --- |
| Session | Host/guest role, guest count, or the guest's direct-IP/Steam transport. |
| Response | Latest application round-trip measurement, worst of fresh peer samples when hosting several guests. Includes sender queues and peer update processing; it is not a pure ICMP/network ping. |
| Simulation | Local simulation ticks per wall-clock second over a two-second window, plus the current tick. This is not rendering FPS. A dash means the measurement is warming up. |
| Send queue | Reliable event count and estimated uncompressed UTF-16 payload memory, including in-flight data. This is not compressed network bandwidth; disposable activity/status packets are excluded. |
| Received | Events waiting locally; on a guest, also how many already-received host ticks are buffered. |
| Peer rows | Response time, reported tick rate or pause/loading state, and approximate tick lag at the time of the reply. Uses connection IDs, not IP addresses. |

The summary explains **Loading**, **Paused**, **Catching up**,
**Guest behind**, **Waiting for response**, **Connection delayed**, or disconnection.
These are observations, not a claim to know the root cause of every stall.

- A sustained received tick buffer with responsive replies means this computer has
  simulation work waiting to be processed; packets have already arrived.
- High response time or a growing send queue points to delivery/peer-processing
  delay. It cannot by itself separate network latency from a busy remote game.
- A host that replies but reports zero running tick rate is not advancing its
  simulation in the last sample. Pauses and measurement warmup are treated separately.
- Peer lag is approximate because samples and packets arrive at different times.
  The host warning allows for sample cadence and transit time at higher tick rates.
- Measurements older than five seconds are marked awaiting response, not shown as
  current readings. Disconnection suppresses obsolete metrics.

## Overhead and synchronization

HUD text refreshes at most four times per second. Each connection sends at most
one probe per second when responsive, with one outstanding probe and a five-second
retry on silence. Replies are rate-limited. Requests and replies use bounded,
replaceable presentation queues and yield to reliable gameplay commands.

Metrics continue while paused or while someone hides the panel, so other players
can still see their connection and simulation progress. Messages are separate from
gameplay replay, tick advancement, RNG and desync hashes. No automatic recovery,
speed adjustment, timeout disconnection, or gameplay pause is added by this panel.

## Verification

Tests exercise the production status model and HUD code with native UI stubs,
monotonic-clock latency/timeout handling, malformed metrics, bounded probes,
gameplay priority, and real TCP host/two-guest telemetry while paused without
changing replay hashes. The mod builds against Timberborn 1.1.2.4.

Native Unity rendering still needs a live check. Install the same Preview 12 ZIP
on both machines, then confirm text placement at your UI scale, collapse/hide and
pause-menu restore, paused readings, catch-up during fast-forward, and cleanup
after **Resync from host**. The panel should not select buildings or zoom the camera
when interacting with its controls.
