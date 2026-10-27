#!/usr/bin/env bash
#
# Regression tests for scripts/check-version.sh.
#
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
repo_root="$(cd -- "$script_dir/.." &>/dev/null && pwd)"
tmp_dir="$(mktemp -d)"

cleanup() {
    rm -rf "$tmp_dir"
}
trap cleanup EXIT

fixture="$tmp_dir/repo"
mkdir -p "$fixture/scripts" "$fixture/Automatics/Properties" "$fixture/distributor/thunderstore" "$fixture/docs"
cp "$repo_root/scripts/check-version.sh" "$fixture/scripts/check-version.sh"
chmod +x "$fixture/scripts/check-version.sh"

write_versions() {
    local version="$1"
    local assembly_version="$2"

    cat >"$fixture/Automatics/Automatics.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>$version</Version>
  </PropertyGroup>
</Project>
EOF

    cat >"$fixture/Automatics/Automatics.cs" <<EOF
public static class UnityPlugin
{
    private const string ModVersion = "$version";
}
EOF

    cat >"$fixture/distributor/thunderstore/manifest.json" <<EOF
{
  "version_number": "$version"
}
EOF

    cat >"$fixture/Automatics/Properties/AssemblyInfo.cs" <<EOF
[assembly: AssemblyVersion("$assembly_version")]
[assembly: AssemblyFileVersion("$assembly_version")]
EOF
}

write_versions "1.6.0" "1.6.0.0"

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### v1.6.0 [2026-05-17]
- Release notes.
EOF

cat >"$fixture/distributor/thunderstore/README.md" <<'EOF'
See [the user guide](https://github.com/eideehi/valheim-automatics/blob/1.6.0/docs/user-guide.md).
EOF

cat >"$fixture/docs/user-guide.md" <<'EOF'
# User Guide
EOF

git -C "$fixture" init -q
git -C "$fixture" config user.email "automatics-tests@example.invalid"
git -C "$fixture" config user.name "Automatics Tests"
git -C "$fixture" add .
git -C "$fixture" commit -q -m "fixture release"
git -C "$fixture" tag 1.6.0

"$fixture/scripts/check-version.sh" >/dev/null

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### Unreleased
- Pending changes.

#### v1.6.0 [2026-05-17]
- Release notes.

#### v1.5.1 [2023-10-15]
- Older release notes.
EOF

"$fixture/scripts/check-version.sh" >/dev/null

write_versions "1.6.1" "1.6.1.0"
if "$fixture/scripts/check-version.sh" >"$tmp_dir/stale-changelog.out" 2>"$tmp_dir/stale-changelog.err"; then
    echo "error: check-version passed with package metadata ahead of changelog and latest tag" >&2
    exit 1
fi

if ! grep -Fq 'project version 1.6.1 does not match latest changelog version 1.6.0' "$tmp_dir/stale-changelog.err"; then
    echo "error: check-version failed for the wrong stale-changelog reason" >&2
    cat "$tmp_dir/stale-changelog.err" >&2
    exit 1
fi

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### Unreleased
- Pending changes.

#### v1.6.1 [2026-05-18]
- Release notes.

#### v1.6.0 [2026-05-17]
- Older release notes.
EOF

if "$fixture/scripts/check-version.sh" >"$tmp_dir/ahead-tag.out" 2>"$tmp_dir/ahead-tag.err"; then
    echo "error: check-version passed with package metadata and changelog ahead of latest tag" >&2
    exit 1
fi

if ! grep -Fq 'project version 1.6.1 does not match latest release tag 1.6.0' "$tmp_dir/ahead-tag.err"; then
    echo "error: check-version failed for the wrong latest-tag reason" >&2
    cat "$tmp_dir/ahead-tag.err" >&2
    exit 1
fi

write_versions "1.6.0" "1.6.0.0"

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### Unreleased
- Pending changes.
EOF

if "$fixture/scripts/check-version.sh" >"$tmp_dir/no-release.out" 2>"$tmp_dir/no-release.err"; then
    echo "error: check-version passed without a numbered release" >&2
    exit 1
fi

if ! grep -Fq 'could not read latest changelog version from CHANGELOG.md' "$tmp_dir/no-release.err"; then
    echo "error: check-version failed for the wrong missing-release reason" >&2
    cat "$tmp_dir/no-release.err" >&2
    exit 1
fi

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### Unreleased
- Pending changes.

#### v1.6.1
- Malformed release heading.

#### v1.6.0 [2026-05-17]
- Older release notes.
EOF

if "$fixture/scripts/check-version.sh" >"$tmp_dir/malformed-release.out" 2>"$tmp_dir/malformed-release.err"; then
    echo "error: check-version skipped a malformed first numbered release" >&2
    exit 1
fi

if ! grep -Fq 'could not read latest changelog version from CHANGELOG.md' "$tmp_dir/malformed-release.err"; then
    echo "error: check-version failed for the wrong malformed-release reason" >&2
    cat "$tmp_dir/malformed-release.err" >&2
    exit 1
fi

cat >"$fixture/CHANGELOG.md" <<'EOF'
#### v1.6.0 [2026-05-17]
- Release notes.
EOF

# Images may reference a full commit created after the release tag.
mkdir -p "$fixture/docs/images/readme/tree.png"
printf 'image\n' >"$fixture/docs/images/readme/image.png"
printf 'child\n' >"$fixture/docs/images/readme/tree.png/child"
git -C "$fixture" add docs/images/readme
git -C "$fixture" commit -q -m "add images"
image_commit="$(git -C "$fixture" rev-parse HEAD)"
url_base="https://raw.githubusercontent.com/eideehi/valheim-automatics/"
write_link() {
    printf 'See [guide](https://github.com/eideehi/valheim-automatics/blob/1.6.0/docs/user-guide.md).\n![image](%s%s)\n' "$url_base" "$1" >"$fixture/distributor/thunderstore/README.md"
}
expect_failure() {
    local entry="$1" expected="$2"
    write_link "$entry"
    if "$fixture/scripts/check-version.sh" >"$tmp_dir/link.out" 2>"$tmp_dir/link.err"; then
        echo "error: check-version accepted $entry" >&2
        exit 1
    fi
    if ! grep -Fq "$expected" "$tmp_dir/link.err"; then
        echo "error: check-version failed for the wrong reason for $entry" >&2
        cat "$tmp_dir/link.err" >&2
        exit 1
    fi
}
write_link "$image_commit/docs/images/readme/image.png"
"$fixture/scripts/check-version.sh" >/dev/null
# Blob links are parsed by the same policy.
printf 'See [guide](https://github.com/eideehi/valheim-automatics/blob/1.6.0/docs/user-guide.md).\n![image](https://github.com/eideehi/valheim-automatics/blob/%s/docs/images/readme/image.png)\n' "$image_commit" >"$fixture/distributor/thunderstore/README.md"
"$fixture/scripts/check-version.sh" >/dev/null
expect_failure "main/docs/images/readme/image.png" 'contains main/master links'
expect_failure "${image_commit:0:12}/docs/images/readme/image.png" 'does not match latest release tag'
expect_failure "0000000000000000000000000000000000000000/docs/images/readme/image.png" 'is not a commit'
expect_failure "$image_commit/docs/images/readme/missing.png" 'is not a blob'
expect_failure "$image_commit/docs/images/readme/tree.png" 'is not a blob'
expect_failure "$image_commit/docs/user-guide.md" 'does not match latest release tag'
expect_failure '1.5.0/docs/user-guide.md' 'does not match latest release tag'

echo "check-version regression tests passed."
