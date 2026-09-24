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

# Area mining: a swing's extra hits wear the pickaxe per target, and gain no skill, stamina or
# Automatic Mining bookkeeping of their own.
source_file="Automatics/AutomaticMining/AreaMining.cs"
if [[ ! -r "$source_file" ]]; then
  fail "cannot read $source_file; the area mining checks were not run"
else
  reject_source_pattern \
    "RaiseSkill" \
    "area mining must not raise a skill: the swing already raised it once"
  reject_source_pattern \
    "UsePickaxe|UseStamina|AddNoise" \
    "area mining must not reuse Automatic Mining's per-hit bookkeeping or charge stamina: only durability is charged per extra target"
  reject_source_pattern \
    "m_durability -= [A-Za-z_.]*m_useDurabilityDrain;" \
    "area mining must not subtract the raw durability drain: vanilla scales it by Game.m_durabilityRate"
  require_source_pattern \
    "if \\(!_swing \\|\\| _expanding" \
    "recording and expansion must ignore the extra hits area mining applies itself"
  require_source_pattern \
    "if \\(_swing \\|\\| _expanding\\) return false;" \
    "a melee attack nested inside a swing must not reset the open swing context"
  require_source_pattern \
    "if \\(!IsStanding\\(target\\)\\) continue;" \
    "each extra target must be revalidated right before its hit, without spending durability or the cap"

  expand_body="$(extract_block "internal static void Expand\\(\\)")"
  expanding_line="$(printf '%s\n' "$expand_body" | first_match_line '_expanding = true;')"
  standing_line="$(printf '%s\n' "$expand_body" | first_match_line 'if \(!IsStanding\(target\)\) continue;')"
  check_line="$(printf '%s\n' "$expand_body" | first_match_line '_weapon\.m_durability <= 0f\) break;')"
  damage_line="$(printf '%s\n' "$expand_body" | first_match_line 'target\.Destructible\.Damage\(hit\);')"
  charge_line="$(printf '%s\n' "$expand_body" | first_match_line '_weapon\.m_durability -= shared\.m_useDurabilityDrain \* Game\.m_durabilityRate;')"
  cleared_line="$(printf '%s\n' "$expand_body" | first_match_line '_expanding = false;')"
  if [[ -z "$expand_body" || -z "$expanding_line" || -z "$check_line" || -z "$damage_line" ||
        -z "$charge_line" || -z "$cleared_line" || "$expanding_line" == "ERROR" ||
        "$check_line" == "ERROR" || "$damage_line" == "ERROR" || "$charge_line" == "ERROR" ||
        "$cleared_line" == "ERROR" ]]; then
    fail "could not locate the guard, durability check, Damage, durability charge and guard reset in the area expansion; their ordering check was not run"
  elif [[ -z "$standing_line" || "$standing_line" == "ERROR" ]] || ((standing_line >= check_line)); then
    fail "area expansion must revalidate a target before it checks and spends durability on it"
  elif ((expanding_line >= check_line || check_line >= damage_line || damage_line >= charge_line ||
         charge_line >= cleared_line)); then
    fail "area expansion must raise its guard, check durability before each extra hit, apply Damage, charge durability per extra target, then lower the guard"
  fi
fi

# The expansion runs once per swing, from the postfix only; the finalizer only clears the swing.
source_file="Automatics/AutomaticMining/Patches.cs"
if [[ ! -r "$source_file" ]]; then
  fail "cannot read $source_file; the area mining patch checks were not run"
else
  postfix_body="$(extract_block "private static void Attack_DoMeleeAttack_Postfix\\(")"
  finalizer_body="$(extract_block "private static void Attack_DoMeleeAttack_Finalizer\\(")"
  expand_calls="$(rg -c "AreaMining\.Expand\(" "$source_file" || true)"
  if [[ "$(printf '%s\n' "$postfix_body" | tr -d '[:space:]')" != *'(bool__state){if(__state)AreaMining.Expand();}' ]]; then
    fail "the DoMeleeAttack postfix must only expand the swing its own prefix opened"
  fi
  if [[ "$(printf '%s\n' "$finalizer_body" | tr -d '[:space:]')" != *'(bool__state){if(__state)AreaMining.EndSwing();}' ]]; then
    fail "the DoMeleeAttack finalizer must only clear the swing its own prefix opened"
  fi
  require_source_pattern \
    "__state = AreaMining\\.BeginSwing\\(" \
    "the DoMeleeAttack prefix must own the swing only when it opened the context"

  if [[ "$expand_calls" != "1" ]]; then
    fail "area expansion must be called exactly once, from the DoMeleeAttack postfix: found ${expand_calls:-0}"
  fi
fi

if ((failures > 0)); then
  exit 1
fi

printf 'PASS mining static invariant checks\n'
