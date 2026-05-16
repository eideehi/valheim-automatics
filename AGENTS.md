# Build Notes

## Project layout
- Solution: `Automatics.sln`
- Project: `Automatics/Automatics.csproj`
- Target framework: `.NET Framework 4.8`

## Repository notes

- `Automatics/Libraries/mod-utils` is a read-only library dependency. Do not edit files under this directory unless the task is explicitly to update the submodule.
- When preparing a release, create the Git tag that matches the release/package version so versioned README and Thunderstore links resolve correctly.

## Required local dependencies
- .NET SDK 8.0 or newer
- A local Valheim installation
- BepInEx installed inside the Valheim directory

The project resolves game references from the Valheim install directory. Set the following environment variable before building:

- `VALHEIM_DIR`: absolute path to the Valheim root directory that contains `valheim_Data/Managed`

The expected directory shape is:

- `<Valheim>/valheim_Data/Managed`
- `<Valheim>/BepInEx/core`

## Build commands
Use `scripts/build.sh` for normal local builds. It validates `VALHEIM_DIR` and BepInEx core files, passes `FrameworkPathOverride`, and runs `dotnet msbuild`.

Debug build:

```bash
scripts/build.sh
scripts/build.sh Debug
```

Release build:

```bash
scripts/build.sh Release
```

Clean before building:

```bash
scripts/build.sh Debug clean
```

Version consistency check:

```bash
scripts/check-version.sh
```

## Direct MSBuild fallback
Use direct MSBuild commands only when debugging the build script or project file.

Debug build:

```bash
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Debug "/p:Platform=Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed
```

Release build:

```bash
dotnet msbuild Automatics.sln /restore /t:Build /p:Configuration=Release "/p:Platform=Any CPU" \
  /p:FrameworkPathOverride=$VALHEIM_DIR/valheim_Data/Managed
```

## Output
- Debug output: `Automatics/bin/Debug/Automatics.dll`
- Release output without packaging: `Automatics/bin/Release/Automatics.dll`
- Release output with `SEVENZIP_PATH`: package archives under `Automatics/bin/Release`
- Installed mod path: `<Valheim>/BepInEx/plugins/Automatics`

## Debug build auto-deploy
Debug builds through `scripts/build.sh` or direct MSBuild trigger `DeployModFiles` when `VALHEIM_DIR` points to a valid BepInEx Valheim install.

## Release repack
Release builds use `ILRepack.Lib.MSBuild.Task` to merge `LitJSON` and `NDesk.Options` into the output assembly. If the ILRepack build targets are missing, the Release build fails.

## Release packaging
Set `SEVENZIP_PATH` to a `7z` executable to create release archives after ILRepack:

- `Automatics - <Version>.7z`: Nexus package with mod files in an `Automatics/` subfolder
- `Automatics - <Version>.zip`: Thunderstore package with `plugins/`, `icon.png`, `manifest.json`, `README.md`, and `CHANGELOG.md`

Thunderstore assets (`icon.png`, `manifest.json`, `README.md`) are copied from `distributor/thunderstore/`. `CHANGELOG.md` is copied into the Thunderstore package as a separate root file.

## Manual install
Copy the following into `<Valheim>/BepInEx/plugins/Automatics`:

- `Automatics.dll`
- `Data/`
- `Languages/`

## Troubleshooting
- If assembly references fail, verify that the chosen Valheim directory really contains `valheim_Data/Managed`.
- If BepInEx references fail, verify that `BepInEx/core/0Harmony.dll` and `BepInEx/core/BepInEx.dll` exist under the same Valheim directory.
- If the Release build fails because ILRepack targets are missing, run restore before building Release.
