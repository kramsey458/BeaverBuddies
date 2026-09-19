# Player activity indicators

Other players' cursors, selections and building activity are
shown on your screen, and you can restyle each player's cursor.

## What you see

- **Colored, translucent cursors** with a name label. Positions are shared in world
  space, so cameras can be at different positions and zoom levels, and markers
  interpolate between samples. A cursor over UI, outside the game window or off the
  map is hidden; losing application focus hides yours too.
- **Remote selection outlines.** Remote selected entities use Timberborn's native
  selection outline in that player's color. Each remote player has an independent
  secondary highlighter, and your own selection/hover colors take priority when you
  select the same object. Selecting an object never changes anyone else's selection
  or camera. This shares entity selections, not construction ghosts or drag-to-paint
  tool areas.
- **Viewing / Editing labels.** A selected building shows **Viewing: player name**.
  Issuing a building command through the mod's common entity-action path (recipes,
  worker counts, rename, priorities, water settings, automation) shows **Editing:
  player name** for three seconds after the most recent change. Viewing is never
  reported as editing. This is an advisory notice, not a lock: both players can still
  change a building, and the normal multiplayer command order decides the result.
  Third-party UI that bypasses BeaverBuddies' entity-action path cannot emit an edit
  notice, and zipline tools and global technology unlocks are not covered.

## Customizing each player's cursor

Open the in-game **Options** menu (Esc) and choose **Player cursors**. Each connected
player who is sharing activity gets a card with:

| Control | Range | Default |
| --- | --- | --- |
| Color | **Their color** (what they chose), 10 presets, or exact Red/Green/Blue sliders | Their color |
| Size | 50%–300% | 100% |
| Transparency | 0%–90% | 50% |

The color also applies to that player's name label and their selection outline, so a
player always looks like one consistent color. The swatch in each card previews the
color and transparency live, and **Reset** returns that player to the defaults.

These are **display-only, local choices**. They are never sent over the network, so
each player can style everyone else differently, and they cannot affect the
simulation.

Styles are remembered **by display name** (for example "Sarah"), so a friend keeps the
same look next time even though their player number changes. If two connected players
share a name, each is keyed by name plus player number so they can be styled
separately. Styles are saved as you edit, to
`BeaverBuddiesCursorStyles.json` in the game's persistent data folder
(`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn`). A missing, damaged or
read-only file just means default styles; values are clamped when loaded.

A player appears in the panel while they are connected and sharing. If someone turns
sharing off, they disappear from the list until they turn it back on.

## Settings

In Mod Settings, **Player activity indicators** turns sharing and display on or off.
**Ping Display Name** and **Ping Color** are the name and color other players see for
you here, and on your pings. Pick different names and colors for easier
identification; connection labels distinguish players even if their names match.
A host who disables their own display still relays other guests' activity.

## Performance and synchronization

- At most ten samples per second using unscaled time, including while paused. No
  catch-up bursts on slow frames, and no dependence on simulation speed.
- Activity frames use the existing TCP/Steam connection but are handled **outside the
  replay script and the desync hash**: they are intercepted on the receive thread
  before they can reach the event queue, so they cannot change tick order, replay, or
  hash state. The host assigns each guest's identity; a guest cannot claim another
  player's id. The host is player 0.
- Sending never blocks the game thread. Each connection keeps only the newest pending
  state per player and is drained by a single pooled task, and every frame goes
  through the same length-framed, stream-locked write path as gameplay events, so an
  activity frame cannot split a reliable event.
- A joining guest is not sent any activity until its map, state and init frames have
  been written, and a guest does not send until it has received the map.
- Incoming activity is validated (coordinates, ids, colors, name length, key count);
  a malformed frame is dropped and never ends the session. The receive mailbox keeps
  at most 64 latest states. Remote displays expire after three seconds without
  updates, and reload, session change, disable and disconnect clear overlays and
  highlights. Missing or deleted entities are ignored. No selectable objects, colliders
  or input controls are created, and no per-frame scene scans or gameplay random calls
  are used.
- If the overlay ever throws, it disables itself for that scene rather than affecting
  the game.

Because the build-compatibility handshake already requires identical mod builds,
every player in a session has this feature or none of them do.

## Validation

`dotnet run --project StabilityTests` (50 checks) covers:

- the production transport over in-memory streams with a host and two guests:
  identity assignment (a guest claiming another id is ignored), relay without echo,
  no change to hash / script / tick progress, dropped malformed frames, gameplay
  ordering under an activity flood, a joining guest receiving map -> state -> init
  before any activity, and disconnect cleanup;
- message validation, name sanitizing, the latest-wins mailbox and its cap, and the
  outgoing coalescing channel;
- the per-player style store: clamping, hostile or damaged files, entry caps, atomic
  save and reload, unwritable locations, and name-collision keys.

`dotnet run --project RuntimeChecks -- <BeaverBuddies.dll> <Timberborn Managed>
<Harmony dir> <ModSettings Scripts dir>` (53 checks) still passes against the
compiled mod.

**Not covered by automated tests:** how the cursor, labels and outlines actually
render, and the settings dialog's layout and slider behavior. Those need the real
game.

## Two-player playtest

1. Install the same build on every computer and restart. Confirm **Player activity
   indicators** appears in Mod Settings, and pick distinct names and colors.
2. Host and join normally. Move around the same building from different camera
   angles; check cursor position on terrain and building surfaces.
3. Select different buildings, then the same one. Check outlines and name labels,
   that your own selection color wins, and that you can scroll and click through a
   cursor marker.
4. Change a recipe, worker count or automation setting. **Editing** should appear for
   three seconds; merely leaving a panel open should show **Viewing**.
5. Open Options -> **Player cursors**. Check the other player's card and that
   dragging each slider updates their cursor live: color presets, RGB, Size and
   Transparency. Use **Reset**, close and reopen the dialog, then restart the game and
   confirm the style is remembered.
6. Have a third player join (or rename one) while the dialog is open and confirm the
   list updates. Try two players with the same name.
7. Repeat while paused and at faster speeds. Hover UI, alt-tab, disable and re-enable
   activity, remove a selected building, and disconnect a guest; stale marks should
   clear within about three seconds.
