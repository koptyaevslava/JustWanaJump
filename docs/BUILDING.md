# Building

Install BepInEx 6 for IL2CPP into Sea of Stars and run the game once so BepInEx generates its interop assemblies. Set `SEA_OF_STARS_DIR` to the game directory, then build:

```powershell
$env:SEA_OF_STARS_DIR = "D:\SteamLibrary\steamapps\common\Sea of Stars"
dotnet build src\CosmeticJump\CosmeticJump.csproj -c Release
```

You can also pass the path directly:

```powershell
dotnet build src\CosmeticJump\CosmeticJump.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Sea of Stars"
```

The build never copies files into the game directory. The resulting plug-in is written to `src/CosmeticJump/bin/Release/net6.0/CosmeticJump.dll`.

The project references only local BepInEx core and generated Sea of Stars interop assemblies. Those dependencies must not be committed or redistributed.
