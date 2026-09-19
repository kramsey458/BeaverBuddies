# BeaverBuddies

## Stability fork — Preview 17

This fork contains the cumulative stability changes through **1.1.0-stability.17**,
based on [thomaswp/BeaverBuddies](https://github.com/thomaswp/BeaverBuddies)'s
`v1.1` branch at `a13b1f20dacb6e30efa967cc8ac83e73779c0755`.
It was built against Timberborn **1.1.2.4**. The fork owner has confirmed that
Preview 4 resolved the reported multiplayer badtide desync in their playtest.
This is not a guarantee against every possible desync or mod interaction.

Preview 17 is built directly on Preview 8. Previews 9 through 16 are deprecated
and are not part of this line, so none of their changes are included.

Preview 17 adds player activity indicators: other players' colored, translucent
cursors, remote selection outlines, and Viewing/Editing labels on buildings, plus an
in-game **Player cursors** dialog to set each player's cursor color, size and
transparency locally. See [PLAYER-ACTIVITY.md](PLAYER-ACTIVITY.md). It also fixes a
multiplayer crash where replaying an area selection that included entities already
demolished ended the session. The activity display needs a two-player playtest to
confirm it renders correctly; the crash fix is covered by a regression check but has
not been confirmed in a live session.

Preview 8 adds input-state recovery after desync and multiplayer scene loading,
including direct-IP rehosting. This targets stuck controls and requires a live
playtest to confirm the reported symptom is resolved.

Preview 7 makes equal-distance demolition selection deterministic in multiplayer
using persistent target IDs. It adds selection diagnostics for further investigation.
The reported incident is consistent with a job-selection issue, but this patch
has not yet been confirmed by multiplayer playtesting.

Preview 6 reduces diagnostic allocations, event-backlog processing, duplicate
JSON parsing and routine logging overhead. The fork owner confirmed Preview 6 works well in multiplayer playtesting.

Preview 5 adds build compatibility checking, stops replay after failed actions,
and makes remaining RNG scopes exception-safe. The fork owner has confirmed Preview 5 works well in their two-player playtest.

Preview 4 corrects a depth-limited water source that used render-frame duration
to advance gameplay state. It uses the configured simulation tick interval in
multiplayer instead. Earlier improvements cover animation crashes, network
packet handling, random-state scopes, save-state cleanup, water-source ordering,
and local desync diagnostics.

See [STABILITY-CHANGELOG.md](STABILITY-CHANGELOG.md) for the full cumulative
changelog, installation instructions and validation limits. The original
project and GPL license are retained; the Workshop and wiki links below refer
to the upstream project, not this fork's preview builds.

### Tests

The Preview 17 validation run passed **107 checks**: 50 transport/replay/animation/activity
checks, 55 compiled mod/game checks, and two Python snapshot-comparison checks.
The optional live Harmony-installation fixture is not included in that count;
see the changelog for its test-runtime limitation.
See [StabilityTests/README.md](StabilityTests/README.md) for commands and required
local game/Harmony assemblies. Those proprietary assemblies are not included.

---

[![Last commit](https://img.shields.io/github/last-commit/thomaswp/BeaverBuddies?label=Last%20commit&color=lightgray)](https://github.com/thomaswp/BeaverBuddies/commits)
[![License](https://img.shields.io/github/license/thomaswp/BeaverBuddies?label=License&color=gray)](https://github.com/thomaswp/BeaverBuddies/blob/master/License.txt)
[![Timberborn 1.0](https://img.shields.io/badge/Timberborn_1.0-compatible-peru)](https://mechanistry.com)
[![Discord mod thread](https://img.shields.io/badge/Discord-mod_thread-mediumpurple)](https://discord.com/channels/558398674389172225/1203786573142032445)  
[![Steam Workshop](https://img.shields.io/badge/Steam_Workshop-available-royalblue)](https://steamcommunity.com/sharedfiles/filedetails/?id=3293380223)
[![mod.io](https://img.shields.io/badge/mod.io-available-limegreen)](https://mod.io/g/timberborn/m/beaverbuddies)

BeaverBuddies is a mod to allow multiplayer co-op in Timberborn.

> [!IMPORTANT]
> **If you would like to use the BeaverBuddies mod**, please see [the setup instructions in the wiki](https://github.com/thomaswp/BeaverBuddies/wiki)! This README is for developers.

## Contributing

We appreciate your help! To get started working on BeaverBuddies, see [the guide in the wiki](https://github.com/thomaswp/BeaverBuddies/wiki/Contributing).

## How to Build BeaverBuddies

1. Clone this repo `git clone git@github.com:thomaswp/BeaverBuddies`.
2. Set up DotNet C#.  
   For Windows, download & install [Visual Studio community edition](https://visualstudio.microsoft.com/vs/community).  
   For Mac, either run `brew install dotnet` or download & install [DotNet SDK](https://dotnet.microsoft.com/en-us/download).
3. Build the project.  
   For Visual Studio, open the solution & hit Ctrl+Shift+B.  
   For DotNet SDK, go to the BeaverBuddies directory & run `dotnet build`.  
   You may get a few "directory not found" errors. To fix these, open `BeaverBuddies/BeaverBuddies/env.props` and adjust the environmental variables there to point to your Timberborn installation & the necessary mods.

Building on Linux is similar to on Mac.

## How to Test Your Build

1. Make sure your project has been built with no errors.
2. Confirm that the mod files were copied to your Timberborn mods folder (e.g. `Documents/Timberborn/Mods/BeaverBuddies`.
3. Launch Timberborn and select the BeaverBuddies mod on the mod selection screen.  
   There may be multiple BeaverBuddies mod entries. The one with a "folder" icon next to it is your local build, select it.
