#!/usr/bin/env bash
#
# Source-shape invariants for the module lifecycle framework.
#
# These are regression tripwires, not proofs. Each one is tied to a defect shape that
# review actually found, and none of them interprets C#: "the deferred return is guarded"
# means the guard is written near the return, not that it dominates it. The properties
# that can genuinely be established offline are executed by tests/ModuleLifecycleHarness
# instead; the rest stay open until the game can be run.
#
# What a tripwire may never do is fail to run and call that a pass. Every search below
# distinguishes "no match" from "could not search", and a missing or unreadable file stops
# the script rather than quietly satisfying every reject_source_pattern in it.
#
# Two halves: the framework itself, and the modules that have been migrated onto it. The
# migrated list below is explicit and grows by one line per migration, because "check every
# module" would fail for the eight that still follow the start-up-only path on purpose
# (R5.1a).
#
set -euo pipefail

failures=0
registry_file="Automatics/ModuleRegistry.cs"
scope_file="Automatics/ModuleScope.cs"
lifecycle_file="Automatics/ModuleLifecycle.cs"
machine_file="Automatics/ModuleTransitionMachine.cs"
plugin_file="Automatics/Automatics.cs"
root_patches_file="Automatics/Patches.cs"

# Modules migrated to the runtime lifecycle. One line per migration; everything else keeps
# the start-up-only path and its early return, and checking it here would be wrong.
migrated_module_dirs=("Automatics/AutomaticPickup")

# Modules that have not been migrated. Their early return is part of passing, not a defect:
# removing it before the module is migrated hands non-activation guards to code this slice
# does not verify (R5.1a).
unmigrated_module_configs=(
  "Automatics/AutomaticDoor/Config.cs"
  "Automatics/AutomaticFarming/Config.cs"
  "Automatics/AutomaticFeeding/Config.cs"
  "Automatics/AutomaticMapping/Config.cs"
  "Automatics/AutomaticMining/Config.cs"
  "Automatics/AutomaticProcessing/Config.cs"
  "Automatics/AutomaticRepair/Config.cs"
  "Automatics/AutomaticStorage/Config.cs"
)

pickup_config="Automatics/AutomaticPickup/Config.cs"
pickup_module="Automatics/AutomaticPickup/Module.cs"
pickup_patches="Automatics/AutomaticPickup/Patches.cs"
pickup_component="Automatics/AutomaticPickup/AutomaticPickup.cs"
pickup_globals="Automatics/AutomaticPickup/Globals.cs"

# Identifiers that are followed by an argument list but are not calls.
keywords="if|for|foreach|while|switch|catch|using|lock|return|new|do|fixed|sizeof|typeof|nameof"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

# rg exits 0 on a match, 1 on no match, and 2 or more on an error. Collapsing the last two
# is how a check silently stops checking, so each helper below spells the three out.
require_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) ;;
    1) fail "$message" ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

reject_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) fail "$message" ;;
    1) ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

# Prints the number of matching lines, or the word ERROR, which no caller accepts.
count_matches() {
  local file="$1"
  local pattern="$2"
  local status=0
  local count=""

  count="$(rg -c "$pattern" "$file")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

count_stdin_matches() {
  local pattern="$1"
  local status=0
  local count=""

  count="$(rg -c "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

# Prints the 1-based line number of the first match in the text on stdin, nothing when
# there is no match, and ERROR when the search could not run. Written this way because
# `rg -n ... | head -1` inside a command substitution aborts the whole script under
# `set -euo pipefail` when there is no match: the check would then neither report nor let
# the checks after it run.
# True when any argument is the ERROR marker from first_match_line. ERROR is not a number,
# and bash arithmetic would read it as 0, so a search that could not run has to be caught
# before any comparison rather than after it.
lookup_failed() {
  local value

  for value in "$@"; do
    if [[ "$value" == "ERROR" ]]; then
      return 0
    fi
  done

  return 1
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

# Prints the block that starts at the first line matching the pattern and ends at the
# line where the braces opened inside it balance again. Pass - to read standard input.
extract_block() {
  local file="$1"
  local start_pattern="$2"

  # The pattern travels through the environment: awk -v would eat its backslashes.
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
  ' "$file"
}

# Every identifier that is applied to an argument list, comments stripped. A search that
# could not run emits a token no allowlist matches, so the caller fails instead of seeing
# an empty and therefore innocent-looking result.
calls_in() {
  local status=0

  sed -e 's://.*::' | rg -o '[A-Za-z_][A-Za-z0-9_]*\s*\(' || status=$?
  if ((status > 1)); then
    printf '<<the search for calls could not be run>>\n'
  fi
}

# Source with its line comments removed. Prose that names a forbidden call must not trip a
# check, and prose that names a required one must not satisfy it.
without_comments() {
  sed -e 's://.*::' "$1"
}

# The number of lines matching a pattern once comments are gone, or ERROR.
count_stripped_matches() {
  local file="$1"
  local pattern="$2"
  local body

  body="$(without_comments "$file")"
  printf '%s\n' "$body" | count_stdin_matches "$pattern"
}

# An expression-bodied member: from its declaration to the line that ends the expression.
# extract_block only knows braces, and would run to the end of the file here.
extract_expression_body() {
  local file="$1"
  local start_pattern="$2"

  EXTRACT_EXPR_START="$start_pattern" awk '
    BEGIN { start = ENVIRON["EXTRACT_EXPR_START"] }
    !started && $0 ~ start { started = 1 }
    !started { next }
    { print; if ($0 ~ /;[ \t]*$/) exit }
  ' "$file"
}

# A reject_source_pattern against a file that is not there passes every time, so the files
# are checked before anything else and a missing one ends the run.
for source_file in \
  "$registry_file" "$scope_file" "$lifecycle_file" \
  "$machine_file" "$plugin_file" "$root_patches_file" \
  "$pickup_config" "$pickup_module" "$pickup_patches" \
  "$pickup_component" "$pickup_globals" \
  "${unmigrated_module_configs[@]}"; do
  if [[ ! -r "$source_file" ]]; then
    fail "cannot read $source_file; none of the checks below would mean anything"
  fi
done

for module_dir in "${migrated_module_dirs[@]}"; do
  if [[ ! -d "$module_dir" ]]; then
    fail "cannot read $module_dir; none of the checks below would mean anything"
  fi
done

if ((failures > 0)); then
  exit 1
fi

# I8 (R1.3b): the registry reads the toggle and never writes it. The UI and the applied
# state are reconciled by reporting the state, not by rewriting the user's setting.
reject_source_pattern \
  "$registry_file" \
  "\\.Value\\s*=[^=]" \
  "the module registry must never assign a config value (R1.3b: the toggle is read only)"

# I8 (R1.4): a setting-changed handler is raised from the ConfigurationManager OnGUI pass
# and from a config file reload, so it may only record intent - and it is a game call
# stack, so it must also contain whatever it provokes.
require_setting_changed_handler_only_requests() {
  local body
  local offending

  body="$(extract_block "$registry_file" "SettingChanged \\+=" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must subscribe to the module toggle's SettingChanged event"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "RequestTransition\\("; then
    fail "the SettingChanged handler must hand the change to RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "the SettingChanged handler must contain its own failures; it runs on a game call stack"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|RequestTransition|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "the SettingChanged handler must request the transition and contain it, nothing else (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# The pump is called from UnityPlugin.Update. The transition callbacks are contained by the
# machine, but nothing else in the loop is, and an escape here reaches the update loop.
require_pump_contains_its_failures() {
  local body

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "Pump must contain its own failures; it runs from the update loop"
  fi
}

# I15 (R1.5a / R1.4): raising the stop request is the one structural thing that happens at
# request time, because the observable stop boundary has to be the frame the user asked
# for. Contain is allowed alongside it: containment is not structural work.
require_request_transition_shape() {
  local body
  local offending

  body="$(extract_block "$registry_file" "private static void RequestTransition" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "SetStopRequested\\(!enable\\)"; then
    fail "RequestTransition must raise and lower the stop request as SetStopRequested(!enable) (R1.5a)"
  fi

  if ! printf '%s\n' "$body" | rg -q "\\.Request\\(enable\\)"; then
    fail "RequestTransition must leave the structural transition to the pump via Request(enable)"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|SetStopRequested|Request|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "RequestTransition must do nothing but the stop request, the transition request and containment (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# I9 (R1.6 / R3.1): removing patches is the scope's business alone, and the always-on root
# patches have no removal path at all.
require_unpatch_calls_only_in_the_scope() {
  local files
  local status=0

  # The call syntax, not the name: the capability probe resolves the same API by
  # reflection, so the string "UnpatchSelf" legitimately appears in $lifecycle_file and
  # that file is excluded here rather than the check being weakened for everyone.
  files="$(
    rg -l --glob "!Automatics/Libraries/**" --glob "!$lifecycle_file" \
      "\\.UnpatchSelf\\(" Automatics
  )" || status=$?

  if ((status > 1)); then
    fail "could not search for UnpatchSelf call sites (rg exit $status); the check was not run"
    return
  fi

  if [[ "$files" != "$scope_file" ]]; then
    fail "UnpatchSelf() must be called from $scope_file and nowhere else (R3.1), found: ${files//$'\n'/ }"
  fi
}

reject_source_pattern \
  "$root_patches_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the always-on root patches must have no removal path (R3.1)"
reject_source_pattern \
  "$plugin_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the plugin entry point must not remove the always-on root patches (R3.1)"

# I9 (AC-S10): the transition layer stays offline-executable. Nothing that needs the engine,
# the patch library or the config framework may leak into it.
reject_source_pattern \
  "$machine_file" \
  "UnityEngine|HarmonyLib|BepInEx|ConfigEntry" \
  "the transition layer must not depend on the engine, Harmony, BepInEx or config entries (AC-S10)"

# I12 (MAJOR-1): Deferred asserts that this attempt left nothing behind. A rollback that
# failed must be reported as a failure, or the machine calls a module disabled while its
# patches and subscriptions are still live.
require_source_pattern \
  "$registry_file" \
  "return TransitionOutcome\\.Deferred" \
  "the module registry must be able to defer an activation (R4.5)"

require_deferred_returns_are_guarded() {
  local unguarded

  unguarded="$(
    awk '
      { lines[NR] = $0 }
      /return TransitionOutcome\.Deferred/ {
        start = NR - 4
        if (start < 1) start = 1
        guarded = 0
        for (i = start; i <= NR; i++)
          if (lines[i] ~ /TeardownIsClean/) guarded = 1
        if (!guarded) print NR
      }
    ' "$registry_file"
  )"

  if [[ -n "$unguarded" ]]; then
    fail "a deferred activation must be returned only after TeardownIsClean confirmed the rollback (MAJOR-1), unguarded at line(s): ${unguarded//$'\n'/ }"
  fi
}

# I13 (MAJOR-2): a scope whose teardown did not complete is the only handle on what is
# still installed. Both the failed-activation paths and the failed-teardown path keep it.
require_failed_scopes_are_kept() {
  local kept

  kept="$(count_matches "$registry_file" "Residual\\s*=\\s*scope")"
  if [[ "$kept" == "ERROR" ]]; then
    fail "could not count the scopes kept for recovery; the check was not run"
    return
  fi

  if ((kept < 3)); then
    fail "every failure path must keep its scope for recovery (MAJOR-2): expected at least 3 assignments of Residual, found $kept"
  fi
}

# I13 (MAJOR-2): and it is only let go where the teardown really finished.
require_residual_is_cleared_only_after_a_clean_teardown() {
  local total
  local in_recover
  local in_disable

  total="$(count_matches "$registry_file" "Residual\\s*=\\s*null")"
  in_recover="$(
    extract_block "$registry_file" "private static TransitionOutcome RecoverTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"
  in_disable="$(
    extract_block "$registry_file" "private static TransitionOutcome DisableTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"

  if [[ "$total" == "ERROR" || "$in_recover" == "ERROR" || "$in_disable" == "ERROR" ]]; then
    fail "could not count where the residual scope is released; the check was not run"
    return
  fi

  if ((in_recover < 1)); then
    fail "recovery must release the residual scope once its teardown completed (MAJOR-2)"
  fi

  if ((in_disable < 1)); then
    fail "a clean teardown must leave no residual scope behind (MAJOR-2)"
  fi

  if ((total != in_recover + in_disable)); then
    fail "the residual scope must be released only by recovery and by a clean teardown (MAJOR-2): $total assignments of null, $in_recover in recovery and $in_disable in the teardown"
  fi
}

# Recovery is a new attempt at the same teardown, so it has to repeat the work whose report
# it drops. Clearing without redoing turns an unfinished cleanup into a clean one.
require_recovery_retries_before_it_clears() {
  local body
  local disable_line
  local clear_line

  body="$(extract_block "$registry_file" "private static TransitionOutcome RecoverTransition")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RecoverTransition"
    return
  fi

  disable_line="$(printf '%s\n' "$body" | first_match_line "Module\\.Disable\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "ClearRecordedFailures\\(")"

  if lookup_failed "$disable_line" "$clear_line"; then
    fail "could not search for recovery ordering; the check was not run"
    return
  fi

  if [[ -z "$disable_line" || -z "$clear_line" ]]; then
    fail "recovery must redo the module's own teardown and only then drop the previous report"
    return
  fi

  if ((disable_line >= clear_line)); then
    fail "recovery must repeat the module teardown before it clears the report of the attempt that failed"
  fi
}

# I14 (MAJOR-7): the subscription token removes first and only marks itself spent once the
# removal returned. Clearing first makes a failed removal look done and loses the only
# handle on a subscription that is still live.
require_token_removes_before_it_clears() {
  local body
  local invoke_line
  local clear_line

  body="$(
    extract_block "$machine_file" "class SubscriptionToken<T>" |
      extract_block - "public void Dispose\\(\\)"
  )"
  if [[ -z "$body" ]]; then
    fail "SubscriptionToken<T> in $machine_file must define Dispose()"
    return
  fi

  invoke_line="$(printf '%s\n' "$body" | first_match_line "remove\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "_remove = null")"

  if lookup_failed "$invoke_line" "$clear_line"; then
    fail "could not search for the subscription token teardown order; the check was not run"
    return
  fi

  if [[ -z "$invoke_line" || -z "$clear_line" ]]; then
    fail "SubscriptionToken<T>.Dispose must both run the removal and mark the token spent"
    return
  fi

  if ((invoke_line >= clear_line)); then
    fail "SubscriptionToken<T>.Dispose must run the removal before it marks the token spent (MAJOR-7)"
  fi
}

# The scope acquires before it registers in only one place left - the component stamp - and
# everywhere else registers the undo first, so an acquisition that throws part way through
# still has a removal behind it.
require_scope_registers_before_it_applies() {
  local body
  local register_line
  local apply_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "harmony\\.PatchAll\\(")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "PatchAll must both apply the patches and register their removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "PatchAll must register the removal before it applies anything: a partial application must not be left without one"
  fi

  body="$(extract_block "$scope_file" "public IDisposable Subscribe<T>")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define Subscribe<T>"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "^\\s*add\\(handler\\)")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "Subscribe must both add the handler and register its removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "Subscribe must register the removal before it adds the handler: an add that subscribes and then throws must not be left without one"
  fi
}

# The pump's containment has to start at the list read, not after it, or the boundary the
# method advertises is not the boundary it has.
require_pump_containment_starts_at_the_top() {
  local body
  local try_line
  local read_line

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  try_line="$(printf '%s\n' "$body" | first_match_line "^\\s*try$")"
  read_line="$(printf '%s\n' "$body" | first_match_line "Entries\\[i\\]")"

  if lookup_failed "$try_line" "$read_line"; then
    fail "could not search for Pump's containment boundary; the check was not run"
    return
  fi

  if [[ -z "$try_line" || -z "$read_line" ]]; then
    fail "Pump must read each entry inside its containment"
    return
  fi

  if ((try_line >= read_line)); then
    fail "Pump's containment must start before it reads an entry, not after"
  fi
}

# One scope owns one Harmony id, and removing an id's patches removes all of them. A second
# registration would take the first call's patches off too, and at the wrong point in the
# reverse walk - before the subscriptions and components acquired after them.
require_patch_removal_registered_once() {
  local body
  local registrations
  local guard_line
  local register_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  registrations="$(printf '%s\n' "$body" | count_stdin_matches "RegisterOrRelease\\(")"
  if [[ "$registrations" == "ERROR" ]]; then
    fail "could not count the patch removal registrations; the check was not run"
    return
  fi

  if ((registrations != 1)); then
    fail "PatchAll must register exactly one removal for the whole scope (found $registrations): one Harmony id means one removal, at the position of the first call"
  fi

  guard_line="$(printf '%s\n' "$body" | first_match_line "_harmony == null")"
  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"

  if lookup_failed "$guard_line" "$register_line"; then
    fail "could not search for the patch removal registration; the check was not run"
    return
  fi

  if [[ -z "$guard_line" || -z "$register_line" ]]; then
    fail "PatchAll must register the scope's single patch removal behind a first-call guard"
    return
  fi

  if ((guard_line >= register_line)); then
    fail "PatchAll must guard the removal registration so that only the first call makes it"
  fi
}

# The refusal path has to give the same isolation the two ledger entries give: a hook that
# throws must not stop the destruction.
require_refusal_release_isolates_the_halves() {
  local body
  local detach_line
  local destroy_line
  local catch_line

  body="$(extract_block "$scope_file" "private void DetachThenDestroy")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refusal-time release for a component"
    return
  fi

  detach_line="$(printf '%s\n' "$body" | first_match_line "DetachTracked\\(")"
  destroy_line="$(printf '%s\n' "$body" | first_match_line "DestroyTracked\\(")"
  catch_line="$(printf '%s\n' "$body" | first_match_line "catch")"

  if lookup_failed "$detach_line" "$destroy_line" "$catch_line"; then
    fail "could not search for the refusal-time release; the check was not run"
    return
  fi

  if [[ -z "$detach_line" || -z "$destroy_line" ]]; then
    fail "the refusal-time release must run both the detach hook and the destruction"
    return
  fi

  if ((detach_line >= destroy_line)); then
    fail "the refusal-time release must run the detach hook before the destruction"
  fi

  if [[ -z "$catch_line" ]] || ((catch_line >= destroy_line)); then
    fail "the refusal-time release must isolate a throwing detach hook from the destruction"
  fi
}

# An undo is arbitrary code and may add an obligation, so a list position taken before it
# means nothing afterwards. This is the same hazard that was removed from the component
# bookkeeping; the orphan walk must not reintroduce it.
require_orphan_walk_is_identity_based() {
  local body

  body="$(extract_block "$scope_file" "private void DisposeOrphans")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refused-obligation walk"
    return
  fi

  if printf '%s\n' "$body" | rg -q "RemoveAt\\(i\\)"; then
    fail "the refused-obligation walk must not hold a list position across an undo: resolve the completed entry by identity"
  fi

  if ! printf '%s\n' "$body" | rg -q "RemoveOrphan\\("; then
    fail "the refused-obligation walk must remove a completed obligation by identity"
  fi
}

# ---------------------------------------------------------------------------------------
# Pilot module: Automatic Pickup. Everything below checks the shape of a migrated module.
# ---------------------------------------------------------------------------------------

# I10 (BLOCKER): the highest-value check in this slice. A coroutine resumes at the yield it
# suspended on, so a guard at the head of its loop is never reached by an iterator that was
# suspended half way through a pass: it resumes straight into the next pickup. Every yield
# therefore has to be followed by its own guard, before the next side effect.
#
# The split is deliberate and this check enforces both halves. `if (!Alive) yield break;`
# handles the irreversible conditions - the module is off, the component is detached, the
# generation moved on - and folds the iterator, because a re-activation builds a new
# component and this one must not linger. `if (!Live) continue;` handles the reversible ones
# - a stop request that can be lowered again, ownership that can come back - and goes back
# to idling, because folding on those would kill pickup on a component that survives them.
#
# What this does not check is that Alive and Live look at the right things: `Alive => true`
# passes. That half is AC-R-P2, and it needs the game.
require_every_yield_is_guarded() {
  local body
  local unguarded
  local yields
  local breaks
  local continues

  body="$(extract_block "$pickup_component" "private IEnumerator Pickup\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup component must define the Pickup() coroutine"
    return
  fi

  unguarded="$(
    printf '%s\n' "$body" | awk '
      { lines[NR] = $0 }
      END {
        for (i = 1; i <= NR; i++) {
          if (lines[i] !~ /yield return/) continue

          guard = "<the coroutine body ends here>"
          for (j = i + 1; j <= NR; j++) {
            candidate = lines[j]
            gsub(/^[ \t]+|[ \t]+$/, "", candidate)
            if (candidate == "") continue
            if (candidate ~ /^\/\//) continue
            guard = candidate
            break
          }

          if (guard != "if (!Alive) yield break;") print i ": " guard
        }
      }
    '
  )"

  if [[ -n "$unguarded" ]]; then
    fail "every yield in Pickup() must be followed by \"if (!Alive) yield break;\" before the next side effect (BLOCKER): a coroutine resumes where it suspended, not at the head of its loop; offending yield(s): ${unguarded//$'\n'/ | }"
  fi

  yields="$(printf '%s\n' "$body" | count_stdin_matches "yield return")"
  breaks="$(printf '%s\n' "$body" | count_stdin_matches "if \\(!Alive\\) yield break;")"
  continues="$(printf '%s\n' "$body" | count_stdin_matches "if \\(!Live\\) continue;")"

  if lookup_failed "$yields" "$breaks" "$continues"; then
    fail "could not count the guards in Pickup(); the check was not run"
    return
  fi

  if ((yields != 5)); then
    fail "Pickup() must suspend at exactly the 5 known points (BLOCKER): found $yields; a new one needs its own guard and this count updated deliberately"
  fi

  if ((breaks != yields)); then
    fail "Pickup() must fold the iterator after every yield (BLOCKER): $yields yields but $breaks occurrences of \"if (!Alive) yield break;\""
  fi

  if ((continues != 4)); then
    fail "Pickup() must idle rather than fold on the reversible conditions after each of the 4 mid-pass yields (R1.5a / MAJOR-6): found $continues occurrences of \"if (!Live) continue;\""
  fi

  # The idle wait is the fifth yield and the one the loop head owns. Its guard is Alive only,
  # so the loop condition is what keeps a stale _active - raised up to a tenth of a second
  # before this loop reads it - from driving a pass that starts after a stop request.
  if ! printf '%s\n' "$body" | rg -q "while \\(!_active \\|\\| !Live\\)"; then
    fail "the idle loop of Pickup() must wait on Live as well as _active (R1.5a): _active is raised frames earlier, and entering the body on a stale one produces a pickup after the stop was requested"
  fi
}

# I10 sibling (P1): Alive and Live answer "is the module still on", not "is the object I
# captured before the suspension still there". The hover target is read once and then
# dereferenced after two frame boundaries; if it is picked or destroyed in between, the
# dereference raises MissingReferenceException outside the per-object handlers, the iterator
# dies, and pickup stops silently for that player until the module is toggled. So the rule is
# positional: every dereference of the captured target must have a revalidation after the
# most recent suspension.
require_the_hover_target_is_revalidated() {
  local body
  local stale

  body="$(extract_block "$pickup_component" "private IEnumerator Pickup\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup component must define the Pickup() coroutine"
    return
  fi

  stale="$(
    printf '%s\n' "$body" | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line ~ /^var hovering =/ { validated = 0; next }
      line ~ /yield return/ { validated = 0; next }
      line ~ /^if \(!hovering\) continue;$/ { validated = 1; next }
      line ~ /hovering\./ {
        if (!validated) print NR ": " line
      }
    '
  )"

  if [[ -n "$stale" ]]; then
    fail "the hovered object must be revalidated after every suspension before it is dereferenced (P1): a target destroyed while the iterator was parked raises MissingReferenceException outside the per-object handlers and kills the coroutine; unguarded use at: ${stale//$'\n'/ | }"
  fi
}

# BLOCKER(c): the reason the guards above exist, written where the next person to tidy them
# away will read it.
require_the_resume_point_is_documented() {
  local preamble

  preamble="$(
    awk '
      /private IEnumerator Pickup\(\)/ { exit }
      { print }
    ' "$pickup_component" | tail -20
  )"

  if ! printf '%s\n' "$preamble" | rg -q "resumes at the yield"; then
    fail "Pickup() must be preceded by the note that a coroutine resumes at the yield it suspended on, not at the head of its loop (BLOCKER): without it the per-yield guards read as redundant"
  fi
}

# BLOCKER(a): destruction is deferred to the end of the frame, and OnDestroy with it, so the
# coroutine has to be stopped synchronously from the teardown instead. Neither shutdown may
# be conditioned on ownership: the coroutine now runs on non-owners too, and the previous
# ownership guard on OnDestroy was already leaving coroutines running on Players whose
# ownership had moved.
require_synchronous_coroutine_shutdown() {
  local hook
  local destroy

  hook="$(extract_block "$pickup_component" "public void OnScopeDetaching\\(\\)")"
  destroy="$(extract_block "$pickup_component" "private void OnDestroy\\(\\)")"

  if [[ -z "$hook" ]]; then
    fail "the pickup component must implement OnScopeDetaching() so the scope can stop its coroutine before it destroys it (BLOCKER)"
  else
    if ! printf '%s\n' "$hook" | rg -q "StopCoroutine\\("; then
      fail "OnScopeDetaching() must stop the pickup coroutine synchronously (BLOCKER): engine destruction is deferred to the end of the frame"
    fi
    if ! printf '%s\n' "$hook" | rg -q "_cancelled = true"; then
      fail "OnScopeDetaching() must mark the component cancelled so an iterator already resumed this frame folds at its next guard (BLOCKER)"
    fi
    if printf '%s\n' "$hook" | rg -q "IsOwner\\("; then
      fail "OnScopeDetaching() must not condition the shutdown on ownership (BLOCKER): the coroutine runs on non-owners, so stopping it cannot depend on ownership"
    fi
  fi

  if [[ -z "$destroy" ]]; then
    fail "the pickup component must define OnDestroy()"
    return
  fi

  if ! printf '%s\n' "$destroy" | rg -q "StopCoroutine\\("; then
    fail "OnDestroy() must stop the pickup coroutine for the destructions the scope does not drive"
  fi

  if printf '%s\n' "$destroy" | rg -q "IsOwner\\("; then
    fail "OnDestroy() must not condition the shutdown on ownership (BLOCKER): a Player whose ownership moved would keep its coroutine running"
  fi
}

# FIX 3: Unity stops a component's coroutines when its GameObject is deactivated and does not
# call Awake again on the way back, so an iterator started in Awake never returns: Update goes
# on raising _active with nothing to consume it. The activation scan includes inactive objects
# by design, so this is a path the pilot really has.
require_the_coroutine_starts_on_enable() {
  local awake
  local enable
  local disable

  awake="$(extract_block "$pickup_component" "private void Awake\\(\\)")"
  enable="$(extract_block "$pickup_component" "private void OnEnable\\(\\)")"
  disable="$(extract_block "$pickup_component" "private void OnDisable\\(\\)")"

  if [[ -z "$awake" ]]; then
    fail "the pickup component must define Awake()"
    return
  fi

  if printf '%s\n' "$awake" | rg -q "StartCoroutine\\("; then
    fail "Awake() must not start the pickup coroutine: a component whose GameObject is deactivated loses its coroutine and never gets another Awake, so it would come back with Update raising a flag nothing consumes"
  fi

  if [[ -z "$enable" ]]; then
    fail "the pickup component must define OnEnable() so a reactivated component starts a coroutine again"
    return
  fi

  if ! printf '%s\n' "$enable" | rg -q "StartCoroutine\\("; then
    fail "OnEnable() must start the pickup coroutine"
  fi

  if ! printf '%s\n' "$enable" | rg -q "if \\(_running\\) return;"; then
    fail "OnEnable() must refuse to start a second iterator beside a running one: disabling the component rather than the GameObject raises OnEnable again without the engine having stopped anything"
  fi

  if ! printf '%s\n' "$enable" | rg -q "if \\(_cancelled\\) return;"; then
    fail "OnEnable() must not restart a component whose activation is over"
  fi

  if [[ -z "$disable" ]]; then
    fail "the pickup component must define OnDisable()"
    return
  fi

  if ! printf '%s\n' "$disable" | rg -q "StopCoroutine\\("; then
    fail "OnDisable() must stop the coroutine itself: deactivating the GameObject stops it but disabling the component alone does not, and OnEnable can only restart exactly one iterator if both leave the same state"
  fi
}

# MAJOR-6: ownership decided once in Awake froze whatever was true at attach time, which
# runtime activation makes worse - the reconciliation pass attaches to every Player at one
# instant. It moved to the per-frame predicate instead.
require_ownership_is_a_per_frame_question() {
  local awake
  local enable

  awake="$(extract_block "$pickup_component" "private void Awake\\(\\)")"
  enable="$(extract_block "$pickup_component" "private void OnEnable\\(\\)")"
  if [[ -z "$awake" || -z "$enable" ]]; then
    fail "the pickup component must define Awake() and OnEnable()"
    return
  fi

  if printf '%s\n' "$awake$enable" | rg -q "IsOwner\\("; then
    fail "the coroutine must be started whatever the ownership is (MAJOR-6): deciding once at attach time leaves a Player that gains ownership later with no coroutine at all"
  fi

  require_source_pattern \
    "$pickup_component" \
    "private bool Live =>" \
    "the pickup component must have a per-frame Live predicate (MAJOR-6 / R1.5a)"
  require_source_pattern \
    "$pickup_component" \
    "_player\\.IsOwner\\(\\)" \
    "Live must be the place ownership is checked (MAJOR-6)"
  require_source_pattern \
    "$pickup_component" \
    "Module\\.StopRequested" \
    "Live must carry the stop request so the observable stop boundary is the request (R1.5a)"

  local live

  live="$(extract_expression_body "$pickup_component" "private bool Live =>")"
  if [[ -z "$live" ]]; then
    fail "the pickup component must have a Live predicate"
    return
  fi

  if ! printf '%s\n' "$live" | rg -q "IsPaused\\(\\)"; then
    fail "Live must refuse a paused game: Update and FixedUpdate already decline to raise the flag while paused, but a pass suspended at a yield resumes and picks unless the check is in the per-frame predicate too"
  fi

  # The reversible half belongs to Live alone. In Alive it would fold the iterator of a
  # component that survives a disable-then-re-enable inside one frame, killing pickup on it
  # permanently.
  local alive
  local misplaced

  alive="$(extract_expression_body "$pickup_component" "private bool Alive =>")"
  if [[ -z "$alive" ]]; then
    fail "the pickup component must have an Alive predicate for the irreversible conditions"
    return
  fi

  misplaced="$(printf '%s\n' "$alive" | count_stdin_matches "StopRequested")"
  if lookup_failed "$misplaced"; then
    fail "could not search the Alive predicate; the check was not run"
    return
  fi

  if ((misplaced != 0)); then
    fail "the stop request belongs in Live, not Alive (R1.5a): it is reversible, and folding the iterator on it kills pickup on a component that survives the request"
  fi
}

# Both per-frame entry points raise _active, so both have to answer the same question the
# coroutine does before they do.
require_the_frame_hooks_check_live() {
  local hook
  local body

  for hook in "private void Update\\(\\)" "private void FixedUpdate\\(\\)"; do
    body="$(extract_block "$pickup_component" "$hook")"
    if [[ -z "$body" ]]; then
      fail "the pickup component must define ${hook//\\/}"
      continue
    fi

    if ! printf '%s\n' "$body" | tail -n +2 | head -3 | rg -q "if \\(!Live\\) return;"; then
      fail "${hook//\\/} must open with \"if (!Live) return;\": it raises the flag the coroutine acts on, so a check anywhere later lets a stopped module queue one more pass"
    fi
  done
}

# The static buffers outlive every component that filled them, so a disable that leaves them
# populated keeps destroyed objects reachable. EmptyItemDrops is exempt by contract.
require_shared_buffers_are_cleared_on_disable() {
  local body

  require_source_pattern \
    "$pickup_component" \
    "internal static void ClearSharedBuffers\\(\\)" \
    "the pickup component must expose ClearSharedBuffers() for its module to call on disable"

  body="$(extract_block "$pickup_module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup module must define Disable(ModuleScope)"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "ClearSharedBuffers\\(\\)"; then
    fail "Disable must clear the shared pickup buffers: they are static and would keep destroyed objects reachable across a disable"
  fi

  local sentinel

  sentinel="$(
    extract_block "$pickup_component" "internal static void ClearSharedBuffers\\(\\)" |
      sed -e 's://.*::' | count_stdin_matches "EmptyItemDrops"
  )"
  if lookup_failed "$sentinel"; then
    fail "could not search ClearSharedBuffers; the check was not run"
    return
  fi

  if ((sentinel != 0)); then
    fail "ClearSharedBuffers must leave EmptyItemDrops alone: it is the empty sentinel GetAllItemDrop falls back to"
  fi
}

# I1 (AC-S4, slice scoped): a migrated module binds its settings whether it starts on or
# off, because it can be turned on later in the same session.
require_migrated_modules_bind_unconditionally() {
  local module_dir
  local config

  for module_dir in "${migrated_module_dirs[@]}"; do
    config="$module_dir/Config.cs"
    if [[ ! -r "$config" ]]; then
      fail "cannot read $config; the unconditional-bind check was not run"
      continue
    fi

    reject_source_pattern \
      "$config" \
      "AutomaticsModule\\.Disabled\\) return" \
      "$config must bind its settings unconditionally (R5.1): a module that can be turned on at runtime cannot leave its entries null"
  done
}

# The same removal must not spread. Until a module is migrated its early return is what
# keeps this slice from handing inactive-guard duty to code it never verifies (R5.1a).
require_unmigrated_modules_keep_their_early_return() {
  local config

  for config in "${unmigrated_module_configs[@]}"; do
    require_source_pattern \
      "$config" \
      "AutomaticsModule\\.Disabled\\) return" \
      "$config must keep its early return until that module is migrated (R5.1a): removing it here binds and wires a module this slice does not verify"
  done
}

# I2 / I3 (AC-S5, R2.4): a migrated module owns no subscription directly. Every add has to be
# half of a scope.Subscribe pair, or the teardown has nothing to remove. The lambda form is
# what makes the pair; a bare `Hooks.X += Handler;` statement is the defect shape.
require_event_wiring_goes_through_the_scope() {
  local module_dir
  local pattern="$1"
  local what="$2"
  local total
  local scoped
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      total="$(count_matches "$file" "$pattern\\s*(\\+|-)=")"
      scoped="$(count_matches "$file" "h => $pattern\\s*(\\+|-)= h")"

      if lookup_failed "$total" "$scoped"; then
        fail "could not search $file for $what wiring; the check was not run"
        continue
      fi

      if ((total != scoped)); then
        fail "$file must wire $what through scope.Subscribe only (AC-S5): $total add/remove sites, $scoped of them half of a Subscribe pair"
      fi
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# I4 (R3.1): patches are applied through the scope, which is what registers their removal.
# A direct CreateAndPatchAll leaves patches nothing can take off.
require_patching_goes_through_the_scope() {
  local module_dir
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "Harmony\\.CreateAndPatchAll" \
        "$file must apply its patches through scope.PatchAll (R3.1): a direct CreateAndPatchAll registers no removal"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# I11 (MAJOR-4): three paths create components - the activation scan, the Player.Awake hook
# and the PickableItem.Awake postfix. A component created outside the scope has no owner, so
# the disable leaves it in the scene and the counts never come back to baseline.
require_components_are_created_through_the_scope() {
  local module_dir
  local file
  local count

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "AddComponent<" \
        "$file must create components through scope.Attach or scope.TryAttach (MAJOR-4): AddComponent leaves the component without an owner to destroy it"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done

  for count in \
    "$pickup_module|TryAttach<AutomaticPickup>|1|the Player.Awake hook must attach through the scope" \
    "$pickup_module|TryAttach<PickableItemCache>|1|the cache attach path must go through the scope" \
    "$pickup_patches|Module\\.AttachCache|1|the PickableItem.Awake postfix must attach through the module's single entry point"; do
    IFS='|' read -r file pattern expected message <<<"$count"
    local found
    found="$(count_matches "$file" "$pattern")"
    if lookup_failed "$found"; then
      fail "could not count $pattern in $file; the check was not run"
      continue
    fi
    if ((found != expected)); then
      fail "$message (MAJOR-4): expected $expected occurrence(s) of $pattern in $file, found $found"
    fi
  done
}

# MAJOR-5: both attach paths run on a game call stack and can still be reached after the
# module stopped - a removal that failed, or an invocation list a caller already fetched. They
# gate on Discovering rather than Running on purpose (FIX 2): a stop request must not suppress
# discovery, because an object dropped in the frame between the request and the pump is never
# recovered if the user withdraws the request before the pump runs - no transition means no
# reconciliation, ever. Attaching under a stop request is harmless; the component's own Live
# gate stops it acting, and a disable that goes ahead destroys it with the rest of the scope.
require_attach_paths_recheck_the_effective_state() {
  local entry
  local body
  local predicate

  for entry in "private static void OnPlayerAwake" "internal static void AttachCache"; do
    body="$(extract_block "$pickup_module" "$entry")"
    if [[ -z "$body" ]]; then
      fail "the pickup module must define ${entry##* }"
      continue
    fi

    if ! printf '%s\n' "$body" | rg -q "if \\(!Discovering\\) return;"; then
      fail "${entry##* } must open with \"if (!Discovering) return;\" (MAJOR-5 / FIX 2): it is reachable after the module stopped, and gating it on Running instead would drop objects that nothing ever comes back for"
    fi
  done

  predicate="$(extract_expression_body "$pickup_module" "internal static bool Discovering =>")"
  if [[ -z "$predicate" ]]; then
    fail "the pickup module must define the Discovering predicate (FIX 2)"
    return
  fi

  if ! printf '%s\n' "$predicate" | rg -q "Active && CurrentScope != null"; then
    fail "Discovering must be exactly the applied state and the scope (FIX 2): Active && CurrentScope != null"
  fi

  if printf '%s\n' "$predicate" | rg -q "StopRequested|Running"; then
    fail "Discovering must not carry the stop request (FIX 2): discovery and side effects are different questions, and sharing Running's predicate answers one of them wrongly"
  fi
}

# I5 (R1.5 / R1.5a): every patch is gated, and gated on Running - which carries the stop
# request - rather than on the applied flag alone. Counting is not enough on its own, so the
# gate is also required to appear once between one patch attribute and the next.
require_every_patch_is_gated() {
  local targets
  local acting
  local discovering
  local bypass
  local ungated
  local misplaced

  targets="$(count_matches "$pickup_patches" "\\[HarmonyPatch\\(typeof\\(")"
  acting="$(count_matches "$pickup_patches" "Module\\.Running")"
  discovering="$(count_matches "$pickup_patches" "Module\\.Discovering")"
  bypass="$(count_matches "$pickup_patches" "Module\\.Active")"

  if lookup_failed "$targets" "$acting" "$discovering" "$bypass"; then
    fail "could not count the patch gates; the check was not run"
    return
  fi

  if ((targets != 5)); then
    fail "the pickup module must patch exactly the 5 audited targets: found $targets; adding or removing one needs the liveness audit of specification 1.4 re-run"
  fi

  if ((acting + discovering != targets)); then
    fail "every patch must open with the module's effective-state gate (R1.5): $targets patches, $acting occurrences of Module.Running and $discovering of Module.Discovering"
  fi

  if ((discovering != 1)); then
    fail "exactly one patch is a discovery path and may use the discovery gate (FIX 2): found $discovering occurrences of Module.Discovering"
  fi

  if ((bypass != 0)); then
    fail "a patch that produces side effects must read Module.Running, never Module.Active (R1.5a): Active alone lets it keep acting between the disable request and the pump that removes it"
  fi

  ungated="$(
    awk '
      /\[HarmonyPatch\(typeof\(/ {
        if (open && seen != 1) print target ": " seen " gate(s)"
        open = 1; seen = 0; target = $0
        gsub(/^[ \t]+|[ \t]+$/, "", target)
        next
      }
      /Module\.(Running|Discovering)/ { if (open) seen++ }
      END { if (open && seen != 1) print target ": " seen " gate(s)" }
    ' "$pickup_patches"
  )"

  if [[ -n "$ungated" ]]; then
    fail "each patch must carry exactly one module gate of its own (R1.5): ${ungated//$'\n'/ | }"
  fi

  # And the discovery gate belongs to the discovery patch, not to one that picks things up.
  misplaced="$(
    awk '
      /\[HarmonyPatch\(typeof\(/ {
        target = $0
        gsub(/^[ \t]+|[ \t]+$/, "", target)
        next
      }
      /Module\.Discovering/ {
        if (target != "[HarmonyPatch(typeof(PickableItem), \"Awake\")]") print target
      }
    ' "$pickup_patches"
  )"

  if [[ -n "$misplaced" ]]; then
    fail "only the PickableItem.Awake postfix may use the discovery gate (FIX 2): every other patch produces side effects and must stop at the stop request; found it on ${misplaced//$'\n'/ | }"
  fi
}

# I6 (R4.3 / R1.6): the scope registers its single patch removal at the first PatchAll and
# undoes registrations in reverse, so acquiring patches first is what puts their removal
# last. Subscribing or attaching before the first PatchAll silently moves the patch removal
# ahead of them. The framework cannot detect it; this is where it is caught.
require_enable_acquires_in_order() {
  local body
  local scope_line
  local patch_line
  local subscribe_line
  local reconcile_line
  local last

  body="$(extract_block "$pickup_module" "public void Enable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup module must define Enable(ModuleScope)"
    return
  fi

  scope_line="$(printf '%s\n' "$body" | first_match_line "CurrentScope = scope;")"
  patch_line="$(printf '%s\n' "$body" | first_match_line "scope\\.PatchAll\\(")"
  subscribe_line="$(printf '%s\n' "$body" | first_match_line "scope\\.Subscribe<")"
  reconcile_line="$(printf '%s\n' "$body" | first_match_line "Reconcile[A-Za-z]*\\(")"

  if lookup_failed "$scope_line" "$patch_line" "$subscribe_line" "$reconcile_line"; then
    fail "could not search the acquisition order of Enable; the check was not run"
    return
  fi

  if [[ -z "$scope_line" || -z "$patch_line" || -z "$subscribe_line" || -z "$reconcile_line" ]]; then
    fail "Enable must publish the scope, apply its patches, subscribe and then reconcile the scene"
    return
  fi

  if ((scope_line >= patch_line)); then
    fail "Enable must publish CurrentScope before it acquires anything (MAJOR-4): a component created while the activation runs needs an owner already"
  fi

  if ((patch_line >= subscribe_line)); then
    fail "Enable must apply its patches before it subscribes (R4.3): the scope registers one patch removal at the first PatchAll, and a subscription taken first has its removal run after the patches come off"
  fi

  if ((subscribe_line >= reconcile_line)); then
    fail "Enable must subscribe before it reconciles the scene (R4.3 / R4.2): a producer installed after the scan misses everything that appears in between"
  fi

  last="$(
    printf '%s\n' "$body" | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line == "" { next }
      line ~ /^\/\// { next }
      line ~ /^\/\/\// { next }
      line == "}" { next }
      { keep = line }
      END { print keep }
    '
  )"

  if [[ "$last" != "Active = true;" ]]; then
    fail "the last statement of Enable must be \"Active = true;\" (R1.3 / R1.5): anything after it runs with the module already advertising itself as running, and a throw before it must leave the module inactive; found \"$last\""
  fi
}

require_disable_lowers_the_flag_first() {
  local body
  local first

  body="$(extract_block "$pickup_module" "public void Disable\\(ModuleScope scope\\)")"
  if [[ -z "$body" ]]; then
    fail "the pickup module must define Disable(ModuleScope)"
    return
  fi

  first="$(
    printf '%s\n' "$body" | tail -n +2 | awk '
      { line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line) }
      line == "" { next }
      line ~ /^\/\// { next }
      line == "{" { next }
      { print line; exit }
    '
  )"

  if [[ "$first" != "Active = false;" ]]; then
    fail "the first statement of Disable must be \"Active = false;\" (R1.5): every hot path has to be shut before anything is dismantled; found \"$first\""
  fi

  if ! printf '%s\n' "$body" | rg -q "CurrentScope = null;"; then
    fail "Disable must close the attach paths by clearing CurrentScope (MAJOR-4)"
  fi

  if ! printf '%s\n' "$body" | rg -q "Generation = 0;"; then
    fail "Disable must reset the generation to the reserved zero (R4.5): a component left over from this activation has to stop matching"
  fi
}

# I7 (R4.2): the cache this module reconciles is filled by its own patch, so it is empty for
# everything that spawned while the module was off. Reading it back would reconcile against
# nothing; the scan has to enumerate the scene itself, inactive objects included.
require_reconciliation_enumerates_the_scene() {
  require_source_pattern \
    "$pickup_module" \
    "FindObjectsByType<PickableItem>\\(\\s*$" \
    "the activation scan must enumerate PickableItem itself (R4.2)"
  require_source_pattern \
    "$pickup_module" \
    "FindObjectsByType<Player>\\(\\s*$" \
    "the activation scan must enumerate Player itself (R4.2)"

  local including
  local deprecated

  including="$(count_stripped_matches "$pickup_module" "FindObjectsInactive\\.Include")"
  deprecated="$(count_stripped_matches "$pickup_module" "FindObjectsOfType")"

  if lookup_failed "$including" "$deprecated"; then
    fail "could not search the enumeration calls; the check was not run"
    return
  fi

  if ((including != 2)); then
    fail "both activation scans must ask for inactive objects (R4.2): FindObjectsInactive.Include appears $including time(s), expected 2; without it a re-enable misses everything parked under a deactivated root"
  fi

  if ((deprecated != 0)); then
    fail "the activation scan must use FindObjectsByType, not the deprecated FindObjectsOfType (build quality): the newer API exists in the shipped Unity and needs no warning suppression"
  fi

  local refill
  local roster

  refill="$(count_stripped_matches "$pickup_module" "PickableItemCache\\.Fill")"
  roster="$(count_stripped_matches "$pickup_module" "GetAllPlayers\\(")"

  if lookup_failed "$refill" "$roster"; then
    fail "could not search the activation scan; the check was not run"
    return
  fi

  if ((refill != 0)); then
    fail "the activation scan must not read the cache back (R4.2): this module's own patch fills it, so it holds nothing that appeared while the module was off"
  fi

  if ((roster != 0)); then
    fail "the activation scan must not use Player.GetAllPlayers (R4.2): it lists the players the game considers alive, not the objects that ran Player.Awake"
  fi
}

# Build quality: the build carries zero warnings, and it has to do so by not producing them.
# A suppression buys the same green result while leaving the thing the compiler objected to in
# place, so a migrated module is not allowed to hold one.
require_no_warning_suppressions() {
  local module_dir
  local file

  for module_dir in "${migrated_module_dirs[@]}"; do
    while IFS= read -r file; do
      reject_source_pattern \
        "$file" \
        "#pragma warning disable" \
        "$file must not suppress a compiler warning: the build is warning-free because the code is, not because the objection was silenced"
    done < <(find "$module_dir" -name '*.cs' -type f | sort)
  done
}

# R5.5 / AC-S7: this slice does not change the default. A module that starts Disabled for
# everyone is a separate decision, taken after the framework has been demonstrated in game.
require_the_default_is_unchanged() {
  require_source_pattern \
    "$pickup_config" \
    "config\\.Bind\\(\"module\", AutomaticsModule\\.Enabled" \
    "the module default must stay Enabled in this slice (R5.5 / AC-S7): changing it needs the runtime toggle demonstrated first"
}

# R9.1 / R9.2: a migrated module needs its own toggle strings. The shared ones still say
# "Reboot Required" and are still correct for the eight modules that have not moved.
require_the_runtime_toggle_strings_exist() {
  local language_file
  local key

  require_source_pattern \
    "$pickup_config" \
    "@config_common_disable_module_runtime_name" \
    "the migrated module must use the runtime toggle name, not the one that says a restart is required (R9.1)"
  require_source_pattern \
    "$pickup_config" \
    "@config_common_disable_module_runtime_description" \
    "the migrated module must use the runtime toggle description (R9.1)"

  for language_file in Automatics/Languages/en_us.json Automatics/Languages/ja_jp.json; do
    if [[ ! -r "$language_file" ]]; then
      fail "cannot read $language_file; the translation check was not run"
      continue
    fi

    for key in \
      "@config_common_disable_module_runtime_name" \
      "@config_common_disable_module_runtime_description" \
      "@config_common_disable_module_name" \
      "@config_common_disable_module_description"; do
      require_source_pattern \
        "$language_file" \
        "\"$key\"" \
        "$language_file must define $key (R9.2): the two key sets have to stay in step, and the eight unmigrated modules still use the restart wording"
    done
  done
}

require_setting_changed_handler_only_requests
require_pump_contains_its_failures
require_pump_containment_starts_at_the_top
require_request_transition_shape
require_unpatch_calls_only_in_the_scope
require_deferred_returns_are_guarded
require_failed_scopes_are_kept
require_residual_is_cleared_only_after_a_clean_teardown
require_recovery_retries_before_it_clears
require_token_removes_before_it_clears
require_scope_registers_before_it_applies
require_patch_removal_registered_once
require_refusal_release_isolates_the_halves
require_orphan_walk_is_identity_based

require_every_yield_is_guarded
require_the_hover_target_is_revalidated
require_the_resume_point_is_documented
require_synchronous_coroutine_shutdown
require_the_coroutine_starts_on_enable
require_ownership_is_a_per_frame_question
require_the_frame_hooks_check_live
require_shared_buffers_are_cleared_on_disable
require_migrated_modules_bind_unconditionally
require_unmigrated_modules_keep_their_early_return
require_event_wiring_goes_through_the_scope "Hooks\\.[A-Za-z]+" "hook subscriptions"
require_event_wiring_goes_through_the_scope "OnCacheAdded" "cache-added subscriptions"
require_event_wiring_goes_through_the_scope "OnCacheRemoved" "cache-removed subscriptions"
require_patching_goes_through_the_scope
require_components_are_created_through_the_scope
require_attach_paths_recheck_the_effective_state
require_every_patch_is_gated
require_enable_acquires_in_order
require_disable_lowers_the_flag_first
require_reconciliation_enumerates_the_scene
require_no_warning_suppressions
require_the_default_is_unchanged
require_the_runtime_toggle_strings_exist

if ((failures > 0)); then
  exit 1
fi

printf 'PASS module lifecycle static invariant checks\n'
