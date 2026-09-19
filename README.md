# BeaverBuddies — Multiplayer Stability Fork

Smoother, more reliable co-op for **Timberborn 1.1**, built on [BeaverBuddies by thomaswp](https://github.com/thomaswp/BeaverBuddies).

This fork focuses on reducing multiplayer desyncs, fixing crashes, cutting mod overhead, and restoring normal controls after reconnecting. It keeps BeaverBuddies' shared-settlement co-op experience, with additional fixes for the simulation and networking problems encountered during play.

**[Download the compiled mod](https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork/releases/download/v1.1.0-stability.16/BeaverBuddies-stability-preview16.zip)** · **[Release notes](https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork/releases/tag/v1.1.0-stability.16)** · **[Full changelog](STABILITY-CHANGELOG.md)**

Current preview: **1.1.0-stability.16** · Built and tested against **Timberborn 1.1.2.4** · No compilation required


Preview 16 keeps Steam invites, direct-IP multiplayer, the readable status panel, player activity, diagnostics, and the stability/performance fixes. Automatic host snapshot resync, reconnect grace, and full mod compatibility checks have been removed. See [Steam hosting instructions](STEAM-INVITES.md).

## What this fork improves

These changes are relative to the upstream `v1.1` code this fork was based on; they are not a claim about changes in later upstream releases.

| Area | Improvements | Benefit during multiplayer |
| --- | --- | --- |
| Simulation consistency | Water-source strength uses simulation time instead of rendered frame duration; water sources have a consistent processing order. | Addresses water and badtide desync causes when players have different frame rates or source ordering. |
| Beaver job selection | Equally distant demolition jobs use persistent target IDs to break ties. | Removes job-list order as a reason for players to select different demolition targets. |
| Crashes and connections | Guards animation path interpolation, handles partial network reads, keeps packet headers and payloads together, and improves connection cleanup. | Addresses identified animation crashes and malformed or interrupted network messages. |
| Random-state handling | Restores gameplay/random-state scopes even when calls are nested or throw exceptions. | Prevents local presentation work or failed calls from leaving gameplay random-state handling incorrect. |
| Performance | Moves compression and socket writes to ordered queues per connection; reuses diagnostic buffers, processes event backlogs more efficiently, parses incoming messages once, and avoids unnecessary routine logging. | Reduces allocations and CPU work in the mod, especially with diagnostics or event backlogs. |
| Rehosting and controls | Clears stale device and button states after a desync and multiplayer reload, including direct-IP rehosting. | Helps restore normal keyboard, mouse, and scrolling behavior without restarting the game. |
| Connection status | Compact HUD shows response time, simulation rate, send queues and guest backlog; collapse or hide it and restore it from the pause menu. | Helps distinguish connection delays from simulation catch-up. |
| Troubleshooting | Keeps bounded rolling RNG, command, water, job and inventory diagnostics, with automatic local reports on desyncs and failures. | Preserves evidence for remaining desyncs without enabling heavy tracing. |
| Player activity | Shows remote cursors at 50% opacity, selection outlines and building Viewing/Editing labels. | Makes shared settlement work easier to coordinate. |

The fork owner confirmed Steam invites and the status-panel hotfix working in Preview 15. Preview 16 requires a fresh in-game multiplayer test. Performance changes reduce specific mod overhead; they are not a measured promise of higher game FPS.

See [status panel controls](CONNECTION-STATUS.md) for hide/restore instructions.

## Installation

1. **Close Timberborn on both computers.**
2. Download **BeaverBuddies-stability-preview16.zip** from the link above. The source archives are for developers.
3. Extract the `BeaverBuddies-StabilityPreview` folder into your actual `Documents/Timberborn/Mods` folder. When upgrading, replace the previous fork's files.
4. In the game's mod menu, enable **BeaverBuddies - Stability Preview**, version **1.1.0-stability.16**, plus **Harmony** and **Mod Settings**. Disable the standard Workshop BeaverBuddies and duplicate local copies.
5. Restart the game on both computers. Host or join using the usual BeaverBuddies flow, including direct IP and port.

**Use the same compiled ZIP, game version, gameplay mods, and gameplay settings on every computer.** These are no longer scanned or enforced by a full compatibility check. Restart after updating. Steam still checks its network protocol so an incompatible invite cannot be treated as a save transfer.

For general co-op setup, see the [upstream wiki](https://github.com/thomaswp/BeaverBuddies/wiki). Install this fork from this repository's releases; upstream Workshop and mod.io downloads contain the standard mod.

## Diagnostics and manual rehosting

Default-on rolling diagnostics keep a bounded history of RNG, commands, sampled water, jobs and inventories. Desyncs, replay failures and connection loss can save local ZIP reports, with the newest ten retained. Reports are written in the background and are not automatically uploaded. See [report locations and comparison instructions](COMPATIBILITY-DIAGNOSTICS.md).

A desync uses the normal manual **Save and rehost** flow. After rehosting, send a new Steam invite or have direct-IP guests reconnect. An unexpected guest disconnect does not automatically pause/reload the host; the disconnected guest is paused and notified. There is no automatic retry countdown or **Resync from host** menu option.

## Validation and reporting problems

The Release Steam build and automated suites cover networking, replay, activity, rolling diagnostics, simulation timing and input cleanup, including real loopback TCP and simulated native Steam tests. Live multiplayer validation of Preview 16 remains pending.

Automated checks do not replace testing inside Unity: native scene/save/UI boundaries and device reset are mocked in the relevant harnesses, and the optional live Harmony installation fixture is unavailable in the test runtime. See the [test instructions](StabilityTests/README.md) and [changelog](STABILITY-CHANGELOG.md) for details.

For a desync report, include both players' `rolling-*.zip` files from `AppData/LocalLow/Mechanistry/Timberborn/BeaverBuddiesDiagnostics` and both `Player.log` files, game/mod versions, other enabled mods, and what happened just before the failure. Capture logs promptly: restarting the game rotates them. Detailed logging can help diagnosis but adds CPU and network overhead.

## Development

Clone this fork's `v1.1` branch:

```sh
git clone --branch v1.1 https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork.git
```

Copy the appropriate `env.props` template to `BeaverBuddies/env.props` and configure your Timberborn, Harmony, Mod Settings, and mod output paths. With the .NET SDK and those local dependencies installed, build from the repository root:

```sh
dotnet build BeaverBuddies/BeaverBuddies.csproj -c "Release Steam"
```

The game assemblies are not included. For project background, see the [upstream contribution guide](https://github.com/thomaswp/BeaverBuddies/wiki/Contributing).

## Credits and license

BeaverBuddies and its multiplayer foundation were created by [thomaswp and upstream contributors](https://github.com/thomaswp/BeaverBuddies). This is an unofficial stability and performance fork, based on upstream `v1.1` commit `a13b1f20dacb6e30efa967cc8ac83e73779c0755`.

Distributed under the [GNU GPL v3 license](License.txt). Version-by-version development history is preserved in the [changelog](STABILITY-CHANGELOG.md).
