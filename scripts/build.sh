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

# shellcheck source=lib/valheim-dir.sh
source "$script_dir/lib/valheim-dir.sh"
resolve_valheim_dir

managed_dir="$VALHEIM_DIR/valheim_Data/Managed"

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
elif [[ "$config" == "Debug" && "${DeployMod:-}" != [Ff][Aa][Ll][Ss][Ee] ]]; then
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
