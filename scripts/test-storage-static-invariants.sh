#!/usr/bin/env bash

set -euo pipefail

failures=0
storage_source="Automatics/AutomaticStorage/AutomaticStorage.cs"
module_source="Automatics/AutomaticStorage/Module.cs"
config_source="Automatics/AutomaticStorage/Config.cs"
container_access_source="Automatics/Valheim/ContainerAccess.cs"
root_config_source="Automatics/Config.cs"
ap_config_source="Automatics/AutomaticProcessing/Config.cs"
valheim_objects_source="Automatics/Valheim/ValheimObjects.cs"
en_locale_source="Automatics/Languages/en_us.json"
ja_locale_source="Automatics/Languages/ja_jp.json"
config_doc_source="CONFIG.md"
readme_source="README.md"
thunderstore_readme_source="distributor/thunderstore/README.md"
user_guide_source="docs/user-guide.md"

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

require_source_count_exact() {
  local file="$1"
  local pattern="$2"
  local expected="$3"
  local message="$4"
  local count

  count=$( (rg -o "$pattern" "$file" || true) | wc -l | tr -d ' ')
  if ((count != expected)); then
    fail "$message (expected $expected, got $count)"
  fi
}

require_source_order() {
  local file="$1"
  local first_pattern="$2"
  local second_pattern="$3"
  local message="$4"
  local first_line
  local second_line

  first_line=$(rg -n "$first_pattern" "$file" | head -n1 | cut -d: -f1 || true)
  second_line=$(rg -n "$second_pattern" "$file" | head -n1 | cut -d: -f1 || true)

  if [[ -z "$first_line" || -z "$second_line" ]]; then
    fail "$message"
    return
  fi

  if ((first_line >= second_line)); then
    fail "$message"
  fi
}

require_json_key() {
  local file="$1"
  local key="$2"
  local message="$3"

  if ! jq -e --arg key "$key" '.translations | has($key)' "$file" >/dev/null; then
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

# The 1-based line of the first match in a text, nothing when there is none,
# and ERROR when the search could not run.
first_line_in_text() {
  local text="$1"
  local pattern="$2"
  local status=0
  local out=""

  out="$(printf '%s\n' "$text" | rg -n "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "${out%%:*}" ;;
    1) ;;
    *) printf 'ERROR\n' ;;
  esac
}

# Both patterns occur in the text, and the first one before the second.
require_text_order() {
  local text="$1"
  local first_pattern="$2"
  local second_pattern="$3"
  local message="$4"
  local first_line
  local second_line

  first_line="$(first_line_in_text "$text" "$first_pattern")"
  second_line="$(first_line_in_text "$text" "$second_pattern")"

  if [[ "$first_line" == "ERROR" || "$second_line" == "ERROR" ]]; then
    fail "could not search for the order; the check behind this was not run: $message"
    return
  fi

  if [[ -z "$first_line" || -z "$second_line" ]] || ((first_line >= second_line)); then
    fail "$message"
  fi
}

# A body extracted by sed is empty when its anchor was renamed, which would turn
# every assertion scoped to it into a failure with a misleading message. Prove
# the body was actually located before asserting anything about its contents.
require_nonempty_body() {
  local text="$1"
  local message="$2"

  if [[ -z "${text//[[:space:]]/}" ]]; then
    fail "$message"
  fi
}

# The hotkey path is wired through the module's scope, as the two halves of one
# scope.Subscribe with OnPlayerUpdate as its handler, so turning the module off
# removes it.
require_source_pattern \
  "$module_source" \
  "h => Hooks\\.OnPlayerUpdate \\+= h" \
  "storage must run from the player update hotkey path through the module's scope: the add half of scope.Subscribe is missing"
require_source_pattern \
  "$module_source" \
  "h => Hooks\\.OnPlayerUpdate -= h" \
  "storage must run from the player update hotkey path through the module's scope: the remove half of scope.Subscribe is missing"
require_source_pattern \
  "$module_source" \
  "^\\s*OnPlayerUpdate\\);" \
  "storage must run from the player update hotkey path through the module's scope: OnPlayerUpdate is not the subscribed handler"
reject_source_pattern \
  "$module_source" \
  "OnPlayerFixedUpdate|Hooks\\.OnPlayerFixedUpdate" \
  "storage must not add periodic fixed-update storage in this slice"
require_source_pattern \
  "$module_source" \
  "!Config\\.EnableAutomaticStorage" \
  "runtime enable toggle must gate storage"
require_source_pattern \
  "$module_source" \
  "!takeInput" \
  "blocked game input must prevent storage"
require_source_pattern \
  "$module_source" \
  "Game\\.IsPaused\\(\\)" \
  "paused games must prevent storage"
require_source_pattern \
  "$module_source" \
  "player\\.InAttack\\(\\) \\|\\| player\\.InDodge\\(\\)" \
  "attack and dodge states must prevent storage"
require_source_pattern \
  "$module_source" \
  "StoreItemsKey\\.MainKey == KeyCode\\.None" \
  "unassigned hotkeys must not trigger storage"
require_source_pattern \
  "$module_source" \
  "StoreItemsKey\\.IsDown\\(\\)" \
  "storage must be a one-shot key press action"

require_source_pattern \
  "$config_source" \
  "ValheimObject\\.Container, excludes: new\\[\\] \\{ \"PieceChestPrivate\" \\}" \
  "private chests must be excluded by default"
require_source_pattern \
  "$config_source" \
  "new KeyboardShortcut\\(\\)" \
  "storage hotkey must default to unassigned"
require_source_pattern \
  "$config_source" \
  "allowed_item_types" \
  "storage must expose allowed item types"
require_source_pattern \
  "$config_source" \
  "excluded_items" \
  "storage must expose item exclusions"

require_source_pattern \
  "$storage_source" \
  "m_gridPos\\.y == 0" \
  "hotbar row must be protected by default"
require_source_pattern \
  "$storage_source" \
  "m_shared\\.m_questItem" \
  "quest items must be skipped"
require_source_pattern \
  "$storage_source" \
  "m_dropPrefab == null" \
  "items without a prefab must be skipped to avoid non-restorable inventory entries"
require_source_pattern \
  "$storage_source" \
  "m_equipped \\|\\| player\\.IsItemEquiped\\(item\\)" \
  "equipped items must be skipped"
require_source_pattern \
  "$storage_source" \
  "StartsWith\\(\"r/\", StringComparison\\.Ordinal\\)" \
  "regex exclusions must use the r/ prefix"
require_source_pattern \
  "$storage_source" \
  "RegexOptions\\.IgnoreCase" \
  "regex exclusions must be case-insensitive"
require_source_pattern \
  "$storage_source" \
  "IndexOf\\(partialMatch, StringComparison\\.OrdinalIgnoreCase\\)" \
  "plain exclusions must be case-insensitive partial matches"
reject_source_pattern \
  "$storage_source" \
  "ContainerAccess\\.TryClaimContainer\\(target\\.Container\\)" \
  "automatic storage must not claim non-owner containers before direct mutation"
require_source_order \
  "$storage_source" \
  "ContainerAccess\\.TryPrepareOwnedContainerMutation\\(target\\.Container\\)" \
  "target\\.Inventory\\.AddItem\\(clone\\)" \
  "owned-container mutation validation must happen before destination mutation"
require_source_pattern \
  "$storage_source" \
  "ContainerAccess\\.CanDirectlyMutateContainer\\(player, target\\.Container\\)" \
  "storage must skip containers not locally owned for direct mutation"
require_source_pattern \
  "$storage_source" \
  "ContainerAccess\\.TryPrepareOwnedContainerMutation\\(target\\.Container\\)" \
  "storage must revalidate owned container mutation immediately before mutating"
require_source_pattern \
  "$storage_source" \
  "target\\.Inventory\\.AddItem\\(clone\\)" \
  "storage must add cloned item data to the destination"
require_source_pattern \
  "$storage_source" \
  "CountExactItems\\(target\\.Inventory, sourceItem\\) - before" \
  "accepted amount must be measured from destination count delta"
require_source_pattern \
  "$storage_source" \
  "RemoveItem\\(sourceItem, accepted\\)" \
  "player inventory removal must use the accepted amount only"
require_source_pattern \
  "$storage_source" \
  "CaptureTransferState\\(target\\.Inventory, sourceItem\\)" \
  "destination rollback must capture pre-add object identities and stack counts"
require_source_pattern \
  "$storage_source" \
  "RollbackTransfer\\(target\\.Inventory, sourceItem, beforeTransfer," \
  "failed player removal must roll back destination additions"
require_source_pattern \
  "$storage_source" \
  "beforeTransfer\\.TryGetValue\\(storedItem, out var stack\\)" \
  "destination rollback must compare against pre-add stack counts"
require_source_pattern \
  "$storage_source" \
  "Where\\(IsValidTarget\\)" \
  "runtime snapshots must skip stale or destroyed containers"
require_source_pattern \
  "$storage_source" \
  "while \\(sourceRemaining > moved\\)" \
  "partial transfers must continue across available capacity"
require_source_pattern \
  "$storage_source" \
  "if \\(accepted < chunk\\) break" \
  "partial destination acceptance must stop the current target chunk"
require_source_pattern \
  "$storage_source" \
  "StoragePlanner\\.Plan" \
  "runtime storage must use the pure routing planner"

# The order of one transfer, inside TransferAmount itself: the owned-container
# check before the copy into the destination, and the copy before the removal
# of the accepted amount from the player. The file-wide order check above only
# compares first occurrences, so a second, reordered copy of these calls would
# pass it.
transfer_amount_body="$(
  sed -n '/private static int TransferAmount(/,/^        }$/p' "$storage_source"
)"
require_nonempty_body \
  "$transfer_amount_body" \
  "AutomaticStorage.TransferAmount body could not be extracted; the transfer order assertions cannot be evaluated"
require_text_order \
  "$transfer_amount_body" \
  "ContainerAccess\\.TryPrepareOwnedContainerMutation\\(target\\.Container\\)" \
  "target\\.Inventory\\.AddItem\\(clone\\)" \
  "TransferAmount must validate the owned-container mutation before it adds the clone to the destination"
require_text_order \
  "$transfer_amount_body" \
  "target\\.Inventory\\.AddItem\\(clone\\)" \
  "playerInventory\\.RemoveItem\\(sourceItem, accepted\\)" \
  "TransferAmount must add the clone to the destination before it removes the accepted amount from the player inventory"

# Turning the module off forgets the invalid exclusion patterns already
# reported, so a pattern that is still invalid is reported again once the
# module is back on instead of never again.
disable_body="$(
  sed -n '/public void Disable(ModuleScope scope)/,/^        }$/p' "$module_source"
)"
reset_session_body="$(
  sed -n '/internal static void ResetSession()/,/^        }$/p' "$storage_source"
)"
forget_regexes_body="$(
  sed -n '/public static void ForgetReportedRegexes()/,/^            }$/p' "$storage_source"
)"
require_nonempty_body \
  "$disable_body" \
  "Module.Disable body could not be extracted; the session reset assertions cannot be evaluated"
require_nonempty_body \
  "$reset_session_body" \
  "AutomaticStorage.ResetSession body could not be extracted; the session reset assertions cannot be evaluated"
require_nonempty_body \
  "$forget_regexes_body" \
  "ItemExclusionMatcher.ForgetReportedRegexes body could not be extracted; the session reset assertions cannot be evaluated"
require_text_pattern \
  "$disable_body" \
  "^\\s*AutomaticStorage\\.ResetSession\\(\\);" \
  "disabling storage must drop its session state through AutomaticStorage.ResetSession()"
require_text_pattern \
  "$reset_session_body" \
  "^\\s*ItemExclusionMatcher\\.ForgetReportedRegexes\\(\\);" \
  "AutomaticStorage.ResetSession must forget the invalid exclusion patterns already reported"
require_text_pattern \
  "$forget_regexes_body" \
  "^\\s*ReportedInvalidRegexes\\.Clear\\(\\);" \
  "ItemExclusionMatcher.ForgetReportedRegexes must clear the reported invalid patterns"

require_source_count_exact \
  "$root_config_source" \
  "BindCustomValheimObject\\(\"custom_container\", ValheimObject\\.Container\\)" \
  1 \
  "custom_container must be bound exactly once in the root config"
reject_source_pattern \
  "$ap_config_source" \
  "custom_container" \
  "automatic processing config must not define its own custom_container"
require_source_pattern \
  "$valheim_objects_source" \
  "public static readonly ValheimObject Container" \
  "shared container ValheimObject must be declared"
require_source_pattern \
  "$valheim_objects_source" \
  "Container = new ValheimObject\\(\"container\"\\)" \
  "shared container ValheimObject must use the container data file"
require_source_pattern \
  "$valheim_objects_source" \
  "Container\\.Register\\(JsonCache\\)" \
  "shared container ValheimObject must be registered during initialization"

require_json_key \
  "$en_locale_source" \
  "@config_automatic_storage_section" \
  "English localization must include the automatic storage section"
require_json_key \
  "$en_locale_source" \
  "@config_automatic_storage_store_items_key_name" \
  "English localization must include the storage hotkey label"
require_json_key \
  "$en_locale_source" \
  "@message_automatic_storage_stored_items" \
  "English localization must include the storage result message"
require_json_key \
  "$ja_locale_source" \
  "@config_automatic_storage_section" \
  "Japanese localization must include the automatic storage section"
require_json_key \
  "$ja_locale_source" \
  "@config_automatic_storage_store_items_key_name" \
  "Japanese localization must include the storage hotkey label"
require_json_key \
  "$ja_locale_source" \
  "@message_automatic_storage_stored_items" \
  "Japanese localization must include the storage result message"

require_source_pattern \
  "$config_doc_source" \
  "^## \\[ #9 Automatic Storage \\] / \\[automatic_storage\\]" \
  "CONFIG.md must document the automatic_storage section"
require_source_pattern \
  "$config_doc_source" \
  "^### Store Items / \\[store_items_key\\]" \
  "CONFIG.md must document the storage hotkey"
require_source_pattern \
  "$readme_source" \
  "Automatic storage" \
  "README.md must mention automatic storage"
require_source_pattern \
  "$thunderstore_readme_source" \
  "Automatic storage" \
  "Thunderstore README must mention automatic storage"
require_source_pattern \
  "$user_guide_source" \
  "Automatic Storage" \
  "user guide must include automatic storage"

# Guard stone (ward) access. The invariant is that a container which opts into
# the ward check cannot be used unless every enclosing, enabled ward permits the
# *acting* player. It is deliberately not spelled `PrivateArea.CheckAccess`:
# 9e98d30 replaced that vanilla call because it is hardcoded to
# Player.m_localPlayer and cannot validate an arbitrary acting player, which the
# server-aware mutation paths require. Assert the behaviour, and reject a revert
# to the local-player-only API.
can_use_container_body="$(
  sed -n '/public static bool CanUseContainer/,/public static bool CanDirectlyMutateContainer/p' \
    "$container_access_source"
)"
check_ward_access_body="$(
  sed -n '/private static bool CheckWardAccess/,/private static bool IsEnabled/p' \
    "$container_access_source"
)"
require_nonempty_body \
  "$can_use_container_body" \
  "ContainerAccess.CanUseContainer body could not be extracted; the guard stone assertions cannot be evaluated"
require_nonempty_body \
  "$check_ward_access_body" \
  "ContainerAccess.CheckWardAccess body could not be extracted; the guard stone assertions cannot be evaluated"
require_text_pattern \
  "$can_use_container_body" \
  "container\\.m_checkGuardStone" \
  "guard stone access must be checked whenever the container opts into the ward check"
require_text_pattern \
  "$can_use_container_body" \
  "!CheckWardAccess\\(player, container\\.transform\\.position\\)" \
  "guard stone access must be evaluated for the acting player at the container position and must deny use when it fails"
require_text_pattern \
  "$check_ward_access_body" \
  "GetStaticField<PrivateArea, List<PrivateArea>>" \
  "ward access must be resolved against PrivateArea's registered areas"
require_text_pattern \
  "$check_ward_access_body" \
  "!HasPlayerAccess\\(area, player\\)" \
  "ward access must deny the acting player when an enclosing ward does not permit them"
reject_source_pattern \
  "$container_access_source" \
  "PrivateArea\\.CheckAccess" \
  "ward access must not use PrivateArea.CheckAccess: it is hardcoded to the local player and cannot validate an arbitrary acting player"
require_source_pattern \
  "$container_access_source" \
  "Reflections\\.InvokeMethod<bool>\\(container, \"CheckAccess\"" \
  "vanilla container privacy access must be checked"
require_source_pattern \
  "$container_access_source" \
  "IsInUse\\(\\)" \
  "in-use containers must be skipped"
require_source_pattern \
  "$container_access_source" \
  "ZDOVars\\.s_inUse" \
  "non-owner in-use checks must use the replicated ZDO flag"
require_source_pattern \
  "$container_access_source" \
  "CanDirectlyMutateContainer" \
  "storage must have a helper that requires local container ownership before mutation"
require_source_pattern \
  "$container_access_source" \
  "TryPrepareOwnedContainerMutation" \
  "storage must have a final owned-mutation validation helper"
require_source_pattern \
  "$container_access_source" \
  "!nview\\.IsOwner\\(\\)\\) return false" \
  "owned-mutation validation must reject non-owner containers"
require_source_pattern \
  "$container_access_source" \
  "ClaimOwnership\\(\\)" \
  "container ownership claim helper must persist direct mutations"

if ((failures > 0)); then
  exit 1
fi

printf 'PASS storage static invariant checks\n'
