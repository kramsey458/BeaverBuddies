# Connection panel

A small panel in the corner of your screen during a multiplayer game. It shows who is
connected, how good each connection is, whether you are in sync, and how fast the
simulation is running. It can be collapsed to a single line or hidden completely.

## What it shows

**Collapsed** (one line): a colored dot, the number of players, and one ping.

```
o  3 players  42 ms                                   +
```

**Expanded:**

```
o  Multiplayer                              Host     -
o  In sync
-------------------------------------------------------
o  Kyler                                 You / Host
o  Sarah                                       42 ms
o  Bob                                        190 ms
-------------------------------------------------------
Tick rate   1.7 ticks/s
Speed       1x
Connection  Direct
```

| Item | Meaning |
| --- | --- |
| **Status** | *In sync* is normal. *Catching up* (guests): this game is a few ticks behind the host. *Waiting for host* (guests): nothing has arrived from the host for a moment. *Connection unstable*: someone has stopped responding for five seconds. *Out of sync*: a desync was detected. *Disconnected*: the session has ended. |
| **Players** | Everyone in the session, host first. **You** marks your own row. |
| **Ping** | Round-trip time to the host in milliseconds. Green dot: 80 ms or less. Yellow: up to 160 ms. Red: more, or **No response**. Grey `...`: not measured yet. |
| **Tick rate** | Simulation ticks per second right now, averaged over about three seconds. Around 1.7 at normal speed; it rises with game speed and drops to 0 when paused. |
| **Speed** | The current game speed, or Paused. |
| **Behind host** | Guests only: how many ticks behind the host this game is. Should sit at 0 or 1. |
| **Slowest guest behind** | Host only: how many ticks behind the slowest guest was at its last report, about once a second. Shown once a guest running 1.0.4 or newer has reported. |
| **Easing off for guests** | Host only, and only while it applies: the share of the chosen speed the host is running at because a guest cannot keep up. It returns to full speed by itself. Reads **waiting for a guest to catch up** while the host stands still for a guest more than 60 ticks behind (1.0.6). |
| **Connection** | How players are connected: Direct (IP, including Hamachi or port forwarding) or Steam. |

The host sees every guest's ping. A guest sees their own ping in the pill and the other
players' pings **to the host**, which is the connection that matters for keeping in sync.

## Showing, collapsing and hiding

- **Click the title** to collapse or expand the panel. Your choice is remembered.
- **Mod Settings -> BeaverBuddies -> Connection panel:** Expanded, Collapsed or Hidden.
- **Mod Settings -> BeaverBuddies -> Connection panel position:** top left (default), top
  right, bottom left or bottom right.
- **Options -> Bindings -> BeaverBuddies -> Toggle connection panel:** an optional key to
  hide and show the panel. It is unbound until you choose a key.

The panel is docked into the game's own interface, so it scales with your UI scale and
does not overlap other panels in the same corner. It appears only in multiplayer games.

## How ping is measured

Once a second the host sends each guest a tiny probe, and the guest answers immediately on
its network thread, so the number reflects the network and not how busy that player's game
is. The host smooths the results (so a single spike does not jump around) and publishes a
short roster that every guest receives. Names come from the same **Ping Display Name**
players already use for cursors and pings; if a player has activity indicators turned off,
they appear as "Player N".

## It cannot affect the game

Probes and the roster use the same separate lane as cursor activity. They are never part
of the replay script or the desync hash, are handled before they can reach the game's event
queue, are never sent to a guest who is still joining, and are validated on arrival; a
malformed frame is ignored and never ends the session. The panel only reads: it sends no
gameplay event. If it ever fails, it disables itself and the game continues.

## Validation

`dotnet run --project StabilityTests` (73 checks) covers:

- the round-trip tracker: smoothing, jitter, ignored duplicate, unknown and expired
  replies, and silence measured from the last reply;
- the wire format, including rejection of every malformed roster and probe frame;
- real host and guest sessions: pings measured for each guest, a guest with 80 ms of
  injected delay reading slower than a prompt one, every guest receiving the roster with
  its own id, status traffic changing neither the hash, the event script nor tick progress,
  bad frames being ignored, a departed guest leaving the roster, and no status traffic
  before a joining guest has its save, state and init frames;
- the panel's wording and states without a game: ping colors and boundaries, the tick-rate
  window, host and guest views, the priority between statuses, silent players, placeholders,
  numbers formatted the same in every culture, and that every string the panel asks for
  exists in the English file.

**Not verified: how it looks.** The panel's layout, colors, spacing and where it sits in each
corner have not been seen in the running game; that needs a screenshot from a real
session. The controls (click to collapse, the settings, the optional key) are likewise
untested in the game.

## Known limits

- Other languages show the English text for the new strings.
- Ping is measured about once a second, so it lags a sudden change slightly.
- The panel does not show packet loss or bandwidth.
