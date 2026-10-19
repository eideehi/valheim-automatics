#!/usr/bin/env bash
#
# Build Automatics and replace the copy installed in the game.
#
# Usage:
#   scripts/deploy.sh                  # Debug build plus the smoke plugin (default)
#   scripts/deploy.sh Debug no-smoke   # Debug build only; removes the smoke plugin
#   scripts/deploy.sh Release          # Release build; removes the smoke plugin
#
# Every run makes a clean build first. Only after the build succeeds does it
# empty <Valheim>/BepInEx/plugins/Automatics and AutomaticsSmoke and copy the
# new files in, so no file from an earlier build or the other configuration is
# left behind, and a failed build leaves the installed copy as it was. Close
# the game before running it.
#
# Environment:
#   VALHEIM_DIR   As for scripts/build.sh.
#
set -euo pipefail

usage() {
    echo "usage: scripts/deploy.sh [Debug|Release] [no-smoke]" >&2
}

if [[ $# -gt 2 ]]; then
    echo "error: too many arguments" >&2
    usage
    exit 1
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

# The smoke plugin is developer tooling, so only a Debug deploy installs it.
smoke="no"
case "${2:-}" in
    "")
        if [[ "$config" == "Debug" ]]; then
            smoke="yes"
        fi
        ;;
    no-smoke) ;;
    *)
        echo "error: second argument must be no-smoke when provided (got: $2)" >&2
        usage
        exit 1
        ;;
esac

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"

# shellcheck source=lib/valheim-dir.sh
source "$script_dir/lib/valheim-dir.sh"
resolve_valheim_dir

# The running game holds Automatics.dll open, so a deploy would fail part way.
game_is_running() {
    if command -v tasklist.exe &>/dev/null; then
        tasklist.exe /FI "IMAGENAME eq valheim.exe" /FO CSV /NH 2>/dev/null | grep -qi '"valheim.exe"'
    elif command -v pgrep &>/dev/null; then
        pgrep -xi 'valheim(\.x86_64)?' &>/dev/null
    else
        return 1
    fi
}

if game_is_running; then
    echo "error: Valheim is running. Close the game and re-run." >&2
    exit 1
fi

# Packaging (when SEVENZIP_PATH is set) moves the built files out of
# bin/Release into the archives, so build without it. scripts/build.sh Release
# still makes the release packages.
# Keep the installed files intact until both builds have succeeded.
env -u SEVENZIP_PATH DeployMod=false "$script_dir/build.sh" "$config" clean
if [[ "$smoke" == "yes" ]]; then
    "$script_dir/build.sh" smoke "$config" clean no-deploy
fi

output_dir="$repo_root/Automatics/bin/$config"
smoke_output_dir="$repo_root/smoke/Automatics.SmokeTest/bin/$config"
plugins_dir="$VALHEIM_DIR/BepInEx/plugins"
mod_dir="$plugins_dir/Automatics"
smoke_dir="$plugins_dir/AutomaticsSmoke"

# The same files that the DeployModFiles and PackageModFiles targets in
# Automatics/Automatics.csproj install. A Release build has LitJSON and
# NDesk.Options merged into Automatics.dll.
mod_files=(Automatics.dll)
for dll in LitJSON.dll NDesk.Options.dll; do
    if [[ -f "$output_dir/$dll" ]]; then
        mod_files+=("$dll")
    fi
done
while IFS= read -r -d '' file; do
    mod_files+=("${file#./}")
done < <(cd -- "$output_dir" && find . -type f -name '*.json' -print0 | sort -z)

# The same files that the SmokeDeploy target in
# smoke/Automatics.SmokeTest/Automatics.SmokeTest.csproj installs.
smoke_files=()
if [[ "$smoke" == "yes" ]]; then
    smoke_files+=(Automatics.SmokeTest.dll)
    if [[ -f "$smoke_output_dir/Automatics.SmokeTest.pdb" ]]; then
        smoke_files+=(Automatics.SmokeTest.pdb)
    fi
fi

rm -rf -- "$mod_dir" "$smoke_dir"

for file in "${mod_files[@]}"; do
    mkdir -p -- "$(dirname -- "$mod_dir/$file")"
    cp -- "$output_dir/$file" "$mod_dir/$file"
done

if (( ${#smoke_files[@]} > 0 )); then
    mkdir -p -- "$smoke_dir"
    for file in "${smoke_files[@]}"; do
        cp -- "$smoke_output_dir/$file" "$smoke_dir/$file"
    done
fi

echo
echo "Deployed the $config build to $mod_dir:"
printf '  %s\n' "${mod_files[@]}"
if (( ${#smoke_files[@]} > 0 )); then
    echo "Deployed the smoke plugin to $smoke_dir:"
    printf '  %s\n' "${smoke_files[@]}"
else
    echo "Removed the smoke plugin from $smoke_dir."
fi
