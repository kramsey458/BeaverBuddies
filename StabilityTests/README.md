# Stability regression checks

Preview 10 adds `ActivityTests` (production activity service with Unity/game test
doubles) and activity transport checks in `StabilityTests`. Run both using
`dotnet run --project ActivityTests` and `dotnet run --project StabilityTests`.
The latter includes a real TCP host with two guests, paused activity relay without
hash changes, identity checks, bounded coalescing and gameplay queue priority.
See [PLAYER-ACTIVITY.md](../PLAYER-ACTIVITY.md) for native two-player acceptance tests.
The complete Preview 10 suite passed 133 checks: 48 StabilityTests, 12 ActivityTests,
14 SnapshotChecks, 57 RuntimeChecks, and 2 Python water checks.

Preview 5 adds fragmented-stream handshake tests for matching, mismatched and
legacy peers, timeout cleanup, and failure notification in both directions.
The production ReplayExecution helper is tested with partial mutation, an error
handler that also throws, and early-stop/nested-scope cases.

RuntimeChecks also invokes all ten production RNG scope prefixes/finalizers
with nesting and cleanup, and checks restoration of the ticker flag. To attempt
actual Harmony patch installation on a managed fixture, set
`BEAVERBUDDIES_TEST_HARMONY=1`. This optional integration test fails under the
current .NET 8 harness because the installed MonoMod dependency cannot access
SignatureHelper.GetMethodSigHelper. It is not counted in the 64 passing checks;
live Unity/Harmony installation and two-player playtesting remain outstanding.

Run `dotnet run --project StabilityTests` from the repository root with .NET 8.
This builds TimberNet and links the production SteamSocket, SteamPacketListener,
and animation patch source. Steam and Unity APIs are test doubles; no game or
Steam client is required. Animation tests model a forward-only path cursor and
invalid visual coordinates, not a running Unity water simulation.

To compare against another checkout:
`dotnet run --project StabilityTests -p:SourceRoot=/absolute/path/to/checkout`

The separate RuntimeChecks executable tests the actual compiled mod's RNG
wrappers and save flags using the installed game's managed assemblies. It also
runs the actual managed UpdateWaterSourcesTask on a small overlapping-source
fixture, verifies identical results across six registration orders after the
ordering fix, and checks water diagnostic snapshots and field hashes:

```
dotnet run --project RuntimeChecks -- /path/to/BeaverBuddies.dll /path/to/Timberborn_Data/Managed /path/to/Harmony-directory
```

Both executables exit nonzero on failure. Neither verifies full multiplayer
determinism or executes Unity's native simulation. Build BeaverBuddies using
the repository's env.props setup before running RuntimeChecks.

Preview 4 also clones the installed game's depth-source modifier IL, substitutes
a controlled frame clock and depth-query stub, and exercises the production
timing transpiler. It reproduces frame-rate-dependent output before the patch
and checks matching ramp values after it. This tests the real ramp arithmetic
and emitted patch, but not Harmony installation inside Unity or depth sensing.

Preview 3 water diagnostic ZIPs can be compared with Python (no extra packages):

```
python RuntimeChecks/compare_water_snapshots.py host-water.zip client-water.zip
python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"
```

## Preview 11 checks

`StabilityTests` includes full-profile validation, post-load admission/rejection over real TCP, bounded rolling report buffers and retention. `dotnet run --project DiagnosticsTests` exercises the production compatibility scanner and sampler with API-shaped game stubs. Run `python -m unittest discover -s RuntimeChecks -p "test_*.py"` for both water and rolling-report comparisons. Native Unity scenes and actual two-player play are not exercised by these harnesses.
