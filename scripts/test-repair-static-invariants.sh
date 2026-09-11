#!/usr/bin/env bash

set -euo pipefail

failures=0
piece_file="Automatics/AutomaticRepair/PieceRepair.cs"
item_file="Automatics/AutomaticRepair/ItemRepair.cs"

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

reject_text_pattern() {
  local text="$1"
  local pattern="$2"
  local message="$3"

  if printf '%s\n' "$text" | rg -q "$pattern"; then
    fail "$message"
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

piece_repair_body="$(
  sed -n '/public static void Repair/,/^        }/p' "$piece_file"
)"
item_can_repair_body="$(
  sed -n '/private static bool CanRepair/,/^        }/p' "$item_file"
)"

require_source_pattern \
  "$piece_file" \
  "GetRightItem" \
  "piece repair must use the vanilla right-hand build tool"
reject_source_pattern \
  "$piece_file" \
  "GetLeftItem|GetCurrentWeapon" \
  "piece repair must not use off-hand or arbitrary current weapons as repair tools"
require_source_pattern \
  "$piece_file" \
  "selectedPiece\\.m_repairPiece" \
  "piece repair must require the selected vanilla repair action"
require_source_pattern \
  "$piece_file" \
  "GlobalKeys\\.NoWorkbench" \
  "piece repair must honor the vanilla NoWorkbench global key"
require_source_pattern \
  "$piece_file" \
  "GetEquipmentHomeItemModifier" \
  "piece repair stamina cost must include equipment home-item modifiers"
require_source_pattern \
  "$piece_file" \
  "ModifyHomeItemStaminaUsage" \
  "piece repair stamina cost must include status effect home-item modifiers"
reject_source_pattern \
  "$piece_file" \
  "m_placementDurabilitySkill|GetPlaceDurability" \
  "piece repair durability cost must match Player.Repair raw durability drain"
require_text_before_text \
  "$piece_repair_body" \
  "HaveStamina(toolData.m_attack.m_attackStamina)" \
  "wearNTear.Repair()" \
  "piece repair must check vanilla stamina before mutating piece durability"
require_source_pattern \
  "$piece_file" \
  "UseStamina\\(GetBuildStamina\\(player, tool\\)\\)" \
  "piece repair must consume vanilla build stamina after successful repair"
require_source_pattern \
  "$piece_file" \
  "UseEitr\\(toolData\\.m_attack\\.m_attackEitr\\)" \
  "piece repair must consume the selected tool's repair eitr cost"
require_source_pattern \
  "$piece_file" \
  "tool\\.m_durability -= toolData\\.m_useDurabilityDrain \\* Game\\.m_durabilityRate;" \
  "piece repair must consume Player.Repair durability, scaled by the world durability rate, after successful repair"
reject_source_pattern \
  "$piece_file" \
  "m_durability -= [A-Za-z_.]*m_useDurabilityDrain;" \
  "piece repair must not subtract the raw durability drain: vanilla scales it by Game.m_durabilityRate"

require_source_pattern \
  "$item_file" \
  "CanUseRepairStation" \
  "item repair must verify station usability before repairing worn items"
require_source_pattern \
  "$item_file" \
  "player\\.NoCostCheat\\(\\) && station == null" \
  "item repair must allow vanilla no-cost repair without a station"
require_source_pattern \
  "$item_file" \
  "RepairAll\\(player, null\\)" \
  "item repair must reach the no-cost stationless repair path"
require_source_pattern \
  "$item_file" \
  "item\\.m_worldLevel >= Game\\.m_worldLevel" \
  "item repair must include the vanilla world-level repair exception"
require_source_pattern \
  "$item_file" \
  "Mathf\\.Min\\(station\\.GetLevel\\(\\), 4\\)" \
  "item repair must clamp station level like InventoryGui.CanRepair"
reject_text_pattern \
  "$item_can_repair_body" \
  "station\\.GetLevel\\(\\) >= recipe\\.m_minStationLevel" \
  "item repair must not compare unclamped station level directly"

if ((failures > 0)); then
  exit 1
fi

printf 'PASS repair static invariant checks\n'
