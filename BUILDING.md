# Build MonMP on Windows

Install the .NET SDK 8 or later and Mon Bazou v1.06. Use a terminal in the repository root. The first build needs internet access for the pinned BepInEx build references and locked NuGet dependencies.

```powershell
.\build.cmd -GameDirectory "C:\Games\Mon Bazou" -PreviewSource "C:\Downloads\MonMP-0.9.110-mod-only.zip"
```

Replace the example paths with your own. A matching MonMP release source ZIP or an extracted `MonMP.dll` also works. If MonMP is already installed under `BepInEx/plugins/MonMP` in the game folder, omit `-PreviewSource`. You can omit it on subsequent builds once the previews have been imported. The build script writes only in this checkout.

The source tree contains a manifest of the 628 appearance previews. The first build reads the image bytes from your local release, checks each SHA-256 hash against the manifest, and places the PNGs in a Git-ignored directory. A release with identical artwork works even if its mod version differs. Game and Unity assemblies come from the local game installation. The build checks the game assembly and BepInEx archive against their expected hashes.

The player build produces `MonMP.dll`, `MonMP.Core.dll` and `LiteNetLib.dll` under `src/MonMP.Plugin/bin/Release/netstandard2.1/`. To install manually, close the game and copy these three files into `BepInEx/plugins/MonMP/` in an existing BepInEx installation.

## Standalone tests

Run these from the repository root with the .NET 8 runtime and SDK installed. The transport checks use the local loopback interface.

```powershell
dotnet run -c Release --project tests/MonMP.TransportContract.Tests
dotnet run -c Release --project tests/MonMP.SaveCollectionRecovery.Tests
dotnet run -c Release --project tests/MonMP.KegProtocol.Tests
```
