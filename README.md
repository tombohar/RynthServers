# RynthServers

Plugins for particular Asheron's Call emulator servers, for [RynthCore](https://aelrynth.com/rynth.html). Each one loads only on its own server and is installed from the launcher's **Server plugins** tab.

| Plugin | Server |
|---|---|
| RynthILT | Infinite Leaftide (pets, Vaulted Dungeons, T11, banking, Dynamic Stamps, ...) |
| RynthThreads | DreamWeave's Threads |
| RynthAelrynth, RynthConquest, RynthLevistras | their server's card, quest pack and own pages |
| RynthAChard, RynthColdeve, RynthDerptide, RynthDreamWeave, RynthEversong, RynthFrostACE, RynthFrostcull, RynthLeafDawn, RynthLeafdawning, RynthPortalStorm, RynthReefcull, RynthSundering, RynthThistlecrown | their server's card in the Mini Remote |

The shared server-plugin build is `Plugins/Shared/ServerPack`.

**RynthILT** is maintained by SilentKelpie: changes to it go through him.

## Building

RynthServers builds beside its two sibling repositories, which it compiles against:

```
<folder>\RynthCore      engine and plugin SDK
<folder>\RynthSuite     RynthAi and the other plugins; its Plugins\Shared holds the contracts server plugins use
<folder>\RynthServers   this repository
```

`dotnet build Plugins\RynthCore.Plugin.<Name>\RynthCore.Plugin.<Name>.csproj -c Release`. Tests: `Tools\RynthILT.Tests`, `Tools\RynthServerPack.Tests`, `Tools\RynthThreads.Tests` (`dotnet run -c Release`, exit 0 = pass).

The history of these folders before 2026-10-21 is RynthSuite's.
