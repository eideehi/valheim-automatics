#!/usr/bin/env bash

set -euo pipefail

failures=0

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

require_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"

  if ! rg -q "$pattern" "$file"; then
    fail "$message"
  fi
}

reject_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"

  if rg -q "$pattern" "$file"; then
    fail "$message"
  fi
}

require_text_pattern() {
  local text="$1"
  local pattern="$2"
  local message="$3"

  if ! printf '%s\n' "$text" | rg -q "$pattern"; then
    fail "$message"
  fi
}

# A body extracted by sed is empty when its anchor was renamed. Positive checks
# fail loudly on an empty body, but a *negative* check would pass vacuously, so
# every body that only feeds a rejection must be proven non-empty first.
require_nonempty_body() {
  local text="$1"
  local message="$2"

  if [[ -z "${text//[[:space:]]/}" ]]; then
    fail "$message"
  fi
}

require_case_guard_before_call() {
  local case_label="$1"
  local guard_call="$2"
  local action_call="$3"

  local block
  block="$(
    awk -v label="case \"$case_label\":" '
      index($0, label) { capture = 1; print; next }
      capture && /^[[:space:]]*case "/ { exit }
      capture { print }
    ' "$print_objects"
  )"

  if ! printf '%s\n' "$block" | awk \
    -v guard="$guard_call" \
    -v action="$action_call" '
      index($0, guard) { guard_line = NR }
      index($0, action) { action_line = NR }
      END { exit !(guard_line > 0 && action_line > 0 && guard_line < action_line) }
    '; then
    fail "printobjects $case_label must call $guard_call before $action_call"
  fi
}

require_text_before_text() {
  local text="$1"
  local first_pattern="$2"
  local second_pattern="$3"
  local message="$4"

  if ! printf '%s\n' "$text" | awk \
    -v first="$first_pattern" \
    -v second="$second_pattern" '
      index($0, first) && first_line == 0 { first_line = NR }
      index($0, second) && second_line == 0 { second_line = NR }
      END { exit !(first_line > 0 && second_line > 0 && first_line < second_line) }
    '; then
    fail "$message"
  fi
}

require_translation_key() {
  local file="$1"
  local key="$2"
  local message="$3"
  shift 3

  local filter='.translations[$key] | type == "string" and length > 0'
  local placeholder
  for placeholder in "$@"; do
    filter="$filter and contains(\"$placeholder\")"
  done

  if ! jq -e --arg key "$key" "$filter" "$file" > /dev/null; then
    fail "$message"
  fi
}

command=Automatics/ConsoleCommands/Command.cs
print_names=Automatics/ConsoleCommands/PrintNames.cs
print_objects=Automatics/ConsoleCommands/PrintObjects.cs
en_us=Automatics/Languages/en_us.json
ja_jp=Automatics/Languages/ja_jp.json

try_create_body="$(
  sed -n '/protected bool TryCreateTextFilter/,/protected string Usage/p' "$command"
)"
try_create_catch_body="$(
  printf '%s\n' "$try_create_body" | sed -n '/catch (ArgumentException/,/return false;/p'
)"
print_objects_command_action_body="$(
  sed -n '/protected override void CommandAction/,/private bool TryGetLocalPlayer/p' "$print_objects"
)"
print_object_body="$(
  sed -n '/private void PrintObject/,/private void PrintAnimal/p' "$print_objects"
)"

require_source_pattern \
  "$command" \
  "TryCreateTextFilter" \
  "console regex filters must be validated when they are created"
# The invariant is that the pattern is compiled into a Regex on the creation
# path and that the compiled Regex is what the filter ends up holding, so an
# invalid pattern surfaces here instead of at first use. The exact constructor
# argument list is not part of the invariant: d3b31ae added a match timeout and
# the previous spelling-exact assertion went stale for the whole v1.7.0 line.
require_text_pattern \
  "$try_create_body" \
  "filter = new TextFilter\\(new Regex\\(pattern[,)]" \
  "regex filters must be compiled from the pattern and installed as the filter when the filter is created"
# d3b31ae hardened this against catastrophic backtracking. Without this
# assertion the check above would happily accept a revert to `new Regex(pattern)`
# and reintroduce a client freeze on a hostile printnames/printobjects filter.
require_text_pattern \
  "$try_create_body" \
  "new Regex\\(pattern,[^)]*RegexTimeout\\)" \
  "regex filters must be compiled with a match timeout so a catastrophic pattern cannot freeze the client"
require_source_pattern \
  "$command" \
  "RegexTimeout = TimeSpan\\.From" \
  "the regex match timeout must be a bounded TimeSpan constant"
require_text_pattern \
  "$try_create_body" \
  "catch \\(ArgumentException" \
  "invalid regex filters must be reported as command errors"
require_text_pattern \
  "$try_create_catch_body" \
  "@command_common_invalid_regex" \
  "invalid regex errors must use the localized command error message"
require_text_pattern \
  "$try_create_catch_body" \
  "AddCommandError" \
  "invalid regex failures must be reported through the terminal context"
require_text_pattern \
  "$try_create_catch_body" \
  "return false" \
  "invalid regex failures must stop command execution"

reject_source_pattern \
  "$print_names" \
  "Regex\\.IsMatch" \
  "printnames must not evaluate unvalidated regex patterns"
require_source_pattern \
  "$print_names" \
  "TryCreateTextFilter\\(args, arg" \
  "printnames must create filters before scanning translations"

reject_source_pattern \
  "$print_objects" \
  "Regex\\.IsMatch" \
  "printobjects must not evaluate unvalidated regex patterns"
require_source_pattern \
  "$print_objects" \
  "TryCreateTextFilter\\(args, _include" \
  "printobjects include filters must be validated before object scans"
require_source_pattern \
  "$print_objects" \
  "TryCreateTextFilter\\(args, _exclude" \
  "printobjects exclude filters must be validated before object scans"
require_text_pattern \
  "$print_object_body" \
  "TryCreateTextFilter\\(args, _include" \
  "printobjects include filters must be validated only on object-scan paths"
require_text_pattern \
  "$print_object_body" \
  "TryCreateTextFilter\\(args, _exclude" \
  "printobjects exclude filters must be validated only on object-scan paths"
require_text_before_text \
  "$print_object_body" \
  "TryCreateTextFilter(args, _include" \
  "foreach" \
  "printobjects include filters must be validated before object scans"
require_text_before_text \
  "$print_object_body" \
  "TryCreateTextFilter(args, _exclude" \
  "foreach" \
  "printobjects exclude filters must be validated before object scans"
require_nonempty_body \
  "$print_objects_command_action_body" \
  "printobjects CommandAction body could not be extracted; the filter-dispatch rejection below would pass vacuously"
if printf '%s\n' "$print_objects_command_action_body" | rg -q "TryCreateTextFilter"; then
  fail "printobjects must not validate include/exclude filters before type dispatch"
fi
require_source_pattern \
  "$print_objects" \
  "TryGetLocalPlayer\\(args\\)" \
  "printobjects must guard the local player before reading transforms"
reject_source_pattern \
  "$print_objects" \
  "Player\\.m_localPlayer\\.transform" \
  "printobjects must use the guarded local player reference"
reject_source_pattern \
  "$print_objects" \
  "Player\\.m_localPlayer\\." \
  "printobjects must not access Player.m_localPlayer members directly"
require_source_pattern \
  "$print_objects" \
  "TryGetZoneSystem\\(args\\)" \
  "printobjects dungeon and spot scans must guard ZoneSystem"
require_source_pattern \
  "$print_objects" \
  "_zoneSystem\\.m_locationInstances" \
  "printobjects must use the guarded ZoneSystem reference"
reject_source_pattern \
  "$print_objects" \
  "ZoneSystem\\.instance\\." \
  "printobjects must not access ZoneSystem.instance members directly"

local_player_start="$(rg -n "private bool TryGetLocalPlayer" "$print_objects" | cut -d: -f1)"
local_player_end="$(rg -n "private bool TryGetZoneSystem" "$print_objects" | cut -d: -f1)"
zone_system_start="$local_player_end"
zone_system_end="$(rg -n "private void PrintObject" "$print_objects" | cut -d: -f1)"

while IFS=: read -r line _; do
  if (( line <= local_player_start || line >= local_player_end )); then
    fail "printobjects must read Player.m_localPlayer only inside TryGetLocalPlayer"
  fi
done < <(rg -n "Player\\.m_localPlayer" "$print_objects")

while IFS=: read -r line _; do
  if (( line <= zone_system_start || line >= zone_system_end )); then
    fail "printobjects must read ZoneSystem.instance only inside TryGetZoneSystem"
  fi
done < <(rg -n "ZoneSystem\\.instance" "$print_objects")

for case_label in animal monster flora mineral spawner vehicle other door container; do
  action="Print$(tr '[:lower:]' '[:upper:]' <<< "${case_label:0:1}")${case_label:1}(args)"
  require_case_guard_before_call "$case_label" "TryGetLocalPlayer(args)" "$action"
done

require_case_guard_before_call "dungeon" "TryGetLocalPlayer(args)" "PrintDungeon(args)"
require_case_guard_before_call "dungeon" "TryGetZoneSystem(args)" "PrintDungeon(args)"
require_case_guard_before_call "spot" "TryGetLocalPlayer(args)" "PrintSpot(args)"
require_case_guard_before_call "spot" "TryGetZoneSystem(args)" "PrintSpot(args)"

require_translation_key \
  "$en_us" \
  "@command_common_invalid_regex" \
  "English localization must include the invalid regex command error with placeholders" \
  "\$1" "\$2"
require_translation_key \
  "$en_us" \
  "@command_printobjects_error_player_unavailable" \
  "English localization must include the player unavailable command error"
require_translation_key \
  "$en_us" \
  "@command_printobjects_error_world_unavailable" \
  "English localization must include the world unavailable command error"
require_translation_key \
  "$ja_jp" \
  "@command_common_invalid_regex" \
  "Japanese localization must include the invalid regex command error with placeholders" \
  "\$1" "\$2"
require_translation_key \
  "$ja_jp" \
  "@command_printobjects_error_player_unavailable" \
  "Japanese localization must include the player unavailable command error"
require_translation_key \
  "$ja_jp" \
  "@command_printobjects_error_world_unavailable" \
  "Japanese localization must include the world unavailable command error"

if ((failures > 0)); then
  exit 1
fi

printf 'PASS console command static invariants\n'
