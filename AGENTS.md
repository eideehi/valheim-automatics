# Build Notes

## Project layout
- Solution: `Automatics.sln`
- Project: `Automatics/Automatics.csproj`
- Target framework: `.NET Framework 4.8`

## Repository notes

- `Automatics/Libraries/mod-utils` is a read-only library dependency. Do not edit files under this directory unless the task is explicitly to update the submodule.
- When preparing a release, create the Git tag that matches the release/package version so versioned README and Thunderstore links resolve correctly.
- Every release commit must update `distributor/thunderstore/manifest.json`. Never release with it unchanged. Set `version_number` to the release version, and set each dependency to the version the release was built and checked against. For `denikson-BepInExPack_Valheim` that is the latest version on Thunderstore; if the local BepInEx install is older, update it first. `scripts/check-version.sh` checks only `version_number`, so check the dependencies by hand.
- Check behaviour in game with the smoke plugin wherever possible, and keep what a person has to check by hand to a minimum. When a change can be observed in game, add or extend a smoke check for it (see `smoke/README.md`) instead of writing manual steps, and ask a person to check only what a smoke check cannot observe, such as visuals or a real multiplayer session.
- `docs/` holds end-user-facing documentation, which is tracked. Local-only working documents live in fixed subdirectories of `docs/` that are gitignored and never committed: `docs/specs/` (requirements and specifications), `docs/plans/` (implementation plans), `docs/reports/` (audits, research notes and findings; deferred findings go in `docs/reports/findings/`) and `docs/decisions/` (decision records). Do not put other non-end-user documents directly under `docs/`, and do not create `plans/`, `specs/` or `.plans/` at the repository root.

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

In-game smoke test plugin (optional developer tooling; see `smoke/README.md`).
Build the main plugin first, then build and deploy the smoke plugin to
`<Valheim>/BepInEx/plugins/AutomaticsSmoke`:

```bash
scripts/build.sh Debug
scripts/build.sh smoke Debug [clean] [deploy|no-deploy]
```

Besides the auto-run startup invariant checks, the smoke plugin registers four optional in-game console commands: `automatics_smoke_world [storage]` drives the live Storage, Door, Pickup and Processing entry points against spawned objects; `automatics_smoke_lifecycle [all|<module>]` turns a module off and on at runtime to check its patches and components (`mapping`, `door`, `farming`, `feeding`, `mining`, `repair`, `storage`, `supply`, `woodcutting`; no argument checks Automatic Pickup; `all` runs the no-argument check and then every module, one after another); `automatics_smoke_processing all|<subcommand>` checks Automatic Processing (`rpc`, `toggle`, `config`, `skill`, `store`, `collect`; `all` runs each in turn); `automatics_smoke_features all|<section>` drives one feature per section (`pickup`, `pin`, `navhotkey`, `aimbar`, `mapfilter`, `pintooltip`, `mining`, `planting`, `farming`, `guide`, `compass`, `supply`, `woodcutting`, `woodcuttingarea`, `repair`). A command that changes settings or spawns objects records what it changed and hands it back, and only one smoke command runs at a time. `smoke/README.md` describes each command's checks, preconditions and temporary setting changes; document a new or changed smoke check there, not here.

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

## Deploying to the game
`scripts/deploy.sh` makes a clean build, then empties `<Valheim>/BepInEx/plugins/Automatics` and `AutomaticsSmoke` and copies the new build in, so no file from an earlier build or the other configuration stays behind. Close the game first; the script stops when Valheim is running.

```bash
scripts/deploy.sh                  # Debug build plus the smoke plugin
scripts/deploy.sh Debug no-smoke   # Debug build only; removes the smoke plugin
scripts/deploy.sh Release          # Release build; removes the smoke plugin
```

A Release deploy builds without `SEVENZIP_PATH`, so it makes no release packages; run `scripts/build.sh Release` for those.

Debug builds through `scripts/build.sh` or direct MSBuild also trigger `DeployModFiles` when `VALHEIM_DIR` points to a valid BepInEx Valheim install, but that only copies over the installed files and leaves the smoke plugin as it was. Release builds through `scripts/build.sh` never deploy, so a Release build alone leaves the game running an older plugin. Before asking anyone to check a change in game (by hand or with a smoke command), close the game, run `scripts/deploy.sh` (or `scripts/deploy.sh Release` to check the release build), and confirm that `<Valheim>/BepInEx/plugins/Automatics` (and `AutomaticsSmoke`) now holds that build, for example by its timestamp or by a name the change added.

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
