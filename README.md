# MonMP

MonMP adds co-op multiplayer to **Mon Bazou** on Windows. Load a save, start hosting from the pause menu, and invite a friend through Steam. Direct IP and LAN hosting are also available. The host owns the shared save.

This repository contains the **0.9.110** player mod, network protocol **93**, by Kenward. Players joining the same session should use matching builds. [Download MonMP on Nexus Mods](https://www.nexusmods.com/monbazou/mods/536)

## Building from source

Use a Windows machine with the .NET SDK and a local copy of Mon Bazou. The build reads game references from your installation and imports the appearance menu artwork from a local MonMP release. [Build instructions](BUILDING.md)

## Repository layout

- `src/MonMP.Core` contains the multiplayer protocol, transports, session management and shared world state.
- `src/MonMP.Plugin` contains the BepInEx plugin, Unity integration, gameplay synchronization and multiplayer menus.
- `tests` contains standalone checks for transport behavior, save recovery and maple wine keg messages. They can run without the game.

## License

MonMP code is licensed under GPL-3.0-or-later with an additional linking permission. Game artwork retains its original ownership. [License](LICENSE) · [Linking permission](ADDITIONAL-PERMISSION.txt) · [Artwork notice](APPEARANCE-PREVIEWS.txt)
