# Shared by scripts/build.sh and scripts/deploy.sh. Source this file; do not
# run it.

# Export VALHEIM_DIR and check that it holds the Valheim and BepInEx files the
# build needs. When the caller did not set VALHEIM_DIR, the default Steam install
# locations on WSL, Linux and macOS are probed. Exits on failure.
resolve_valheim_dir() {
    # Only platform defaults are probed; custom library folders (SteamLibrary on
    # a non-default drive, etc.) should be supplied via the VALHEIM_DIR
    # environment variable.
    if [[ -z "${VALHEIM_DIR:-}" ]]; then
        local candidates=(
            "/mnt/c/Program Files (x86)/Steam/steamapps/common/Valheim"
            "$HOME/.steam/steam/steamapps/common/Valheim"
            "$HOME/.local/share/Steam/steamapps/common/Valheim"
            "$HOME/Library/Application Support/Steam/steamapps/common/Valheim"
        )
        local path
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

    local required_files=(
        "$VALHEIM_DIR/valheim_Data/Managed/assembly_valheim.dll"
        "$VALHEIM_DIR/BepInEx/core/0Harmony.dll"
        "$VALHEIM_DIR/BepInEx/core/BepInEx.dll"
    )

    local missing_files=()
    local required_file
    for required_file in "${required_files[@]}"; do
        if [[ ! -f "$required_file" ]]; then
            missing_files+=("$required_file")
        fi
    done

    if (( ${#missing_files[@]} > 0 )); then
        echo "error: VALHEIM_DIR is missing required Valheim or BepInEx files." >&2
        local missing_file
        for missing_file in "${missing_files[@]}"; do
            echo "       Missing: $missing_file" >&2
        done
        echo "       Set VALHEIM_DIR=/path/to/Valheim with BepInEx installed and re-run." >&2
        exit 1
    fi
}
