#!/usr/bin/env bash
#
# Check version fields and released package documentation links.
#
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"

project_file="Automatics/Automatics.csproj"
plugin_file="Automatics/Automatics.cs"
manifest_file="distributor/thunderstore/manifest.json"
assembly_info="Automatics/Properties/AssemblyInfo.cs"
thunderstore_readme="distributor/thunderstore/README.md"
changelog_file="CHANGELOG.md"
failures=0

read_single_match() {
    local file="$1"
    local expression="$2"
    local label="$3"
    local value

    value="$(sed -nE "$expression" "$repo_root/$file")"
    if [[ -z "$value" ]]; then
        echo "error: could not read $label from $file" >&2
        exit 1
    fi

    if [[ "$(printf '%s\n' "$value" | wc -l)" -ne 1 ]]; then
        echo "error: found multiple $label values in $file" >&2
        exit 1
    fi

    printf '%s' "$value"
}

read_latest_release_tag() {
    local tag

    tag="$(git -C "$repo_root" tag --list '[0-9]*.[0-9]*.[0-9]*' --sort=version:refname | tail -n 1)"
    if [[ -z "$tag" ]]; then
        echo "error: could not read latest release tag" >&2
        exit 1
    fi

    printf '%s' "$tag"
}

check_thunderstore_links() {
    local found=0
    local entry
    local tag
    local path

    while IFS= read -r entry; do
        [[ -z "$entry" ]] && continue
        found=1

        tag="${entry%%/*}"
        path="${entry#*/}"

        if [[ "$tag" == "$entry" || -z "$path" ]]; then
            echo "error: could not parse Thunderstore README GitHub link '$entry'" >&2
            failures=1
            continue
        fi

        if [[ "$tag" != "$latest_release_tag" ]]; then
            echo "error: Thunderstore README link tag '$tag' does not match latest release tag $latest_release_tag" >&2
            failures=1
        fi

        if ! git -C "$repo_root" cat-file -e "$tag:$path" 2>/dev/null; then
            echo "error: Thunderstore README link path '$path' does not exist in release tag $tag" >&2
            failures=1
        fi
    done < <(
        {
            (grep -Eo 'github\.com/eideehi/valheim-automatics/blob/[^)[:space:]]+' "$repo_root/$thunderstore_readme" || true) \
                | sed -E 's|^.*blob/||'
            (grep -Eo 'raw\.githubusercontent\.com/eideehi/valheim-automatics/[^)[:space:]]+' "$repo_root/$thunderstore_readme" || true) \
                | sed -E 's|^.*valheim-automatics/||'
        } | sed -E 's/[?#].*$//' | sort -u
    )

    if (( found == 0 )); then
        echo "error: Thunderstore README does not contain versioned GitHub links" >&2
        failures=1
    fi
}

project_version="$(read_single_match "$project_file" 's/^[[:space:]]*<Version>([^<]+)<\/Version>[[:space:]]*$/\1/p' 'project version')"
plugin_version="$(read_single_match "$plugin_file" 's/^[[:space:]]*private const string ModVersion = "([^"]+)";[[:space:]]*$/\1/p' 'plugin version')"
manifest_version="$(read_single_match "$manifest_file" 's/^[[:space:]]*"version_number":[[:space:]]*"([^"]+)".*$/\1/p' 'Thunderstore manifest version')"
assembly_version="$(read_single_match "$assembly_info" 's/^\[assembly: AssemblyVersion\("([^"]+)"\)\]$/\1/p' 'assembly version')"
file_version="$(read_single_match "$assembly_info" 's/^\[assembly: AssemblyFileVersion\("([^"]+)"\)\]$/\1/p' 'assembly file version')"
changelog_version="$(read_single_match "$changelog_file" '1s/^#### v([^[:space:]]+) \[.*$/\1/p' 'latest changelog version')"

expected_assembly_version="$project_version.0"
latest_release_tag="$(read_latest_release_tag)"

if [[ "$plugin_version" != "$project_version" ]]; then
    echo "error: plugin version $plugin_version does not match project version $project_version" >&2
    exit 1
fi

if [[ "$manifest_version" != "$project_version" ]]; then
    echo "error: manifest version $manifest_version does not match project version $project_version" >&2
    exit 1
fi

if [[ "$assembly_version" != "$expected_assembly_version" ]]; then
    echo "error: assembly version $assembly_version does not match expected $expected_assembly_version" >&2
    exit 1
fi

if [[ "$file_version" != "$expected_assembly_version" ]]; then
    echo "error: assembly file version $file_version does not match expected $expected_assembly_version" >&2
    exit 1
fi

if [[ "$changelog_version" != "$latest_release_tag" ]]; then
    echo "error: changelog version $changelog_version does not match latest release tag $latest_release_tag" >&2
    exit 1
fi

if grep -Eq 'github\.com/eideehi/valheim-automatics/blob/(main|master)/|raw\.githubusercontent\.com/eideehi/valheim-automatics/(main|master)/' "$repo_root/$thunderstore_readme"; then
    echo "error: Thunderstore README contains main/master links instead of release tag links" >&2
    exit 1
fi

check_thunderstore_links

if (( failures > 0 )); then
    exit 1
fi

echo "Version metadata matches $project_version. Released documentation links use $latest_release_tag."
