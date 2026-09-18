# Stability previews: cumulative changelog



Based on upstream BeaverBuddies `v1.1` commit

`a13b1f20dacb6e30efa967cc8ac83e73779c0755`. All previews are cumulative.

Preview 4 was built against Timberborn 1.1.2.4.



## Preview 7 — 1.1.0-stability.7

- Choose exactly equal-distance demolition jobs by persistent entity ID in
  multiplayer. Preserve nearer-job preference, eligibility, priority and
  reservation rules; retain vanilla single-player behavior.
- Record selected target IDs and distances in detailed traces. Include up to
  eight eligible candidates in verbose local logs without treating harmless
  candidate-order differences as synchronized trace mismatches.
- 77 checks passed, including six new demolition selection checks. Release Steam
  build succeeded. Multiplayer playtesting is still required; the original
  Preview 6 logs did not prove an equal-distance tie caused that incident.

## Preview 6 — 1.1.0-stability.6



- Recycle expired water diagnostic arrays, preserving snapshot retention and

  captured values while removing steady-state map-sized allocations.

- Optimize ordered event insertion and bulk removal of consumed backlog.

- Parse each incoming transport frame once instead of twice.

- Gate routine event/packet/tick logging before formatting and serialization.

- 71 automated checks passed; Release Steam build succeeded. Synthetic tests

  measured about 85 MB versus zero bytes allocated for 16 warmed-up captures,

  and 438.92 ms versus 0.28 ms for 4,000 ordered inserts. These are not FPS tests.

- The fork owner confirmed Preview 6 works well in a two-player playtest. Detailed logging, synchronous sends

  and throttling remain performance costs. The optional Harmony installation

  fixture retains its previously documented test-runtime limitation.



## Preview 5 â€” 1.1.0-stability.5



- Negotiate compatibility before requesting or loading the shared map. Compare

  the running game version, full mod version, and loaded BeaverBuddies/TimberNet

  module IDs. Replacing files without restarting cannot disguise an old process.

- Reject older previews and mismatched binaries. Bound the compatibility wait

  to 15 seconds, close failed connections, and report an update/restart message.

- Stop replay after a failed action, discard pending actions, pause the session,

  notify connected peers, and restore the replay flag even if error handling

  throws. Block further simulation and rehosting until the scene is reloaded;

  the affected player is told to reload a known-good save.

- Make the remaining ten RNG classification patches exception-safe. Count

  nested calls instead of using simple set membership. Restore the ticker's

  prior RNG classification in a finalizer, including nested updates.

- Keep the ordinary RNG check enabled regardless of detailed logging settings.

- Synchronize client-list access during joins, broadcasts and shutdown.



Both players must install the same compiled archive and fully restart the game.

Independently compiled binaries may have different module IDs and be rejected.

This checks the game and BeaverBuddies binaries, not every third-party mod or its

settings. No changes to Housing Optimize, evaporation, district fallbacks, or

animation timing are included in this preview.



Validation: 64 passing checks (23 transport/replay/animation, 39 compiled-mod/game,

two Python snapshot comparisons), plus a successful Release Steam build against

Timberborn 1.1.2.4. The compiled scope checks exercise the production prefix and

finalizer methods. An additional attempt to install Harmony on a managed test

fixture failed because the installed MonoMod dependency could not access

SignatureHelper.GetMethodSigHelper under .NET 8; that integration check remains

opt-in and is not counted as passing. The fork owner subsequently confirmed Preview 5 works well in their two-player

playtest. A failure stop contains partial state; it does not roll back the action

or recover unsaved progress. Peer notification is best-effort if the connection

has already failed.



## Preview 4 â€” 1.1.0-stability.4



- Replace the render-frame clock in `WaterDepthStrengthModifier.GetStrengthModifier`

  with Timberborn's configured simulation tick interval during multiplayer.

  This prevents different frame rates from producing different water-seep output.

- Inject `ITickService` into the existing water-source buffer. Preserve the

  game's depth thresholds, hysteresis, fade speed, disabled-state reset and

  maximum-strength clamp. Single-player retains its original frame clock.

- Validate that the targeted method contains exactly one clock call to replace.

- Include individual source coordinates and exact strength/contamination bits

  in detailed traces.

- Add five runtime checks covering the original frame-rate divergence, the

  configured clock, matching patched results, reset/clamp behavior and rejection

  of an incompatible method body.



The fork owner confirmed Preview 4 resolved their reported badtide desync.

The regression experiment also reproduced different output at 30 and 144 FPS

using the installed game's ramp instructions, then verified identical output

after the production transpiler. Its depth query and frame clock are test doubles;

the test does not start Unity or install Harmony into a live game.



Evaporation settings are unchanged. The source ramp now advances by simulation

seconds rather than local frame duration, so its timing can differ from upstream.



## Preview 3 â€” 1.1.0-stability.3



- Apply water-source simulation snapshots in a consistent order by coordinates,

  strength and contamination. Preserve the live source registry and values.

- Correct a demonstrated order-dependent case when multiple sources affect

  the same water column. The installed game's source-update task produced three

  results across six registration orders; canonical ordering produced one.

  This case was not established as the cause of the reported badtide desync.

- Add separate hashes for active depth, contamination, overflow, geometry,

  inactive storage and source inputs without disabling existing checks.

- With detailed logging enabled, retain up to four water-map snapshots within

  a 64 MiB budget and write a local ZIP on desync. Reset capture between sessions

  and catch diagnostic failures. No automatic diagnostic upload was added.

- Add a Python tool to compare retained snapshots by tick, cell, field and

  exact floating-point bits.



## Preview 2 â€” 1.1.0-stability.2



- Restore the previous saving flag after exit saves, including exceptions,

  using a Harmony finalizer.

- Clear stale saving state when resetting between scenes.

- Restore the previous flag after deferred normal saves using `try/finally`.

- Address a defect consistent with immediate join-time desync reports in which

  one peer omitted a moisture trace because its saving flag remained set.



## Preview 1 â€” 1.1.0-stability.1



- Reset the animation path cursor before interpolation that can move backward

  between ticks. Fall back to the simulation position for non-finite visual

  coordinates before water/swimming listeners consume them. The fork owner

  reported that this fixed their crashing issue.

- Preserve unread Steam packet tails across partial reads, validate read ranges,

  handle zero-length reads, and respect the actual received byte count.

- Wake blocked readers on close and tolerate packets arriving during shutdown.

- Reject failed Steam sends and writes to closed sockets.

- Lock complete network frames so concurrent header/payload writes cannot mix.

- Close corrupted/failed connections, stop consuming events after failure,

  and deliver client error callbacks through the update thread.

- Close discarded client connections and prevent an old socket's cleanup from

  unregistering its replacement. Synchronize socket registry access.

- Preserve nested gameplay/non-gameplay RNG classification and restore it after

  exceptions in random-selection wrappers.

- Apply regenerated entity IDs to the entity builder and fail explicitly if a

  unique ID cannot be found after the retry limit.

- Add transport/animation regression and compiled-mod runtime test executables.



## Installation



1. Fully close Timberborn on both computers.

2. Extract the preview build into the actual `Documents/Timberborn/Mods` directory,

   replacing the prior `BeaverBuddies-StabilityPreview` files.

3. Enable **BeaverBuddies - Stability Preview**, version **1.1.0-stability.7**, on

   both computers. Disable Workshop BeaverBuddies and duplicate local previews.

4. Test a copied save. Both players must use the same preview.



For a diagnostic session, enable **Always Use Detailed Logging** on both peers.

It adds overhead. If a desync occurs, retain both Player.log files and the newest

water ZIP from each peer under:



`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics`



Large maps may retain fewer snapshots; peers running far apart may have no shared

retained ticks. Old ZIPs remain until removed. See

`RuntimeChecks/compare_water_snapshots.py` for comparison commands.



## Validation and limits



Preview 4: **41 passing checks** (13 transport/animation, 26 compiled mod/game,

two Python archive comparison). The mod builds successfully against the stated

game version. Tests require .NET 8; game-dependent checks additionally require

the user's installed game assemblies and Harmony directory. No proprietary game

assemblies, decompiled game code, player logs or saves are included in this fork.



The owner's playtest confirms the reported issue, not universal determinism.

Other game versions and combinations of mods may still have unrelated problems.

No Housing Optimize changes are included. Upstream authorship and GPL licensing

are preserved in `License.txt` and the repository history.

