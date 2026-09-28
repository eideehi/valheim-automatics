#!/usr/bin/env bash
#
# Fast checks for the object data under Automatics/Data/Objects.
#
# These assertions used to live alongside the in-game object-validation harness in
# the former scripts/test-object-validation.sh. The heavy harness moved in-game
# (smoke/Automatics.SmokeTest), but these checks are fast, run without the game,
# and validate every data file, which the smoke plugin cannot exhaustively cover,
# so they are kept here. Requires `jq` and `rg`.

set -euo pipefail

failures=0

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

object_files=()
while IFS= read -r file; do
  object_files+=("$file")
done < <(rg --files Automatics/Data/Objects -g '*.json')

# Every object data file must declare a non-empty type and an array of values,
# each value must carry a non-empty identifier/label and at least one matcher with
# a non-empty value, and every regex matcher must compile.
for file in "${object_files[@]}"; do
  if ! jq -e '
    type == "object"
    and (.type | type == "string" and length > 0)
    and (.values | type == "array")
    and all(.values[];
      type == "object"
      and (.identifier | type == "string" and length > 0)
      and (.label | type == "string" and length > 0)
      and (.matches | type == "array" and length > 0)
      and all(.matches[];
        type == "object"
        and (.value | type == "string" and length > 0)
        and (((.regex // false) == false) or
          (try (.value as $pattern | ("probe" | test($pattern) | type == "boolean")) catch false))
      )
    )
  ' "$file" > /dev/null; then
    fail "object data must reject null or empty child values: $file"
  fi
done

runestone_match="$(
  jq -r '.values[] | select(.identifier == "Runestone") | .matches[0].value // empty' \
    Automatics/Data/Objects/AutomaticMapping/other.json
)"
if [[ "$runestone_match" != '$piece_lorestone' ]]; then
  fail "Runestone matcher should be \$piece_lorestone but was ${runestone_match:-<empty>}"
fi

rock_matches="$(
  jq -r '
    .values[] | select(.identifier == "Rock") | .matches[]
    | [((.regex // false) | tostring), .value] | @tsv
  ' Automatics/Data/Objects/mineral.json
)"
if [[ -z "$rock_matches" ]]; then
  fail "mineral data must define a Rock entry for ordinary stone rocks"
else
  if printf '%s\n' "$rock_matches" | cut -f1 | grep -qv '^false$'; then
    fail "the Rock mineral entry must use only exact (non-regex) matchers"
  fi
  if printf '%s\n' "$rock_matches" | cut -f2 | grep -qx -e 'rock4_copper' -e 'rock3_silver'; then
    fail "the Rock mineral entry must not match the copper or silver deposit prefabs"
  fi
fi

duplicates="$(
  for file in "${object_files[@]}"; do
    jq -r '
    select(.type != null) as $doc
    | .values[]?
    | select(type == "object")
    | .matches[]?
    | select(type == "object")
    | select((.regex // false) == false)
    | select(.value | type == "string" and length > 0)
    | [$doc.type, .value]
    | @tsv
    ' "$file"
  done | sort | uniq -d
)"
if [[ -n "$duplicates" ]]; then
  fail "duplicate exact object matchers found: ${duplicates//$'\n'/, }"
fi

if ((failures > 0)); then
  exit 1
fi

printf 'PASS object data static invariants\n'
