#!/usr/bin/env bash
#
# Build the Automatics mod.
#
# Usage:
#   scripts/build.sh               # Debug build (default)
#   scripts/build.sh Release       # Release build (produces a distributable zip)
#   scripts/build.sh Debug clean   # Clean first, then Debug build
#   scripts/build.sh smoke Debug              # Build + deploy the in-game smoke plugin
#   scripts/build.sh smoke Debug no-deploy    # Build the smoke plugin without deploying
#
# Environment:
#   VALHEIM_DIR   Path to the Valheim install that contains
#                 valheim_Data/Managed/assembly_valheim.dll and
#                 BepInEx/core/{0Harmony.dll,BepInEx.dll}.
#                 If unset, the script checks common Steam locations on
#                 WSL, Linux, and macOS.
#
set -euo pipefail

usage() {
    echo "usage:" >&2
    echo "  scripts/build.sh [Debug|Release] [clean]" >&2
    echo "  scripts/build.sh smoke [Debug|Release] [clean] [deploy|no-deploy]" >&2
}

mode="library"
clean=""
deploy_arg=""

if [[ "${1:-}" == "smoke" ]]; then
    mode="smoke"
    shift
fi

config="${1:-Debug}"

case "$config" in
    Debug | Release) ;;
    *)
        echo "error: configuration must be Debug or Release (got: $config)" >&2
        usage
        exit 1
        ;;
esac

if [[ "$mode" == "smoke" ]]; then
    if [[ $# -gt 3 ]]; then
        echo "error: too many arguments for smoke mode" >&2
        usage
        exit 1
    fi
    # Parse the remaining args (in any order) into clean and deploy mode.
    for arg in "${@:2}"; do
        case "$arg" in
            clean) clean="clean" ;;
            deploy) deploy_arg="/p:DeploySmoke=true" ;;
            no-deploy) deploy_arg="/p:DeploySmoke=false" ;;
            *)
                echo "error: smoke argument must be clean, deploy, or no-deploy (got: $arg)" >&2
                usage
                exit 1
                ;;
        esac
    done
else
    clean="${2:-}"

    if [[ $# -gt 2 ]]; then
        echo "error: usage is scripts/build.sh [Debug|Release] [clean]" >&2
        exit 1
    fi

    case "$clean" in
        "" | clean) ;;
        *)
            echo "error: second argument must be clean when provided (got: $clean)" >&2
            exit 1
            ;;
    esac
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"

if [[ "$mode" == "smoke" ]]; then
    build_target="$repo_root/smoke/Automatics.SmokeTest/Automatics.SmokeTest.csproj"
    output_dll="$repo_root/smoke/Automatics.SmokeTest/bin/$config/Automatics.SmokeTest.dll"
else
    build_target="$repo_root/Automatics.sln"
    output_dll="$repo_root/Automatics/bin/$config/Automatics.dll"
fi

if [[ ! -f "$build_target" ]]; then
    echo "error: build target not found: $build_target" >&2
    exit 1
fi

# Auto-detect VALHEIM_DIR from default Steam install locations when the
# caller did not export it. Only platform defaults are probed; custom
# library folders (SteamLibrary on a non-default drive, etc.) should be
# supplied via the VALHEIM_DIR environment variable.
if [[ -z "${VALHEIM_DIR:-}" ]]; then
    candidates=(
        "/mnt/c/Program Files (x86)/Steam/steamapps/common/Valheim"
        "$HOME/.steam/steam/steamapps/common/Valheim"
        "$HOME/.local/share/Steam/steamapps/common/Valheim"
        "$HOME/Library/Application Support/Steam/steamapps/common/Valheim"
    )
    for path in "${candidates[@]}"; do
        if [[ -f "$path/valheim_Data/Managed/assembly_valheim.dll" ]]; then
            export VALHEIM_DIR="$path"
            echo "Auto-detected VALHEIM_DIR=$VALHEIM_DIR"
            break
        fi
    done
fi

if [[ -z "${VALHEIM_DIR:-}" ]]; then
    echo "error: VALHEIM_DIR is not set and no Valheim install was auto-detected." >&2
    echo "       Expected: \$VALHEIM_DIR/valheim_Data/Managed/assembly_valheim.dll" >&2
    echo "       Set VALHEIM_DIR=/path/to/Valheim and re-run." >&2
    exit 1
fi

managed_dir="$VALHEIM_DIR/valheim_Data/Managed"
bepinex_core_dir="$VALHEIM_DIR/BepInEx/core"

required_files=(
    "$managed_dir/assembly_valheim.dll"
    "$bepinex_core_dir/0Harmony.dll"
    "$bepinex_core_dir/BepInEx.dll"
)

missing_files=()
for required_file in "${required_files[@]}"; do
    if [[ ! -f "$required_file" ]]; then
        missing_files+=("$required_file")
    fi
done

if (( ${#missing_files[@]} > 0 )); then
    echo "error: VALHEIM_DIR is missing required Valheim or BepInEx files." >&2
    for missing_file in "${missing_files[@]}"; do
        echo "       Missing: $missing_file" >&2
    done
    echo "       Set VALHEIM_DIR=/path/to/Valheim with BepInEx installed and re-run." >&2
    exit 1
fi

output_dir="$(dirname -- "$output_dll")"
msbuild_args=(
    "$build_target"
    /restore
    /nologo
    "/p:Configuration=$config"
    "/p:Platform=Any CPU"
    "/p:FrameworkPathOverride=$managed_dir"
)

if [[ "$mode" == "smoke" && -n "$deploy_arg" ]]; then
    msbuild_args+=("$deploy_arg")
fi

if [[ "$clean" == "clean" ]]; then
    dotnet msbuild "${msbuild_args[@]}" /t:Clean
fi

dotnet msbuild "${msbuild_args[@]}" /t:Build

echo
if [[ -f "$output_dll" ]]; then
    echo "Build succeeded. Output: $output_dll"
else
    echo "Build succeeded. Output directory: $output_dir"
fi
if [[ "$mode" == "smoke" ]]; then
    # Mirror the csproj's DeploySmoke default: an explicit `deploy`, or Debug with
    # no deploy override. A plain `smoke Release` does not auto-deploy, so do not
    # claim it did.
    if [[ "$deploy_arg" == "/p:DeploySmoke=true" || ( -z "$deploy_arg" && "$config" == "Debug" ) ]]; then
        echo "Smoke deploy path: $VALHEIM_DIR/BepInEx/plugins/AutomaticsSmoke"
    fi
elif [[ "$config" == "Debug" ]]; then
    echo "Debug deploy target: $VALHEIM_DIR/BepInEx/plugins/Automatics"
elif [[ "$config" == "Release" && -e "${SEVENZIP_PATH:-}" ]]; then
    # Mirror the csproj's PackageModFiles condition. Without SEVENZIP_PATH no
    # package is made, and any archive here is left over from an earlier build.
    nexus_package=$(find "$output_dir" -maxdepth 1 -name 'Automatics - *.7z' -print -quit 2>/dev/null || true)
    thunderstore_package=$(find "$output_dir" -maxdepth 1 -name 'Automatics - *.zip' -print -quit 2>/dev/null || true)
    if [[ -n "$nexus_package" ]]; then
        echo "Nexus package:      $nexus_package"
    fi
    if [[ -n "$thunderstore_package" ]]; then
        echo "Thunderstore zip:   $thunderstore_package"
    fi
fi
