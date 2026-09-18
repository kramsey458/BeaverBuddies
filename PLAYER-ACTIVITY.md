# Player activity (Preview 10)

Other players' cursors appear at **50% opacity**, with names next to them. Positions
are shared in world space, so cameras can be at different positions and zoom levels.
Markers interpolate between samples. A cursor over UI, outside the game window or
off the map is hidden; losing application focus clears the advertised selection too.

Remote selected entities use Timberborn's native selection outlines. Each remote
player has an independent secondary highlighter. Your primary selection/hover colors
take priority when you select the same object. Selecting an object never changes
someone else's selection or camera. This shares entity selections, not construction
ghosts or drag-to-paint tool areas.

A selected building shows **Viewing: player name**. Issuing a building command
through the mod's common entity-action path (such as recipes, worker counts, rename,
priorities, water settings or automation) shows **Editing: player name** for three
seconds after the most recent change. Viewing is not falsely reported as editing.
This is an advisory notice, not an exclusive lock: both players can still change a
building, with the normal multiplayer command order deciding the resulting state.
Third-party UI actions that bypass BeaverBuddies' entity-action path cannot emit an
edit notice. Zipline connection tools and global technology unlocks are not covered
by the common entity edit indicator.

In Mod Settings, **Player activity indicators** turns sharing and display on/off.
**Player display name** and **Player color** also control pings; existing preferences
are retained. Connection labels distinguish players even if their names match. Pick
different names/colors for easier identification. A host who disables their display
still relays other guests' activity.

## Performance and synchronization

- At most ten samples per second using unscaled time, including while paused. No
  catch-up bursts on slow frames and no dependence on simulation speed.
- Presentation packets use the existing TCP/Steam transport, outside replay and
  desync hashing. The host assigns each guest's identity rather than trusting a
  name or ID sent by the guest. The host is ID 0.
- Only the latest pending update per player is retained. Outgoing activity yields
  to gameplay/control messages; reliable commands keep their original FIFO order.
  The per-connection pending activity budget is 64 players, at most 4096 characters
  each, separate from the gameplay queue's limits. One small activity frame already
  being written cannot be preempted.
- The incoming mailbox also holds at most 64 latest states. Coordinates, entity IDs,
  colors and names are validated. Activity that aged out during scene loading is
  dropped. No per-frame scene scans or gameplay random calls are used.
- Remote displays expire after three seconds without updates. Reload, session change,
  recovery, disable and disconnect clear local overlays/highlights. Missing/deleted
  entities are ignored. The feature creates no selectable objects or input controls.

## Validation

`dotnet run --project StabilityTests` tests production transport, malformed payloads,
coalescing, queue priority/budgets, identity assignment, and real TCP host/two-guest
relay at tick zero without hash changes.

`dotnet run --project ActivityTests` compiles the production activity service against
controlled Unity/game boundaries to test cursor picking, UI/focus behavior, sampling,
highlight ownership, editing expiry, stale peers, settings, reload and recovery.
These doubles cannot prove native visual output or game container initialization.

The compiled mod must also pass RuntimeChecks, SnapshotChecks and the Python water
checks. A two-player Unity playtest remains necessary:

1. Install the exact same latest preview ZIP on every computer and restart. Confirm
   activity settings appear and choose distinct names/colors.
2. Host/join normally. Move around the same building from different camera angles;
   check translucent cursor positions at terrain and building surfaces.
3. Select different buildings, then the same one. Check remote outlines/name labels,
   local selection priority, scrolling and clicking through the marker.
4. Change a recipe, worker count or automation setting. Confirm **Editing** appears
   for three seconds and merely leaving a panel open shows **Viewing**.
5. Repeat while paused and at faster speeds. Hover UI, alt-tab, disable/re-enable
   activity, remove a selected building and disconnect a guest; check stale marks clear.
6. Use **Resync from host** on a spare save. Confirm old marks disappear and fresh
   activity appears after everyone reconnects. Repeat the cursor checks over Steam
   if using invites (Preview 9's Steam manual-rehost limitation still applies).
