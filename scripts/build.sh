#!/usr/bin/env bash
#
# Build the Automatics mod.
#
# Usage:
#   scripts/build.sh               # Debug build (default)
#   scripts/build.sh Release       # Release build (produces a distributable zip)
#   scripts/build.sh Debug clean   # Clean first, then Debug build
#
# Environment:
#   VALHEIM_DIR   Path to the Valheim install that contains
#                 valheim_Data/Managed/assembly_valheim.dll.
#                 If unset, the script tries a few common Steam locations
#                 for WSL / Linux / macOS.
#
set -euo pipefail

config="${1:-Debug}"
clean="${2:-}"

# Resolve repo root from the script location so the script works from any CWD.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"
project="$repo_root/Automatics/Automatics.csproj"

if [[ ! -f "$project" ]]; then
    echo "error: project file not found: $project" >&2
    exit 1
fi

case "$config" in
    Debug | Release) ;;
    *)
        echo "error: configuration must be Debug or Release (got: $config)" >&2
        exit 1
        ;;
esac

# Auto-detect VALHEIM_DIR from default Steam install locations when the
# caller did not export it. Only platform defaults are probed — custom
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

if [[ -z "${VALHEIM_DIR:-}" ]] \
    || [[ ! -f "$VALHEIM_DIR/valheim_Data/Managed/assembly_valheim.dll" ]]; then
    echo "error: VALHEIM_DIR is not set or does not point to a Valheim install." >&2
    echo "       Expected: \$VALHEIM_DIR/valheim_Data/Managed/assembly_valheim.dll" >&2
    echo "       Export VALHEIM_DIR=/path/to/Valheim and re-run." >&2
    exit 1
fi

if [[ "$clean" == "clean" ]]; then
    dotnet build "$project" -c "$config" --target:Clean -nologo
fi

dotnet build "$project" -c "$config" -nologo

output_dir="$repo_root/Automatics/bin/$config"
echo
echo "Build succeeded. Output: $output_dir/Automatics.dll"
if [[ "$config" == "Release" ]]; then
    zip=$(find "$output_dir" -maxdepth 1 -name 'Automatics*.zip' -print -quit 2>/dev/null || true)
    if [[ -n "$zip" ]]; then
        echo "Release zip:    $zip"
    fi
fi
