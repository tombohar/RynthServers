# Contributing to RynthServers

This repository holds the plugins for particular game servers. RynthCore is the hook layer and RynthSuite holds the shared plugins; a feature for one server lives here, in that server's plugin, and nowhere else.

## Where a change goes

| It is... | It goes in |
|---|---|
| A feature, page, command or data for one server | **That server's plugin here** (`Plugins/RynthCore.Plugin.Rynth<Server>`) |
| Something every server plugin needs (the card, pages, settings, chat replies) | **`Plugins/Shared/ServerPack`** |
| A place in a shared plugin for server plugins to fill (a tab slot, a data feed) | **RynthSuite**, as a generic hook that names no server |
| A hook into the client or a new plugin API function | **RynthCore** |

## Pull requests

- One purpose per pull request. A change that needs a new hook comes as a pull request to RynthCore or RynthSuite for the hook first, then one here for the feature.
- Plugin API numbers and plugin versions are assigned when a change is merged.
- Add or update tests (`Tools/`).
- Say in the description what you tested in game, on which server.
