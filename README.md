# BeaverBuddies — Multiplayer Stability Fork

Smoother, more reliable co-op for **Timberborn 1.1**, built on [BeaverBuddies by thomaswp](https://github.com/thomaswp/BeaverBuddies).

This fork focuses on reducing multiplayer desyncs, fixing crashes, cutting mod overhead, and restoring normal controls after reconnecting. It keeps BeaverBuddies' shared-settlement co-op experience, with additional fixes for the simulation and networking problems encountered during play.

**[Download the compiled mod](https://github.com/kramsey458/BeaverBuddies/releases/download/v1.1.0-stability.8/BeaverBuddies-stability-preview8.zip)** · **[Release notes](https://github.com/kramsey458/BeaverBuddies/releases/tag/v1.1.0-stability.8)** · **[Full changelog](STABILITY-CHANGELOG.md)**

Current build: **1.1.0-stability.8** · Built and tested against **Timberborn 1.1.2.4** · No compilation required

## What this fork improves

These changes are relative to the upstream `v1.1` code this fork was based on; they are not a claim about changes in later upstream releases.

| Area | Improvements | Benefit during multiplayer |
| --- | --- | --- |
| Simulation consistency | Water-source strength uses simulation time instead of rendered frame duration; water sources have a consistent processing order. | Addresses water and badtide desync causes when players have different frame rates or source ordering. |
| Beaver job selection | Equally distant demolition jobs use persistent target IDs to break ties. | Removes job-list order as a reason for players to select different demolition targets. |
| Crashes and connections | Guards animation path interpolation, handles partial network reads, keeps packet headers and payloads together, and improves connection cleanup. | Addresses identified animation crashes and malformed or interrupted network messages. |
| Random-state handling | Restores gameplay/random-state scopes even when calls are nested or throw exceptions. | Prevents local presentation work or failed calls from leaving gameplay random-state handling incorrect. |
| Joining and failed actions | Checks game and loaded mod builds before loading the shared map; stops replay if a multiplayer action fails. | Catches mismatched builds early and prevents continuing simulation after a partially failed action. |
| Performance | Reuses diagnostic buffers, processes event backlogs more efficiently, parses incoming messages once, and avoids unnecessary routine logging. | Reduces allocations and CPU work in the mod, especially with diagnostics or event backlogs. |
| Rehosting and controls | Clears stale device and button states after a desync and multiplayer reload, including direct-IP rehosting. | Helps restore normal keyboard, mouse, and scrolling behavior without restarting the game. |
| Troubleshooting | Adds water-state snapshots and more useful target-selection traces. | Makes remaining desyncs easier to investigate. |

The fork owner reports that the current build works well in multiplayer. It does not eliminate every possible desync or guarantee compatibility with all other mods. Performance improvements reduce specific mod overhead; they are not a measured promise of higher game FPS.

## Install

1. **Close Timberborn on both computers.**
2. Download **BeaverBuddies-stability-preview8.zip** from the link above. The source archives are for developers.
3. Extract the `BeaverBuddies-StabilityPreview` folder into your actual `Documents/Timberborn/Mods` folder. When upgrading, replace the previous fork's files.
4. In the game's mod menu, enable **BeaverBuddies - Stability Preview**, version **1.1.0-stability.8**, plus **Harmony** and **Mod Settings**. Disable the standard Workshop BeaverBuddies and duplicate local copies.
5. Restart the game on both computers. Host or join using the usual BeaverBuddies flow, including direct IP and port.

**Every player must use the same compiled ZIP.** The compatibility check compares the loaded game and BeaverBuddies/TimberNet builds. Replacing files while the game is running is not enough; restart after updating. Separately compiled copies can also be rejected.

Keep other gameplay mods and their settings consistent between players. The handshake does not verify every third-party mod. Back up your save before changing your mod setup.

For general co-op setup, see the [upstream wiki](https://github.com/thomaswp/BeaverBuddies/wiki). Install this fork from this repository's releases; upstream Workshop and mod.io downloads contain the standard mod.

## Validation and reporting problems

The current build passed **83 automated checks** covering networking, replay, animation, simulation timing, diagnostics, demolition selection, and input recovery. The Release Steam build completed successfully, and the fork owner has reported successful playtesting.

Automated checks do not replace testing inside Unity: native device reset is mocked, and the optional live Harmony installation fixture is unavailable in the test runtime. See the [test instructions](StabilityTests/README.md) and [changelog](STABILITY-CHANGELOG.md) for details.

For a desync or crash report, include both players' `Player.log` files, game/mod versions, other enabled mods, and what happened just before the failure. Capture logs promptly: restarting the game rotates them. Detailed logging can help diagnosis but adds CPU and network overhead.

## Development

Clone this fork's `v1.1` branch:

```sh
git clone --branch v1.1 https://github.com/kramsey458/BeaverBuddies.git
```

Copy the appropriate `env.props` template to `BeaverBuddies/env.props` and configure your Timberborn, Harmony, Mod Settings, and mod output paths. With the .NET SDK and those local dependencies installed, build from the repository root:

```sh
dotnet build BeaverBuddies/BeaverBuddies.csproj -c "Release Steam"
```

The game assemblies are not included. For project background, see the [upstream contribution guide](https://github.com/thomaswp/BeaverBuddies/wiki/Contributing).

## Credits and license

BeaverBuddies and its multiplayer foundation were created by [thomaswp and upstream contributors](https://github.com/thomaswp/BeaverBuddies). This is an unofficial stability and performance fork, based on upstream `v1.1` commit `a13b1f20dacb6e30efa967cc8ac83e73779c0755`.

Distributed under the [GNU GPL v3 license](License.txt). Version-by-version development history is preserved in the [changelog](STABILITY-CHANGELOG.md).

