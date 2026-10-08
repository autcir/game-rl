# RimWorld Integration Guide

## Overview

This guide covers integrating the game-rl Rust MCP server with RimWorld via Harmony runtime patching.

---

## Architecture

```
Agent Process (Python/Claude)
        │ MCP (stdio, JSON-RPC)
        ▼
harmony-server (Rust)
  ├── game-rl-server: MCP protocol
  └── harmony-bridge: IPC client
        │ Unix socket (MessagePack)
        ▼
RimWorld Process
  ├── GameRL.Harmony.dll (base library)
  └── RimWorld.GameRL.dll (game adapter)
```

---

## Project Structure

```
game-rl/
├── dotnet/
│   ├── GameRL.Harmony.sln
│   └── GameRL.Harmony/           # Base library (game-agnostic)
│       ├── Bridge.cs             # IPC server
│       ├── StateExtractor.cs     # Interface
│       ├── CommandExecutor.cs    # Interface
│       └── Protocol/Messages.cs  # Wire protocol
│
└── adapters/rimworld/
    ├── RimWorld.GameRL/          # Game adapter
    │   ├── Mod.cs                # Entry point
    │   ├── State/                # State extraction
    │   ├── Actions/              # Command execution
    │   ├── Rewards/              # Reward computation
    │   └── Patches/              # Harmony hooks
    ├── About/About.xml
    └── manifest.json
```

---

## Phase 1: Base Library

### GameRL.Harmony.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MessagePack" Version="2.5.140" />
    <PackageReference Include="Lib.Harmony" Version="2.3.3" />
  </ItemGroup>
</Project>
```

### Protocol Messages (must match Rust)

```csharp
// C# → Rust
[MessagePackObject]
public class StepComplete {
    [Key(0)] public string Type => "step_complete";
    [Key(1)] public ulong StepId { get; set; }
    [Key(2)] public ulong Tick { get; set; }
    [Key(3)] public byte[] Observation { get; set; }
    [Key(4)] public bool Done { get; set; }
    [Key(5)] public bool Truncated { get; set; }
}

// Rust → C#
[MessagePackObject]
public class ExecuteAction {
    [Key(0)] public string Type => "execute_action";
    [Key(1)] public string AgentId { get; set; }
    [Key(2)] public ulong StepId { get; set; }
    [Key(3)] public byte[] Action { get; set; }
    [Key(4)] public uint Ticks { get; set; }
}
```

### IPC Bridge

```csharp
public class Bridge : IDisposable {
    private Socket _socket;
    private ConcurrentQueue<GameMessage> _commandQueue;
    
    public bool Connect(string socketPath) {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Connect(endpoint);
        // Start receive thread...
    }
    
    // Call from main thread only
    public void ProcessCommands() {
        while (_commandQueue.TryDequeue(out var msg)) {
            // Handle message
        }
    }
}
```

---

## Phase 2: RimWorld Adapter

### Mod Entry Point

```csharp
[StaticConstructorOnStartup]
public static class GameRLMod {
    private static Bridge _bridge;
    private static uint _ticksRemaining;
    
    static GameRLMod() {
        var harmony = new Harmony("arkavo.gamerl.rimworld");
        harmony.PatchAll();
        
        _bridge = new Bridge(...);
        _bridge.Connect(GetSocketPath());
        _bridge.OnActionReceived += HandleAction;
    }
    
    internal static void OnTick() {
        _bridge.ProcessCommands();
        
        if (_ticksRemaining > 0 && --_ticksRemaining == 0) {
            // Send observation back to Rust
            _bridge.SendStepComplete(...);
        }
    }
}
```

### Critical Patches

```csharp
// Hook game loop
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
public static class TickPatch {
    static void Postfix() => GameRLMod.OnTick();
}

// Deterministic seeding
[HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
public static class RngPatch {
    internal static ulong? PendingSeed;
    static void Prefix(ref int value) {
        if (PendingSeed.HasValue) {
            value = (int)(PendingSeed.Value & 0x7FFFFFFF);
            PendingSeed = null;
        }
    }
}
```

### State Extraction

```csharp
public class RimWorldStateExtractor : IStateExtractor {
    public byte[] ExtractState(string agentId) {
        var map = Find.CurrentMap;
        var obs = new RimWorldObservation {
            Colonists = ColonistExtractor.Extract(map),
            Resources = ResourceExtractor.Extract(map),
            // ...
        };
        return MessagePackSerializer.Serialize(obs);
    }
}
```

### Action Execution

```csharp
public class RimWorldCommandExecutor : ICommandExecutor {
    public void Execute(string agentId, byte[] actionData) {
        var action = MessagePackSerializer.Deserialize<RimWorldAction>(actionData);
        switch (action.Type) {
            case "set_work_priority": /* ... */ break;
            case "draft": /* ... */ break;
            case "move": /* ... */ break;
        }
    }
}
```

---

## Phase 3: Integration

### Start Sequence

```bash
# 1. Rust server
cargo run --bin harmony-server -- --game rimworld

# 2. Launch RimWorld (connects automatically)

# 3. Test agent
python agent/examples/test_rimworld.py
```

### Test Cases

1. Agent registration
2. Reset with seed
3. Step with wait action
4. Step with draft action
5. Determinism (same seed → same hash)
6. Episode termination

---

## Key Rules

### Threading

- Harmony patches run on Unity main thread
- IPC receive runs on background thread
- **Never call Unity APIs from background thread**
- Queue commands, process in OnTick()

### Reward Synchronization

**CRITICAL**: Rewards must be in the same StepComplete message.

```
Agent sends: ExecuteAction
Game does:   Execute → Tick → Extract → Compute reward
Game sends:  StepComplete with observation AND reward
```

### Wire Protocol

```
[4 bytes: length (big-endian)] [N bytes: MessagePack]
```

---

## Multi-instance isolation

Every runtime path is per-instance. There is **no global fallback**: the mod
refuses to start without `GAMERL_INSTANCE_ID`, because a shared path does not
fail loudly when two colonies run side by side. The second instance attaches to
the first one's socket or framebuffer, and the corruption only shows up later as
unexplainable divergence.

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `GAMERL_INSTANCE_ID` | yes | none | Identity of this game process |
| `GAMERL_SOCKET` | no | `{TMP}/gamerl-rimworld-{id}.sock` | Full socket override |
| `GAMERL_CAMERA_SHM` | no | `{TMP}/gamerl-rimworld-camera-{id}-{stream}.shm` | Full vision override |
| `GAMERL_LOG_DIR` | no | `{TMP}/gamerl-logs/{id}` | Per-instance log directory |

`GAMERL_INSTANCE_ID` accepts at most 64 characters from letters, digits, `-` and
`_`. Anything else is rejected rather than sanitised, so a typo fails loudly
instead of collapsing two instances onto one id.

The vision framebuffer is opened with `FileMode.CreateNew`, so a second writer is
refused with an `IOException` instead of silently overwriting the first
instance's stream.

### Launching the server against one instance

`game-rl-server` refuses to guess when several instances are live:

```bash
GAMERL_INSTANCE_ID=alpha cargo run -p game-rl-server
GAMERL_INSTANCE_ID=beta  cargo run -p game-rl-server

# or, explicitly
cargo run -p game-rl-server -- --socket "$TMP/gamerl-rimworld-alpha.sock"
```

If exactly one instance is live and no id is given, that one is used. If two or
more are live the server exits with the list of candidates rather than picking by
modification time.

### RimWorld's own log

RimWorld writes `Player.log` through Unity, and a mod cannot redirect it. Launch
the game with `-logFile` pointing into the instance directory:

```bash
RimWorldLinux -logFile "${TMP}/gamerl-logs/alpha/Player.log"
```

Without this, two instances interleave into one log and post-mortem diagnosis of a
failed episode becomes unreliable. This is the one isolation item the mod cannot
enforce on its own.

---

## File Paths

### macOS
```
RimWorld: ~/Library/Application Support/Steam/steamapps/common/RimWorld/
Mods:     .../RimWorldMac.app/Mods/
Managed:  .../RimWorldMac.app/Contents/Resources/Data/Managed/
Logs:     ~/Library/Logs/Ludeon Studios/RimWorld/Player.log  (see -logFile above)
```

### Linux
```
RimWorld: ~/.local/share/Steam/steamapps/common/RimWorld/
Mods:     .../RimWorld/Mods/
Managed:  .../RimWorldLinux_Data/Managed/
Logs:     ~/Logs/Ludeon Studios/RimWorld/Player.log  (see -logFile above)
```

RimWorld has no native Linux build. Under Proton/Wine the IPC socket is a Unix
socket from the .NET side, so verify that `UnixDomainSocketEndPoint` binds and
accepts under that runtime before trusting any benchmark. Do not assume it works
and do not assume it fails.

---

## The game must already be in the Play scene

This is the single most expensive trap in this integration, and it is not a code
bug. It cost a full debugging session to find, and it looks like three different
bugs depending on what you read first.

**A reset loads a save. Loading a save from the main menu throws.** Observed:

```
System.InvalidCastException: Specified cast is not valid.
  at Verse.Find.get_MapUI ()
  at Verse.Game.set_CurrentMap (Verse.Map value)
  at Verse.Game.LoadGame ()
```

`Verse.Find.MapUI` is `((UIRoot_Play)UIRoot).mapUI` — an unchecked cast. And
`Verse.Root.Start` only ever creates a `UIRoot_Play`:

```csharp
if (GenScene.InPlayScene)        uiRoot = new UIRoot_Play();
else if (GenScene.InEntryScene)  uiRoot = new UIRoot_Entry();
```

`GenScene.InPlayScene` is `SceneManager.GetActiveScene().name == "Play"`, so the
scene is fixed when the process boots. **A RimWorld sitting at the main menu has
no map UI, and vanilla has no path into one.**

**A reset cannot fix this itself.** The scene is only ever loaded from
`LongEventHandler.LongEventsUpdate`, which `Root.Update` pumps. So
`QueueLongEvent(..., levelToLoad: "Play", ...)` does work — but the scene changes
on a *later frame*, while a reset reads its five postconditions in the frame it
was called. Those two contracts cannot both hold. Attempting it anyway was tried
and reverted: it destroys the current `Root` in the middle of the command.

**So the environment has to boot into Play.** That is what vanilla's own
autostart mechanism is for, and setting it up is two files:

```bash
SAVES="/home/autcir/Games/RimWorld/prefix/drive_c/users/autcir/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Saves"
CONFIG="/home/autcir/Games/RimWorld/prefix/drive_c/users/autcir/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config"

# 1. a save named exactly "autostart", which is what Verse.SaveGameFilesUtility
#    .GetAutostartSaveFile() looks for (case-insensitive filename match)
cp "$SAVES/Nuovi arrivati7.rws" "$SAVES/autostart.rws"

# 2. dev mode, or GetAutostartSaveFile returns null before it even looks
sed -i 's|<devMode>False</devMode>|<devMode>True</devMode>|' "$CONFIG/Prefs.xml"
```

Then `Verse.Root_Play.Start` finds the autostart save and loads it through
`LongEventHandler.QueueLongEvent` — vanilla's own path, with a `UIRoot_Play`
already present. Verified: the log shows

```
Loading game from file autostart with mods:
  at Verse.SavedGameLoaderNow:LoadGameFromSaveFileNow
  at Verse.Root_Play/<>c:<Start>b__1_1 ()
  at Verse.LongEventHandler:RunEventFromAnotherThread
```

and every reset then reports `frame:root_play` instead of `frame:root_entry`.

**The tell, once you know it.** A `[GameRL] ResetMessage handled by the
frame:root_entry pump` line means the game is at the menu and the reset cannot
work. `frame:root_play` means it can. That one word in the log is the whole
diagnosis.

`GAMERL_RESET_TEMPLATE` also has to be in the **game's** environment, not just the
harness's — see the launcher entry in `File Paths` below. The save name must
match the filename exactly, without `.rws`: it is `Nuovi arrivati7`, not
`Nuovi arrivanti7`.

---

## Inspecting vanilla RimWorld

Do not guess at vanilla behaviour, and do not conclude that it is unknowable.

`Krafs.Rimworld.Ref` — the package the mod compiles against — ships every method
body stripped at RVA 0. Reading that assembly tells you which members exist and
nothing about what they do. It is easy to read that as "this cannot be verified
offline" and then ship a guess. It is a property of the *reference package*, not
of the game.

The implementation is on disk, next to the executable:

```
RimWorldWin64_Data/Managed/Assembly-CSharp.dll        # full bodies, 1.6.4850 rev646
```

Decompile it and read the real thing:

```bash
dotnet tool install -g ilspycmd          # once
export PATH="$PATH:$HOME/.dotnet/tools"

ASM="<RimWorld>/RimWorldWin64_Data/Managed/Assembly-CSharp.dll"

# one type
ilspycmd -t Verse.Game "$ASM"

# the whole assembly into a directory you can grep (~9k files, ~30s, ~50MB)
ilspycmd -p -o /tmp/rw-asm "$ASM"
grep -rn "ExecuteWhenFinished" /tmp/rw-asm
```

`/tmp` does not survive a reboot; regenerate it rather than trusting a stale
copy, and check the version line the game itself logs (`RimWorld 1.6.4850
rev646` appears near the top of `Player.log`) against the reference package
version in the csproj. At the time of writing they did not match
(`Krafs.Rimworld.Ref` 1.6.4633 vs the running 1.6.4850), which is exactly the
kind of skew that produces "the API does not exist" surprises.

---

## Load ordering: the `ExecuteWhenFinished` rule

`Verse.LongEventHandler.ExecuteWhenFinished` runs its delegate **immediately and
synchronously** whenever no long event is displayed:

```csharp
public static void ExecuteWhenFinished(Action action)
{
    toExecuteWhenFinished.Add(action);
    if ((currentEvent == null || currentEvent.ShouldWaitUntilDisplayed)
        && !executingToExecuteWhenFinished)
        ExecuteToExecuteWhenFinished();
}
```

Vanilla only ever calls `SavedGameLoaderNow.LoadGameFromSaveFileNow` from
`Verse/Root_Play.Start`, and both call sites wrap it in
`LongEventHandler.QueueLongEvent(..., doAsynchronously: true, ...)`. That wrapper
is load-bearing, because `Verse.Game.LoadGame` assigns `CurrentMap` *before* it
calls `Scribe.loader.FinalizeLoading()`, which is where cross-references are
resolved:

```
Verse/Game.cs            CurrentMap setter -> AmbientSoundManager.Notify_SwitchedMap()
                         -> LongEventHandler.ExecuteWhenFinished(RecreateMapSustainers)
Verse/MapInfo.cs         Tile => parent?.Tile ?? PlanetTile.Invalid
Verse/Game.cs (LoadGame) currentMapIndex = sbyte.MinValue; CurrentMap = maps[value];
                         ... then ... Scribe.loader.FinalizeLoading();
RimWorld.Planet/         PlanetTile.Invalid = new PlanetTile(-1)
WorldGrid.cs             this[tile] => tile.Layer.Tiles[tile.tileId]   // raw List index
```

Called bare, `RecreateMapSustainers` reads `Map.Biome` while `MapInfo.parent` is
still an unresolved cross-reference, `Tile` is `PlanetTile.Invalid`, and the
lookup indexes `-1`. `List<T>.get_Item` throws
`ArgumentOutOfRangeException` — from inside vanilla code that has no idea a mod
is involved.

The exception is **not fatal**. `ExecuteToExecuteWhenFinished` wraps each
delegate in `try/catch` and logs `"Could not execute post-long-event action"`, so
the load completes and the game keeps running. Symptom seen in the wild: one such
line per reset, every reset, hundreds of log lines of healthy play afterwards.

The fix is to restore vanilla's ordering without giving up synchronous
completion. `ExecuteWhenFinished` with no long event in flight runs inline, which
is both what keeps `Reset` blocking and what sets `executingToExecuteWhenFinished`
— the flag that defers the load's own nested `ExecuteWhenFinished` call until
after `FinalizeLoading`:

```csharp
LongEventHandler.ExecuteWhenFinished(
    () => SavedGameLoaderNow.LoadGameFromSaveFileNow(template));
```

Do **not** reach for `QueueLongEvent` to fix this. It completes on a later frame,
so `Reset` would return before the world was in place and the next action would
run against a world still being replaced. And queueing from inside a tick or
frame postfix then blocking would deadlock, since a synchronous queued event is
pumped by `LongEventHandler.LongEventsUpdate` from that same `Root.Update`.

---

## Vanilla new-game order (what Mode=Fresh must follow)

Transcribed from `Assembly-CSharp.dll` 1.6.4850. `Verse.Root_Play.SetupForQuickTestPlay`
plus the third branch of `Verse.Root_Play.Start` is the whole path:

```csharp
Current.ProgramState = ProgramState.Entry;
Game.ClearCaches();
Current.Game = new Game();
Current.Game.InitData = new GameInitData();
Current.Game.Scenario = <ScenarioDef.scenario>;   // BEFORE GenerateWorld
Find.Scenario.PreConfigure();
Current.Game.storyteller = new Storyteller(def, difficultyDef);
Current.Game.World = WorldGenerator.GenerateWorld(coverage, seedString, ...);
Find.GameInitData.ChooseRandomStartingTile();
Find.GameInitData.mapSize = GameInitData.DefaultMapSize;
Find.Scenario.PostIdeoChosen();                   // when Ideology is active

Find.GameInitData.PrepForMapGen();
Find.Scenario.PreMapGenerate();                   // creates the Settlement
Current.Game.InitNewGame();                       // generates the map
```

Requirements that are easy to get wrong, each with the code that imposes it:

- **`Scenario` must be set before `GenerateWorld`.** `Find.Scenario` returns
  `Current.Game.Scenario`, and `GenerateWorld` needs it twice: planet layers are
  built from `Scenario.AllParts` by `WorldGrid.CreateRequiredLayers`, and
  `GenerateWorld` itself ends by calling `Find.Scenario.PostWorldGenerate`.
- **`GenerateWorld` already calls `PostWorldGenerate`.** Calling it again runs
  `ScenPart_PlayerFaction.PostWorldGenerate` twice and creates two player
  factions.
- **`GenerateWorld` nulls `Current.CreatingWorld` in its `finally`,** and returns
  the world. Do not assign `Current.CreatingWorld` afterwards, and do not read
  `Find.World` between the return and assigning `Current.Game.World`: with
  `CreatingWorld` already null and `Game.World` still null, `Find.World` is null
  and so is `FactionManager` under it.
- **`GenerateWorld` calls `PostIdeoChosen` only when Ideology is _absent_**, so
  with the DLC active that is the caller's step.
- **`InitNewGame` does not create the Settlement, it requires one.** It scans
  `Find.WorldObjects.Settlements` for the first `Settlement` whose `Faction` is
  `Faction.OfPlayer`, logs `"Could not generate starting map because there is no
  any player faction base"` when it finds none, and then dereferences the null
  anyway. The Settlement is created by `Scenario.PreMapGenerate`, which reaches
  `ScenPart_PlayerFaction.PreMapGenerate` — `Scenario.AllParts` yields
  `playerFaction` ahead of `surfaceLayer` and the ordinary parts, so every stock
  scenario has one whether or not its def lists it.
- **`InitNewGame` generates the map itself,** via
  `MapGenerator.GenerateMap(new IntVec3(initData.mapSize, 1, initData.mapSize),
  settlement, ...)`, and `GenerateMap` calls `Current.Game.AddMap(map)` before
  generating contents. Generating one yourself leaves two maps.
- **Odyssey widens the planet.** `WorldGenerator.DefaultPlanetCoverage` is `0.3`
  and `DefaultPlanetCoverageOdyssey` is `0.5`; use the latter when
  `ModsConfig.OdysseyActive`. `GenerateWorld` is called with a `seedString`, and
  everything derives from `GenText.StableStringHash(seedString)` — which is also
  what `WorldInfo.Seed` reads back, so it is the one place a generated world's
  seed can be observed rather than assumed.
- **`InitNewGame` requires `maps` to be empty** and `InitData` to be non-null,
  and it calls `FinalizeInit()`, which sets `ProgramState = Playing`.

---

## Two traps in this codebase that cost a debugging session

**`IntVec3` is `(x, height, z)`.** A map's height is always 1, so
`map.info.Size.x` by `map.info.Size.y` prints `250x1` for a perfectly normal
250x250 map. Use `.x` and `.z`. The save XML makes this explicit:
`<size>(250, 1, 250)</size>`.

**Def names are not labels.** The stock `ScenarioDef` names in this install are
`Crashlanded`, `Tutorial`, `LostTribe`, `TheRichExplorer`, `NakedBrutality`,
`TheAnomaly`, `Mechanitor`, `Sanguophage`, `TheGravship` — there is no
`Crashland`. A default that names nothing is a silent gate:
`DefDatabase.GetNamedSilentFail` returns null, and every probe that depends on it
fails in a way that looks like a broken probe rather than a typo.

---

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| NullReferenceException | Unity call from background thread | Queue + process in OnTick |
| Connection refused | Server not running | Start harmony-server first |
| Mod not loading | Missing Harmony | Subscribe in Steam Workshop |
| Non-deterministic | Time-based RNG | Audit all Rand usage |
| `ArgumentOutOfRangeException` in `WorldGrid.get_Item` from `LongEventHandler` | Saved game loaded outside a long event, so `MapInfo.parent` is still unresolved | Wrap the load in `LongEventHandler.ExecuteWhenFinished` (see above) |
| `Log.Error` in vanilla, action silently does nothing | Mod called vanilla API outside the long event vanilla would have used | Find vanilla's own call site and match it |
| API missing at compile time | csproj references `Krafs.Rimworld.Ref`, version-skewed from the running build | Check the version in `Player.log`; decompile the real `Assembly-CSharp.dll` |
