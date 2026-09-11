#!/usr/bin/env bash

set -euo pipefail

failures=0
source_file="Automatics/AutomaticMining/AutomaticMining.cs"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

require_source_pattern() {
  local pattern="$1"
  local message="$2"
  local status=0

  rg -q "$pattern" "$source_file" || status=$?
  case "$status" in
    0) ;;
    1) fail "$message" ;;
    *) fail "could not search $source_file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

require_source_count_at_least() {
  local pattern="$1"
  local minimum="$2"
  local message="$3"
  local count
  local status=0

  count="$(rg -c "$pattern" "$source_file")" || status=$?
  case "$status" in
    0)
      if ((count < minimum)); then fail "$message"; fi
      ;;
    1) fail "$message" ;;
    *) fail "could not search $source_file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

reject_source_pattern() {
  local pattern="$1"
  local message="$2"
  local status=0

  rg -q "$pattern" "$source_file" || status=$?
  case "$status" in
    0) fail "$message" ;;
    1) ;;
    *) fail "could not search $source_file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

first_match_line() {
  local pattern="$1"
  local status=0
  local out=""

  out="$(rg -n "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$out" | head -1 | cut -d: -f1 ;;
    1) ;;
    *) printf 'ERROR\n' ;;
  esac
}

extract_block() {
  local start_pattern="$1"

  EXTRACT_BLOCK_START="$start_pattern" awk '
    BEGIN { start = ENVIRON["EXTRACT_BLOCK_START"] }
    !started && $0 ~ start { started = 1 }
    !started { next }
    {
      line = $0
      opens = gsub(/{/, "{")
      closes = gsub(/}/, "}")
      depth += opens - closes
      if (!opened && opens > 0) opened = 1
      print line
      if (opened && depth <= 0) exit
    }
  ' "$source_file"
}

if [[ ! -r "$source_file" ]]; then
  fail "cannot read $source_file; none of the mining checks below would mean anything"
  exit 1
fi

reject_source_pattern \
  "\\.IsOwner\\(\\)" \
  "mining candidate selection must not skip valid non-owner minerals"
require_source_pattern \
  "foreach \\(var automaticMining in automaticMinings\\)" \
  "mining must iterate ordered candidates instead of stopping at the first one"
require_source_pattern \
  "x\\._zNetView\\.HasOwner\\(\\)" \
  "mining candidate selection must skip ownerless ZDOs that cannot route damage"
require_source_pattern \
  "if \\(automaticMining\\.Mining\\(player\\)\\)" \
  "candidate iteration must continue until a target is actually mined"
require_source_count_at_least \
  "m_shared\\.m_toolTier >= minToolTier" \
  2 \
  "pickaxe selection must require a tool tier sufficient for the target"
reject_source_pattern \
  "Vector3\\.Distance\\(player\\.m_eye\\.position, hit\\) [^\\n]*Config\\.MiningRange" \
  "hit validation must use the computed mining range, including mining_range = 0 fallback"
require_source_pattern \
  "var hitCount = Physics\\.RaycastNonAlloc" \
  "raycast processing must capture the returned hit count"
require_source_pattern \
  "i < hitCount" \
  "raycast processing must iterate only returned hits"
require_source_pattern \
  "RaycastHitBuffer\\[i\\]" \
  "raycast processing must read only indexed returned hits"
reject_source_pattern \
  "RaycastHitBuffer\\.Where|foreach \\([^\\n]* in RaycastHitBuffer\\)" \
  "raycast processing must not scan stale entries in the whole static buffer"
require_source_pattern \
  "var mined = false" \
  "mining must start each candidate as not mined"
require_source_pattern \
  "parent\\.Damage\\(CreateHitData" \
  "mining must call damage only after a valid hit has been selected"
require_source_pattern \
  "mined = true" \
  "mining must mark success only after applying damage"
require_source_pattern \
  "return mined" \
  "mining must report success from the actual damage path"
require_source_pattern \
  "var colliders = GetColliders\\(parent\\)" \
  "mining should fetch colliders only after a sufficient pickaxe was selected"
require_source_pattern \
  "pickaxe\\.m_durability -= shared\\.m_useDurabilityDrain \\* Game\\.m_durabilityRate;" \
  "mining must wear the pickaxe the way a vanilla attack does, scaled by the world durability rate"
reject_source_pattern \
  "m_durability -= [A-Za-z_.]*m_useDurabilityDrain;" \
  "mining must not subtract the raw durability drain: vanilla scales it by Game.m_durabilityRate"

mining_body="$(extract_block "private static bool Mining\\(Player player, ItemDrop")"
damage_line="$(printf '%s\n' "$mining_body" | first_match_line 'parent\.Damage\(CreateHitData')"
use_line="$(printf '%s\n' "$mining_body" | first_match_line 'UsePickaxe\(player, pickaxe\)')"
success_line="$(printf '%s\n' "$mining_body" | first_match_line 'mined = true')"
if [[ -z "$mining_body" || -z "$damage_line" || -z "$use_line" || -z "$success_line" ||
      "$damage_line" == "ERROR" || "$use_line" == "ERROR" || "$success_line" == "ERROR" ]]; then
  fail "could not locate Damage, UsePickaxe and the success flag in the mining hit loop; their ordering check was not run"
elif ((damage_line >= use_line || use_line >= success_line)); then
  fail "each mining hit must apply Damage, then UsePickaxe, then mark the candidate mined"
fi

if ((failures > 0)); then
  exit 1
fi

printf 'PASS mining static invariant checks\n'
