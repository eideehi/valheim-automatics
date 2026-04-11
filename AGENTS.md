# Build Instructions

This repository targets .NET Framework 4.8. Use `dotnet msbuild` rather than `dotnet build`.

## Prerequisites

- Install `dotnet-sdk-8.0` or newer.
- Point `VALHEIM_DIR` at the Valheim game directory.

## Windows Build

Set `VALHEIM_DIR` to the game install path, then build with `dotnet msbuild`.

```powershell
$env:VALHEIM_DIR = '<Valheim install path>'
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Debug /p:Platform="Any CPU"
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

Notes:

- A native Windows `Debug` build auto-deploys the mod into `BepInEx\plugins\Automatics` if the game path is valid.
- `Release` outputs are written to `Automatics/bin/Release`.

## WSL / Linux Build

When building under WSL or Linux, pass `FrameworkPathOverride` so MSBuild can use Valheim's managed framework assemblies.

```bash
DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
VALHEIM_DIR=/path/to/Valheim \
dotnet msbuild Automatics.sln /t:Build \
  /restore \
  /p:Configuration=Debug \
  /p:Platform="Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed

DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
VALHEIM_DIR=/path/to/Valheim \
dotnet msbuild Automatics.sln /t:Build \
  /restore \
  /p:Configuration=Release \
  /p:Platform="Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed
```

Notes:

- WSL builds do not auto-deploy into the game directory.
- Whenever you build `Automatics.dll`, deploy the freshly built DLL to `Valheim/BepInEx/plugins/Automatics` before finishing.
- If you want to install the Debug build manually, copy:
  - `Automatics.dll`
  - `LitJSON.dll`
  - `NDesk.Options.dll`
  - `Data/`
  - `Languages/`

into `Valheim/BepInEx/plugins/Automatics`.

- If you want to install the Release build manually, copy:
  - `Automatics.dll`
  - `Data/`
  - `Languages/`

into `Valheim/BepInEx/plugins/Automatics`. Release builds merge `NDesk.Options.dll` into `Automatics.dll` with ILRepack.

## Output Paths

- Debug: `Automatics/bin/Debug`
- Release: `Automatics/bin/Release`
- Installed mod path: `Valheim/BepInEx/plugins/Automatics`
