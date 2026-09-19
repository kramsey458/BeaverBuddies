# BeaverBuddies — Stability Fork

Multiplayer co-op for Timberborn, with **Steam friend invites**, an **in-game connection panel**, and a long list of crash and desync fixes.

**Latest release: [1.0.0](https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork/releases/latest)** · built for Timberborn **1.1.2.4** · tested on Windows with the Steam version of the game · GPL-3.0

This is an independent fork of [thomaswp/BeaverBuddies](https://github.com/thomaswp/BeaverBuddies), the original multiplayer mod. It keeps everything the original does (players build one colony together in real time, each with their own camera and interface, multi-start maps, map pings, hosting and joining from the in-game menus) and builds on top of it. All credit for the multiplayer design belongs to the original project. Please report problems with *this fork* here, not to the original project.

## Highlights

- **Steam invites work.** Invite a Steam friend from Steam's own overlay and they join with a click: no Hamachi, no port forwarding. Confirmed in real playtests with a friend over Steam. Direct IP still works, and you can offer both at once.
- **A connection panel in the game.** See who is connected, each player's ping, whether you are in sync, the tick rate and more, in a small panel you can collapse or hide.
- **See what your teammates are doing.** Colored, translucent cursors, selection outlines, and "Viewing / Editing" labels on buildings, with per-player cursor color, size and transparency.
- **Fewer crashes and desyncs.** Specific, documented fixes for water, animation, random numbers, saving, demolition and input problems (details [below](#how-this-fork-improves-on-the-original)). This reduces known causes; it is **not** a guarantee that a desync can never happen.
- **Mismatched builds are caught early.** Joining with a different build is refused before the save is sent, with a message that says what to do, instead of failing halfway through.
- **Failures are explained.** A failed connection or multiplayer action ends with a plain-language reason (including Steam's own error code) instead of a silent hang.
- **Tested.** 144 automated checks, including runs against the game's own assemblies. See [Testing](#testing-and-verification).

## Install

**You need:** Timberborn (this release is built and tested against **1.1.2.4**), with the **Harmony** and **Mod Settings** mods enabled. Every player must run the same game version too.

1. Download `BeaverBuddies-stability-1.0.0.zip` from the [latest release](https://github.com/kramsey458/BeaverBuddies-Multiplayer-Stability-Fork/releases/latest).
2. **Close Timberborn.**
3. Extract the zip and copy the `BeaverBuddies-StabilityPreview` folder into `Documents\Timberborn\Mods`, replacing any older copy.
4. Start Timberborn and enable **BeaverBuddies - Stability Fork** (v1.0.0) in the mod list. **Disable the Workshop BeaverBuddies and any other BeaverBuddies copy**: they share the same mod ID and will conflict.
5. **Every player must install the exact same download** and restart the game. This is the most common cause of trouble; see [Things to know](#things-to-know-before-you-play).

This fork is distributed through GitHub Releases only. The Steam Workshop and mod.io pages linked further down belong to the original project.

## Host and join

**Host**
1. Load the save you want to play and choose **Host co-op game**.
2. Bring your friends in: for Steam choose **Invite Friends**; for direct IP give them your IP address (default port **25565**, which must be forwarded, or use a VPN such as Hamachi).
3. When your friends appear in the connected-player list, choose **Start Game**.

**Join**
- **Steam:** accept the invite. If Timberborn is closed, Steam launches it and joins for you. With **Allow Friends to Join Directly via Steam** on, a friend can also use **Join Game** from Steam's friends list.
- **Direct IP:** from the main menu choose **Join co-op game** and enter the host's IP address or domain name.

Guests receive a copy of the host's save (kept under **Online Games**). Nobody can join after the host chooses **Start Game**.

**If a desync happens:** the host chooses **Save and Rehost**. Steam guests accept a fresh invite; direct-IP guests reconnect.

## Steam invites

Steam friend invites are a first-class way to play, alongside direct IP.

- **Requirements:** both players online in Steam, both owning Timberborn, and both running the exact same build.
- **Settings** (Mod Settings → BeaverBuddies): **Enable Steam Networking** and **Allow Friends to Join Directly via Steam**.
- **Who can join:** the host opens a friends-only Steam lobby, and only players who joined that lobby are accepted. A stranger who knows your Steam ID cannot connect.
- **How it works:** connections go straight between players when Steam can find a route and are otherwise relayed through Steam's network. Valve documents that relaying keeps players' IP addresses hidden from each other. The original used Valve's older networking API, which Valve now marks as deprecated; this fork uses the current one.
- **If Steam has a problem,** hosting over direct IP still works. If joining hangs on "Receiving map...", the two players are almost certainly running different builds.
- **Status:** confirmed working in real playtests between the maintainer and a friend. More details, including how to read the log if something fails, are in [STEAM-INVITES.md](STEAM-INVITES.md).

## The connection panel

A small panel appears in the top-left corner during a multiplayer game.

| It shows | Meaning |
| --- | --- |
| **Players** | Everyone in the session, host first. |
| **Ping** | Round-trip time to the host. Green dot: 80 ms or less. Yellow: up to 160 ms. Red: higher, or "No response". Grey "...": not measured yet. |
| **Sync status** | In sync, Catching up, Waiting for host, Connection unstable, Out of sync, or Disconnected. |
| **Tick rate and speed** | Simulation ticks per second right now, and the game speed or Paused. |
| **Behind host** | Guests only: how many ticks this game is behind the host (0 or 1 is normal). |
| **Connection** | Direct or Steam. |

- **Collapse it** by clicking its title; it shrinks to one line and remembers your choice.
- **Hide it or move it** in Mod Settings → BeaverBuddies: **Connection panel** (Expanded / Collapsed / Hidden) and **Connection panel position** (any corner).
- **Optional key:** bind **Toggle connection panel** under Options → Bindings → BeaverBuddies. It is unbound until you choose a key.

Ping is measured by the network layer (a tiny probe once a second), so it means the same thing over Steam, Hamachi and direct IP, and it never touches the game simulation. Full details: [CONNECTION-PANEL.md](CONNECTION-PANEL.md).

## How this fork improves on the original

The comparison below is against the original project's `v1.1` branch at the point this fork branched (commit `a13b1f2`, 24 August 2026). Since then the fork has changed 78 files (about 8,000 lines added). As of September 2026 the original's `v1.1` branch has not moved since that commit, so this comparison is current.

Each item says how well it is confirmed: **confirmed** means the maintainer verified it in a real multiplayer playtest; **tested** means it is covered by automated regression checks but has not been confirmed in a live session.

**Connections and Steam**

- **Steam networking rebuilt on Valve's current API.** The original used the older, deprecated API, with a fixed 128 KB/s cap on the save transfer. Failures now end with Steam's own reason in plain language, and a Steam problem can no longer stop direct-IP hosting. *Confirmed with a real Steam friend.*
- **Steam packet handling made robust.** A comment in the original's Steam read routine says it "will fail" if Steam merges several messages into one packet, and it logs "This is probably a bug!" when bytes are left over. The rebuilt transport keeps unread data between reads, checks read ranges and wakes blocked readers when a connection closes. It is tested with messages split mid-event and with a 220 KB event. Each network frame is also written under a lock, so a header and its payload can never be interleaved. *Tested.*
- **Mismatched builds refused up front.** The original only warned about a version mismatch after the save had loaded. The fork checks the game version and the exact mod build before the save is transferred, with a time limit and a clear message. *Tested.*
- **A failed multiplayer action stops safely.** Replay stops after a failed action, pending actions are discarded, the session pauses and peers are told, so two games do not quietly drift apart. Connection cleanup bugs were fixed at the same time. *Tested.*

**Desyncs and determinism**

- **Water no longer depends on frame rate.** The depth-limited water source advanced using render-frame time, so players at different frame rates saw different water. It now uses the simulation tick interval in multiplayer. *Confirmed: resolved a reported "badtide" desync.*
- **Water sources applied in a consistent order.** With several sources affecting one column, the installed game produced three different results across six registration orders; the fork produces one. This was not established as the cause of the badtide desync. *Tested.*
- **Stale saving flag fixed.** A flag could stay set after an exit save, making one player skip a moisture calculation, consistent with reported desyncs right at join. *Tested.*
- **Random-number bookkeeping made safe.** Nested random-number scopes are counted correctly and restored even when an error interrupts them. *Tested.*
- **Equal-distance demolition jobs chosen deterministically**, by persistent target IDs. *Tested; not yet confirmed in a playtest.*
- **Entity ID collisions handled explicitly.** A regenerated ID is now applied, and the game fails with a clear error if no unique ID can be found. *Tested.*
- **Stuck-controls recovery.** Input state is reset after a desync and when a multiplayer game loads. This is a targeted recovery measure: its root cause was not proven and the original report has not been confirmed fixed. *Tested with a mocked device reset.*

**Crashes**

- **Animation crash.** A path cursor that could move backward between ticks, and non-finite visual coordinates, are handled. *Confirmed.*
- **Demolition-selection crash.** Replaying an area selection that included buildings already demolished used to end the whole session. Missing ones are now skipped. *Tested; not yet confirmed in a live session.*

**Awareness and usability**

- **Player activity.** Other players' cursors, selection outlines, and Viewing / Editing labels, plus a **Player cursors** dialog (Options menu) for each player's color, size and transparency. See [PLAYER-ACTIVITY.md](PLAYER-ACTIVITY.md). *Confirmed.*
- **Steam invites and the connection panel**, described above. *Confirmed.*

**Performance.** Fewer allocations from diagnostics, faster handling of the event backlog, one JSON parse per network message instead of two, and routine logging skipped unless needed. In synthetic tests, 4,000 ordered event inserts went from about 439 ms to under 1 ms, and 16 diagnostic captures stopped allocating about 85 MB. These are not frame-rate measurements. *Confirmed to play well in a two-player playtest.*

**What the fork does not change.** It does not make desyncs impossible, and it has not been tried on more than two players. Everything the original provides (multi-start maps, pings, the pause-reduction setting, hosting and joining from the menus) is still there.

## Things to know before you play

- **Everyone must run the exact same build.** The mod compares the game version and the mod's own files when someone joins. A copy someone compiled themselves can be refused even when the version number matches. If one player is on a different build over Steam, joining can look like it is hanging on "Receiving map...".
- **Other mods and settings are not checked.** Only the game and BeaverBuddies are compared. Every player should have the same mods at the same versions, and settings that affect the simulation (for example **Reduce the number of forced pauses**) should match. A mismatched third-party mod is a classic cause of desyncs.
- **Join before the host starts.** Nobody can join a game that has already started. After a desync the host uses **Save and Rehost**.
- **Desyncs can still happen.** This fork reduces known causes, not all of them.
- **Tested with two players**, on Windows, with the Steam version of Timberborn 1.1.2.4. Other stores, platforms and larger groups have not been tested by this fork. Steam invites need the Steam version of the game.
- **"Post Bug Report" does not upload in this fork's builds.** The original's automatic upload needs an access token that these builds do not contain. If you hit a problem, keep the `Player.log` files from both players (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn`). **Always Use Detailed Logging** captures more but costs some performance; any diagnostic ZIPs are saved in the `BeaverBuddiesDiagnostics` folder next to the log.
- **Only one BeaverBuddies at a time.** This fork and the Workshop version use the same mod ID.
- **New text is English only.** Other languages fall back to English for the strings added by this fork.
- **Not a Workshop mod.** Update by downloading a new release and replacing the folder; there is no automatic update.

## Testing and verification

The 1.0.0 validation run passed **144 checks**: **87** in `StabilityTests` (network transport, the Steam transport against a simulated Steam network, protocol parity between direct and Steam connections, player activity, ping measurement and the panel), **55** in `RuntimeChecks` (the compiled mod running against the game's own assemblies: random-number scopes, water simulation, demolition, input recovery), and **2** Python snapshot-comparison checks. Both Steam and non-Steam builds compile with no warnings.

These checks cannot start Unity or prove full multiplayer determinism, and they need the game installed locally (no proprietary game files are included in this repository). See [StabilityTests/README.md](StabilityTests/README.md) for how to run them. The maintainer's real playtests, described above, are what confirm behavior in the live game.

## Credits and license

Every change in this fork is listed in [STABILITY-CHANGELOG.md](STABILITY-CHANGELOG.md).

Thank you to the original BeaverBuddies authors and contributors, whose work this fork builds on. Their license (GPL-3.0) and authorship are preserved in [License.txt](License.txt) and the repository history.

*Below the line is the original project's developer README, kept as it was. Its badges, Workshop, mod.io, wiki and Discord links, and the clone address in "How to Build", refer to the original project, not to this fork. To build this fork, clone this repository instead.*

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
